using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BackendV2.Data;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Mqtt;
using BackendV2.Gateway.Realtime;
using BackendV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace BackendV2.Gateway.Provisioning;

/// <summary>
/// Yêu cầu cấp tuyến đang chờ duyệt. <see cref="ExistingLineId"/> có giá trị khi NM đã có tuyến nhưng xin cấp lại
/// (mất cấu hình), duyệt khi đó cấp lại đúng tuyến cũ.
/// </summary>
public sealed record PendingGateway(
    string HardwareId, string? Mac, string? Ip, string? Version, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    int? ExistingLineId = null);

/// <summary>Bản tin <c>ts/prov/{hwId}/resp</c> gửi NM. <see cref="Username"/>/<see cref="Password"/> chỉ có khi bật Dynamic Security.</summary>
public sealed record ProvisionResponse(string Status, int? LineId, string? Name, string? Username, string? Password);

/// <summary>
/// Cấp tuyến cho NM (docs mục 4.4): NM gửi yêu cầu, operator duyệt (hoặc tự duyệt khi phát triển),
/// PC cấp lineId nhỏ nhất chưa từng dùng, tạo tài khoản MQTT riêng cho tuyến và trả kết quả về NM.
/// Yêu cầu được xử lý trên worker riêng (mỗi lần cấp tốn vài lượt hỏi đáp với broker), không chặn luồng dữ liệu.
/// Tài khoản cấp phát dùng chung cho mọi NM, nên một yêu cầu không bao giờ tự làm đổi mật khẩu của tuyến đang chạy:
/// cấp lại cho tuyến đã có cũng phải qua duyệt.
/// </summary>
public sealed class GatewayProvisioningService : BackgroundService
{
    public const int MaxPending = 256;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DynamicSecurityClient _dynSec;
    private readonly IMqttPublisher _publisher;
    private readonly TopologyCache _topology;
    private readonly DeviceHealthService _health;
    private readonly RealtimeNotifier _notifier;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GatewayProvisioningService> _logger;
    private readonly ConcurrentDictionary<string, PendingGateway> _pending = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Mỗi NM chỉ giữ yêu cầu mới nhất trong hàng đợi: NM gửi lại liên tục cũng không làm đầy hàng đợi.
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<byte>> _latestRequest = new();
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxPending) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public GatewayProvisioningService(
        IServiceScopeFactory scopeFactory,
        DynamicSecurityClient dynSec,
        IMqttPublisher publisher,
        TopologyCache topology,
        DeviceHealthService health,
        RealtimeNotifier notifier,
        IOptions<GatewayOptions> options,
        TimeProvider time,
        ILogger<GatewayProvisioningService> logger)
    {
        _scopeFactory = scopeFactory;
        _dynSec = dynSec;
        _publisher = publisher;
        _topology = topology;
        _health = health;
        _notifier = notifier;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<PendingGateway> Pending
    {
        get
        {
            PruneExpired();
            return _pending.Values.OrderBy(p => p.FirstSeen).ToList();
        }
    }

    /// <summary>Gọi từ router MQTT: chỉ xếp hàng, việc xử lý chạy trên worker của service này.</summary>
    public void Enqueue(string hardwareId, ReadOnlyMemory<byte> payload)
    {
        var isQueued = _latestRequest.ContainsKey(hardwareId);
        _latestRequest[hardwareId] = payload;
        if (!isQueued)
        {
            _queue.Writer.TryWrite(hardwareId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var hardwareId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_latestRequest.TryRemove(hardwareId, out var payload))
                {
                    continue;
                }

                try
                {
                    await ProcessRequestAsync(hardwareId, payload, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError("[Provision] Request from {Hw} failed: {Message}", hardwareId, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<Line> ApproveAsync(string hardwareId, string? name, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        Line line;
        try
        {
            if (!_pending.TryGetValue(hardwareId, out var request))
            {
                throw new KeyNotFoundException($"No pending request from gateway '{hardwareId}'");
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (request.ExistingLineId is { } existingId)
            {
                line = await db.Lines.FirstOrDefaultAsync(l => l.Id == existingId && l.RetiredAt == null, ct)
                    ?? throw new InvalidOperationException($"Line {existingId} no longer exists or was retired");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    line.Name = name.Trim();
                    await db.SaveChangesAsync(ct);
                }
            }
            else
            {
                line = await CreateLineAsync(db, hardwareId, request, name, ct);
            }

            _pending.TryRemove(hardwareId, out _);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("[Provision] Gateway {Hw} approved as line {Line}", hardwareId, line.Id);
        await IssueAsync(line, ct);
        await _notifier.BroadcastAsync(RealtimeNotifier.GatewayChanged, new { lineId = line.Id, state = line.Status, at = _time.GetUtcNow() });
        return line;
    }

    public async Task<bool> RejectAsync(string hardwareId, CancellationToken ct)
    {
        if (!_pending.TryRemove(hardwareId, out _))
        {
            return false;
        }

        await PublishResponseAsync(hardwareId, new ProvisionResponse("rejected", null, null, null, null), ct);
        _logger.LogInformation("[Provision] Gateway {Hw} rejected", hardwareId);
        return true;
    }

    /// <summary>
    /// Gỡ tuyến: ngừng nhận dữ liệu của tuyến ngay, rồi thu hồi tài khoản MQTT. Gọi lại được trên tuyến đã gỡ
    /// để hoàn tất việc thu hồi nếu lần trước broker lỗi. Trả về false nếu tuyến không tồn tại.
    /// </summary>
    public async Task<bool> RetireAsync(int lineId, CancellationToken ct)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var line = await db.Lines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
            if (line is null)
            {
                return false;
            }

            if (line.RetiredAt is null)
            {
                line.RetiredAt = _time.GetUtcNow();
                line.Status = Liveness.Offline;
                await db.SaveChangesAsync(ct);
            }
        }

        _topology.RemoveLine(lineId);
        _health.RemoveLine(lineId);

        if (_dynSec.Enabled)
        {
            await _dynSec.DeleteLineAccountAsync(lineId, ct);
        }

        _logger.LogInformation("[Provision] Line {Line} retired", lineId);
        return true;
    }

    private async Task ProcessRequestAsync(string hardwareId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var info = ParseRequest(payload);

        int? existingLineId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            existingLineId = await db.Lines.AsNoTracking()
                .Where(l => l.GatewayHardwareId == hardwareId && l.RetiredAt == null)
                .Select(l => (int?)l.Id)
                .FirstOrDefaultAsync(ct);
        }

        var now = _time.GetUtcNow();
        PruneExpired();
        var isNew = !_pending.ContainsKey(hardwareId);
        if (isNew && _pending.Count >= MaxPending)
        {
            _logger.LogWarning("[Provision] Too many pending gateways ({Max}), ignoring {Hw}", MaxPending, hardwareId);
            return;
        }

        _pending.AddOrUpdate(hardwareId,
            _ => new PendingGateway(hardwareId, info.Mac, info.Ip, info.Version, now, now, existingLineId),
            (_, old) => old with { Mac = info.Mac, Ip = info.Ip, Version = info.Version, LastSeen = now, ExistingLineId = existingLineId });

        if (_options.AutoApproveGateways)
        {
            await ApproveAsync(hardwareId, null, ct);
            return;
        }

        if (isNew)
        {
            _logger.LogInformation(existingLineId is null
                ? "[Provision] Gateway {Hw} waiting for approval"
                : "[Provision] Gateway {Hw} asks to be re-issued line {Line}, waiting for approval", hardwareId, existingLineId);
            await _notifier.BroadcastAsync(RealtimeNotifier.PendingGateway, _pending[hardwareId]);
        }
    }

    private async Task<Line> CreateLineAsync(AppDbContext db, string hardwareId, PendingGateway request, string? name, CancellationToken ct)
    {
        // Không tái sử dụng id của tuyến đã gỡ, để lịch sử của tuyến cũ không bị lẫn.
        var used = (await db.Lines.Select(l => l.Id).ToListAsync(ct)).ToHashSet();
        var id = Enumerable.Range(GatewayTopics.MinLineId, GatewayTopics.MaxLineId).FirstOrDefault(i => !used.Contains(i));
        if (id == 0)
        {
            throw new InvalidOperationException("All 254 line ids are in use; split into another station");
        }

        var line = new Line
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"Tuyến {id}" : name.Trim(),
            GatewayHardwareId = hardwareId,
            GatewayIp = request.Ip,
            GatewayMac = request.Mac,
            GatewayVersion = request.Version,
            CreatedAt = _time.GetUtcNow()
        };
        db.Lines.Add(line);
        await db.SaveChangesAsync(ct);

        _topology.UpsertLine(new LineEntry(line.Id, line.Name, line.GatewayHardwareId));
        _health.RegisterLine(line.Id);
        return line;
    }

    private void PruneExpired()
    {
        var cutoff = _time.GetUtcNow() - TimeSpan.FromSeconds(_options.PendingRequestTtlSeconds);
        foreach (var stale in _pending.Values.Where(p => p.LastSeen < cutoff))
        {
            _pending.TryRemove(stale.HardwareId, out _);
        }
    }

    private async Task IssueAsync(Line line, CancellationToken ct)
    {
        string? password = null;
        if (_dynSec.Enabled)
        {
            password = await _dynSec.EnsureLineAccountAsync(line.Id, ct);
        }

        await PublishResponseAsync(line.GatewayHardwareId, new ProvisionResponse(
            "approved", line.Id, line.Name, password is null ? null : DynamicSecurityClient.LineUsername(line.Id), password), ct);
    }

    private Task PublishResponseAsync(string hardwareId, ProvisionResponse response, CancellationToken ct) =>
        _publisher.PublishAsync(
            GatewayTopics.ProvisionResponse(hardwareId), JsonSerializer.SerializeToUtf8Bytes(response, Json), MqttQos.AtLeastOnce, false, ct);

    private static (string? Mac, string? Ip, string? Version) ParseRequest(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return (Str(root, "mac"), Str(root, "ip"), Str(root, "version"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?[..Math.Min(v.GetString()!.Length, 64)]
                : null;
    }
}
