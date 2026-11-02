using System.Threading.Channels;
using BackendV2.Data;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Ingestion;
using BackendV2.Gateway.Realtime;
using BackendV2.Gateway.Uplink;
using BackendV2.Models;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Gateway.Alerts;

public abstract record AlertEvent;

/// <summary>Mức đã lọc dao động của một cảm biến thay đổi (xem <see cref="SensorLevelTracker"/>).</summary>
public sealed record SensorLevelChanged(
    string SensorId, string NodeId, int LineId, byte NodeByteId, string Kind, byte Ordinal,
    ReadingLevel Level, double Value, double? Threshold, DateTimeOffset At) : AlertEvent;

public sealed record LivenessChanged(HealthTransition Transition) : AlertEvent;

internal sealed record OperatorRequest(Func<Task<object?>> Work, TaskCompletionSource<object?> Done) : AlertEvent;

public sealed record ActiveAlertInfo(Guid Id, int? LineId, AlertSeverity Severity);

public sealed record OpenIncidentInfo(Guid Id, int LineId, AlertSeverity Severity);

public sealed record AlertSnapshot(IReadOnlyList<ActiveAlertInfo> Alerts, IReadOnlyList<OpenIncidentInfo> Incidents)
{
    public static readonly AlertSnapshot Empty = new([], []);
}

/// <summary>
/// Vòng đời cảnh báo và tình huống. Mọi sự kiện (từ cảm biến, sống/chết thiết bị, thao tác operator)
/// đi qua một hàng đợi và được xử lý tuần tự bởi một luồng duy nhất, nên trạng thái trong bộ nhớ không cần khóa.
/// </summary>
public sealed class AlertEngine : BackgroundService
{
    public const string SystemActor = "system";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RealtimeNotifier _notifier;
    private readonly UplinkPublisher _uplink;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertEngine> _logger;
    private readonly Channel<AlertEvent> _events = Channel.CreateUnbounded<AlertEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Alert> _activeByKey = new();
    private readonly Dictionary<Guid, Incident> _openIncidents = new();
    private readonly Dictionary<Guid, List<Guid>> _incidentAlerts = new();

    // Bản ghi chưa từng ghi được vào CSDL: lần ghi sau phải Insert chứ không Update.
    private readonly HashSet<Guid> _unsavedAlerts = new();
    private readonly HashSet<Guid> _unsavedIncidents = new();
    private readonly SensorLevelTracker _levels;
    private volatile AlertSnapshot _snapshot = AlertSnapshot.Empty;

    public AlertEngine(
        IServiceScopeFactory scopeFactory, RealtimeNotifier notifier, UplinkPublisher uplink, SensorLevelTracker levels,
        TimeProvider time, ILogger<AlertEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _levels = levels;
        _notifier = notifier;
        _uplink = uplink;
        _time = time;
        _logger = logger;
    }

    public AlertSnapshot Snapshot => _snapshot;

    public void Post(AlertEvent e) => _events.Writer.TryWrite(e);

    public Task<Alert?> AcknowledgeAsync(Guid alertId, string actor) =>
        RunAsync(() => ChangeAlertAsync(alertId, AlertState.Acknowledged, actor));

    public Task<Alert?> ResolveAsync(Guid alertId, string actor) =>
        RunAsync(() => ChangeAlertAsync(alertId, AlertState.Resolved, actor));

