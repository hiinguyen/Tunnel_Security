using System.Collections.Concurrent;
using System.Threading.Channels;
using BackendV2.Data;
using BackendV2.Models;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Gateway.Ingestion;

public sealed record SensorThresholds(string SensorId, double? Warning, double? Critical);

/// <summary>
/// Danh mục cảm biến trong bộ nhớ, kèm ngưỡng cảnh báo. Cảm biến chưa có trong CSDL được tự tạo khi
/// lần đầu nhận giá trị (không cần khai báo trước, nên số cảm biến mở rộng theo node). Ngưỡng được nạp lại định kỳ
/// để thay đổi qua API có hiệu lực mà không cần khởi động lại.
/// </summary>
public sealed class SensorCatalog : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>Số cảm biến tối đa của một node, để một node lỗi hoặc bị chiếm quyền không tạo được vô hạn bản ghi.</summary>
    public const int MaxSensorsPerNode = 64;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SensorCatalog> _logger;
    private readonly ConcurrentDictionary<string, SensorThresholds> _known = new();
    private readonly ConcurrentDictionary<string, int> _countByNode = new();
    private readonly Channel<Sensor> _toCreate = Channel.CreateUnbounded<Sensor>(new UnboundedChannelOptions { SingleReader = true });

    public SensorCatalog(IServiceScopeFactory scopeFactory, ILogger<SensorCatalog> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    /// <summary>Ngưỡng của cảm biến; null nếu node đã có đủ <see cref="MaxSensorsPerNode"/> cảm biến.</summary>
    public SensorThresholds? Resolve(NodeEntry node, DecodedMeasurement measurement)
    {
        var id = DeviceIds.Sensor(node.LineId, node.NodeByteId, measurement.Kind, measurement.Ordinal);
        if (_known.TryGetValue(id, out var known))
        {
            return known;
        }

        if (_countByNode.GetValueOrDefault(node.NodeId) >= MaxSensorsPerNode)
        {
            return null;
        }

        var profile = SensorFrameDecoder.Profiles[measurement.Kind];
        var created = new SensorThresholds(id, profile.Warning, profile.Critical);
        if (_known.TryAdd(id, created))
        {
            _countByNode.AddOrUpdate(node.NodeId, 1, (_, c) => c + 1);
            _toCreate.Writer.TryWrite(new Sensor
            {
                Id = id,
                NodeId = node.NodeId,
                Name = measurement.Ordinal > 1 ? $"{profile.Name} {measurement.Ordinal}" : profile.Name,
                Type = profile.Type,
                Description = profile.Description,
                SensorByteId = measurement.TypeByte,
                Ordinal = measurement.Ordinal,
                Unit = profile.Unit,
                WarningThreshold = profile.Warning,
                CriticalThreshold = profile.Critical
            });
        }

        return _known[id];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRefresh = DateTime.UtcNow + RefreshInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(nextRefresh - DateTime.UtcNow is var remaining && remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                try
                {
                    await _toCreate.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }

                await CreatePendingAsync(stoppingToken);

                if (DateTime.UtcNow >= nextRefresh)
                {
                    await RefreshAsync(stoppingToken);
                    nextRefresh = DateTime.UtcNow + RefreshInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SensorCatalog] Sync failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
            }
        }
    }

    private async Task CreatePendingAsync(CancellationToken ct)
    {
        var batch = new List<Sensor>();
        while (_toCreate.Reader.TryRead(out var sensor))
        {
            batch.Add(sensor);
        }

        if (batch.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = batch.Select(s => s.Id).ToList();
        var existing = (await db.Sensors.Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var fresh = batch.Where(s => !existing.Contains(s.Id)).DistinctBy(s => s.Id).ToList();
        db.Sensors.AddRange(fresh);
        await db.SaveChangesAsync(ct);

        if (fresh.Count > 0)
        {
            _logger.LogInformation("[SensorCatalog] Registered {Count} new sensors", fresh.Count);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sensors = await db.Sensors.AsNoTracking()
            .Select(s => new { s.Id, s.NodeId, s.WarningThreshold, s.CriticalThreshold })
            .ToListAsync(ct);

        foreach (var s in sensors)
        {
            _known[s.Id] = new SensorThresholds(s.Id, s.WarningThreshold, s.CriticalThreshold);
        }

        foreach (var group in sensors.GroupBy(s => s.NodeId))
        {
            _countByNode.AddOrUpdate(group.Key, group.Count(), (_, current) => Math.Max(current, group.Count()));
        }
    }
}