    public Task<Incident?> SetIncidentStateAsync(Guid incidentId, IncidentState state, string actor, string? note) =>
        RunAsync(() => ChangeIncidentAsync(incidentId, state, actor, note));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var alert in await db.Alerts.AsNoTracking().Where(a => a.State != AlertState.Resolved).ToListAsync(cancellationToken))
            {
                _activeByKey[alert.Key] = alert;
            }

            foreach (var incident in await db.Incidents.AsNoTracking().Where(i => i.State != IncidentState.Closed).ToListAsync(cancellationToken))
            {
                _openIncidents[incident.Id] = incident;
            }

            var openIds = _openIncidents.Keys.ToList();
            var members = await db.Alerts.AsNoTracking()
                .Where(a => a.IncidentId != null && openIds.Contains(a.IncidentId.Value))
                .Select(a => new { a.Id, IncidentId = a.IncidentId!.Value })
                .ToListAsync(cancellationToken);
            foreach (var group in members.GroupBy(m => m.IncidentId))
            {
                _incidentAlerts[group.Key] = group.Select(m => m.Id).ToList();
            }
        }

        RefreshSnapshot();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (e)
                {
                    case SensorLevelChanged s:
                        await OnSensorLevelAsync(s);
                        break;
                    case LivenessChanged l:
                        await OnLivenessAsync(l.Transition);
                        break;
                    case OperatorRequest r:
                        r.Done.TrySetResult(await r.Work());
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Alerts] Failed to process {Event}", e.GetType().Name);
                if (e is OperatorRequest failed)
                {
                    failed.Done.TrySetException(ex);
                }
            }

            RefreshSnapshot();
        }
    }

    private async Task OnSensorLevelAsync(SensorLevelChanged e)
    {
        var key = DeviceIds.SensorAlertKey(e.SensorId);
        _activeByKey.TryGetValue(key, out var active);

        if (e.Level == ReadingLevel.Normal)
        {
            if (active is not null)
            {
                await ResolveAsync(active, SystemActor);
            }

            return;
        }

        var severity = e.Level == ReadingLevel.Critical ? AlertSeverity.Critical : AlertSeverity.High;
        var message = SensorMessage(e);

        if (active is not null)
        {
            // Cảnh báo chỉ tăng mức, giữ mức cao nhất đã gặp đến khi cảm biến trở về bình thường.
            if (severity <= active.Severity)
            {
                return;
            }

            active.Severity = severity;
            active.Value = e.Value;
            active.Threshold = e.Threshold;
            active.Message = message;
            active.UpdatedAt = e.At;
            await SaveAlertAsync(active, isNew: false);
            await CorrelateAsync(active);
            return;
        }

        var alert = new Alert
        {
            Key = key,
            Rule = $"{e.Kind}_threshold",
            Severity = severity,
            LineId = e.LineId,
            NodeByteId = e.NodeByteId,
            NodeId = e.NodeId,
            SensorId = e.SensorId,
            SensorKind = e.Kind,
            SensorOrdinal = e.Ordinal,
            Value = e.Value,
            Threshold = e.Threshold,
            Message = message,
            CreatedAt = e.At,
            UpdatedAt = e.At
        };
        _activeByKey[key] = alert;
        await SaveAlertAsync(alert, isNew: true);
        await CorrelateAsync(alert);
    }

    private async Task OnLivenessAsync(HealthTransition t)
    {
        if (t.To == Liveness.Unknown)
        {
            return;
        }

        var key = t.IsGateway ? DeviceIds.GatewayOfflineAlertKey(t.LineId) : DeviceIds.NodeOfflineAlertKey(t.LineId, t.NodeByteId!.Value);
        _activeByKey.TryGetValue(key, out var active);

        if (t.To == Liveness.Online)
        {
            if (active is not null)
            {
                await ResolveAsync(active, SystemActor);
            }

            return;
        }

        if (active is not null)
        {
            return;
        }

        var alert = new Alert
        {
            Key = key,
            Rule = t.IsGateway ? "gateway_offline" : "node_offline",
            Severity = t.IsGateway ? AlertSeverity.High : AlertSeverity.Medium,
            LineId = t.LineId,
            NodeByteId = t.NodeByteId,
            NodeId = t.IsGateway ? null : DeviceIds.Node(t.LineId, t.NodeByteId!.Value),
            Message = t.IsGateway
                ? $"Mất kết nối NM tuyến {t.LineId}"
                : $"Mất kết nối N{t.NodeByteId} tuyến {t.LineId}",
            CreatedAt = t.At,
            UpdatedAt = t.At
        };
        _activeByKey[key] = alert;
        await SaveAlertAsync(alert, isNew: true);
    }

    private async Task CorrelateAsync(Alert alert)
    {
        var decision = IncidentCorrelator.Decide(alert, _activeByKey.Values, _openIncidents.Values, _time.GetUtcNow());
        Incident incident;
        var isNew = false;
        var members = new List<Alert> { alert };

        if (decision.AttachTo is { } existingId && _openIncidents.TryGetValue(existingId, out var existing))
        {
            incident = existing;
        }
        else if (decision.CreateType is { } type)
        {
            isNew = true;
            incident = new Incident
            {
                Type = type,
                Severity = alert.Severity,
                LineId = alert.LineId!.Value,
                CreatedAt = alert.UpdatedAt,
                UpdatedAt = alert.UpdatedAt
            };
            _openIncidents[incident.Id] = incident;
            members.AddRange(_activeByKey.Values.Where(a => decision.Partners.Contains(a.Id)));
        }
        else
        {
            return;
        }

        var nodes = IncidentCorrelator.ParseNodes(incident.NodeByteIds).ToList();
        foreach (var member in members)
        {
            member.IncidentId = incident.Id;
            nodes.Add(member.NodeByteId!.Value);
            incident.Severity = (AlertSeverity)Math.Max((int)incident.Severity, (int)member.Severity);
            incident.Type = (IncidentType)Math.Max((int)incident.Type, (int)IncidentCorrelator.TypeOf(member.SensorKind));
        }

        incident.NodeByteIds = IncidentCorrelator.FormatNodes(nodes);
        incident.UpdatedAt = _time.GetUtcNow();

        if (isNew)
        {
            _unsavedIncidents.Add(incident.Id);
        }

        var ids = _incidentAlerts.TryGetValue(incident.Id, out var known) ? known : _incidentAlerts[incident.Id] = [];
        ids.AddRange(members.Select(m => m.Id).Where(id => !ids.Contains(id)));

        await PersistAsync(members, [incident]);
        _logger.LogWarning("[Alerts] Incident {Id} ({Type}, {Severity}) on line {Line}, nodes {Nodes}",
            incident.Id, incident.Type, incident.Severity, incident.LineId, incident.NodeByteIds);
        await PublishIncidentAsync(incident);
    }

    private async Task<Alert?> ChangeAlertAsync(Guid alertId, AlertState state, string actor)
    {
        var alert = _activeByKey.Values.FirstOrDefault(a => a.Id == alertId);
        if (alert is null)
        {
            return null;
        }

        if (state == AlertState.Resolved)
        {
            await ResolveAsync(alert, actor);
            return alert;
        }

        if (alert.State == AlertState.Open)
        {
            alert.State = AlertState.Acknowledged;
            alert.AcknowledgedAt = _time.GetUtcNow();
            alert.AcknowledgedBy = actor;
            alert.UpdatedAt = alert.AcknowledgedAt.Value;
            await SaveAlertAsync(alert, isNew: false);
        }

        return alert;
    }

    private async Task<Incident?> ChangeIncidentAsync(Guid incidentId, IncidentState state, string actor, string? note)
    {
        if (!_openIncidents.TryGetValue(incidentId, out var incident))
        {
            return null;
        }

        incident.State = state;
        incident.Operator = actor;
        incident.Note = note ?? incident.Note;
        incident.UpdatedAt = _time.GetUtcNow();
        if (state == IncidentState.Closed)
        {
            incident.ClosedAt = incident.UpdatedAt;
            _openIncidents.Remove(incidentId);
        }

        await PersistAsync([], [incident]);
        await PublishIncidentAsync(incident);
        if (state == IncidentState.Closed)
        {
            _incidentAlerts.Remove(incidentId);
        }

        return incident;
    }

    private async Task ResolveAsync(Alert alert, string actor)
    {
        alert.State = AlertState.Resolved;
        alert.ResolvedAt = _time.GetUtcNow();
        alert.ResolvedBy = actor;
        alert.UpdatedAt = alert.ResolvedAt.Value;
        _activeByKey.Remove(alert.Key);
        if (actor != SystemActor && alert.SensorId is not null)
        {
            // Operator đóng tay khi cảm biến có thể vẫn vượt ngưỡng: quên mức đã lọc để lần đo sau báo lại.
            _levels.Reset(alert.SensorId);
        }

        await SaveAlertAsync(alert, isNew: false);
    }

    private async Task SaveAlertAsync(Alert alert, bool isNew)
    {
        if (isNew)
        {
            _unsavedAlerts.Add(alert.Id);
        }

        await PersistAsync([alert], []);
        _logger.LogInformation("[Alerts] {State} {Severity} {Key}: {Message}", alert.State, alert.Severity, alert.Key, alert.Message);
        await _notifier.BroadcastAsync(RealtimeNotifier.AlertChanged, alert);
        _uplink.PublishAlert(alert);
        _uplink.RequestSafetyRefresh();
    }

    /// <summary>
    /// Ghi CSDL nhưng không để lỗi CSDL chặn việc báo cho Station và Center: trạng thái trong bộ nhớ là nguồn đúng,
    /// bản ghi chưa ghi được sẽ được ghi lại (Insert) ở lần thay đổi sau.
    /// </summary>
    private async Task PersistAsync(IReadOnlyList<Alert> alerts, IReadOnlyList<Incident> incidents)
    {
        // Cảnh báo trỏ tới tình huống chưa ghi được thì phải ghi kèm tình huống đó, nếu không vi phạm khóa ngoại.
        var allIncidents = incidents
            .Concat(alerts.Where(a => a.IncidentId is { } id && _unsavedIncidents.Contains(id) && _openIncidents.ContainsKey(id))
                .Select(a => _openIncidents[a.IncidentId!.Value]))
            .DistinctBy(i => i.Id)
            .ToList();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var incident in allIncidents)
            {
                if (_unsavedIncidents.Contains(incident.Id))
                {
                    db.Incidents.Add(incident);
                }
                else
                {
                    db.Incidents.Update(incident);
                }
            }

            foreach (var alert in alerts)
            {
                if (_unsavedAlerts.Contains(alert.Id))
                {
                    db.Alerts.Add(alert);
                }
                else
                {
                    db.Alerts.Update(alert);
                }
            }

            await db.SaveChangesAsync();
            _unsavedIncidents.ExceptWith(allIncidents.Select(i => i.Id));
            _unsavedAlerts.ExceptWith(alerts.Select(a => a.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError("[Alerts] Failed to persist {Alerts} alert(s) / {Incidents} incident(s), will retry on next change: {Message}",
                alerts.Count, allIncidents.Count, ex.Message);
        }
    }

    private async Task PublishIncidentAsync(Incident incident)
    {
        var alertIds = _incidentAlerts.TryGetValue(incident.Id, out var ids) ? ids.ToList() : [];

        await _notifier.BroadcastAsync(RealtimeNotifier.IncidentChanged, new { incident, alertIds });
        _uplink.PublishIncident(incident, alertIds);
        _uplink.RequestSafetyRefresh();
    }

    private Task<T?> RunAsync<T>(Func<Task<T?>> work) where T : class
    {
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new OperatorRequest(async () => await work(), done));
        return done.Task.ContinueWith(t => (T?)t.Result, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void RefreshSnapshot() =>
        _snapshot = new AlertSnapshot(
            _activeByKey.Values.Select(a => new ActiveAlertInfo(a.Id, a.LineId, a.Severity)).ToList(),
            _openIncidents.Values.Select(i => new OpenIncidentInfo(i.Id, i.LineId, i.Severity)).ToList());

    private static string SensorMessage(SensorLevelChanged e)
    {
        var profile = SensorFrameDecoder.Profiles[e.Kind];
        var level = e.Level == ReadingLevel.Critical ? "nguy hiểm" : "cảnh báo";
        return $"{profile.Name} vượt ngưỡng {level} tại N{e.NodeByteId} tuyến {e.LineId}: {e.Value:0.##} {profile.Unit} (ngưỡng {e.Threshold:0.##})";
    }
}
