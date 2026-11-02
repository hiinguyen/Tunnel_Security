using System.Threading.Channels;
using BackendV2.Data;
using BackendV2.Models;
using Microsoft.Extensions.Options;

namespace BackendV2.Gateway.Ingestion;

/// <summary>
/// Ghi chuỗi thời gian theo lô (docs mục 8.2): gom tối đa BatchSize dòng hoặc FlushIntervalMs rồi ghi một lần.
/// Hàng đợi có giới hạn; khi đầy thì bỏ bản cũ nhất thay vì chặn luồng nhận MQTT.
/// </summary>
public sealed class ReadingWriter : BackgroundService
{
    private readonly ReadingWriterOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReadingWriter> _logger;
    private readonly Channel<Reading> _queue;
    private long _dropped;
    private long _written;
    private long _lost;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    public ReadingWriter(IOptions<ReadingWriterOptions> options, IServiceScopeFactory scopeFactory, ILogger<ReadingWriter> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _queue = Channel.CreateBounded<Reading>(
            new BoundedChannelOptions(_options.Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public long Written => Interlocked.Read(ref _written);

    public long Dropped => Interlocked.Read(ref _dropped);

    public long Lost => Interlocked.Read(ref _lost);

    public int QueueLength => _queue.Reader.Count;

    public void Enqueue(Reading reading) => _queue.Writer.TryWrite(reading);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Reading>(_options.BatchSize);
        var flushInterval = TimeSpan.FromMilliseconds(_options.FlushIntervalMs);
        long reportedDrops = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                var deadline = DateTime.UtcNow + flushInterval;
                while (batch.Count < _options.BatchSize && DateTime.UtcNow < deadline)
                {
                    while (batch.Count < _options.BatchSize && _queue.Reader.TryRead(out var reading))
                    {
                        batch.Add(reading);
                    }

                    if (batch.Count < _options.BatchSize)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(20), stoppingToken);
                    }
                }

                await WriteWithRetryAsync(batch, stoppingToken);
                batch.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref _lost, batch.Count);
                _logger.LogError(ex, "[ReadingWriter] Unexpected error, {Count} readings lost", batch.Count);
                batch.Clear();
            }

            var dropped = Dropped;
            if (dropped != reportedDrops)
            {
                _logger.LogWarning("[ReadingWriter] Queue full, {Count} readings dropped so far", dropped);
                reportedDrops = dropped;
            }
        }

        await DrainOnShutdownAsync(batch);
    }

    /// <summary>
    /// Ghi một lô, thử lại khi CSDL trục trặc ngắn (1, 2, 4 giây). Trong lúc chờ, hàng đợi vẫn nhận reading mới
    /// (và chỉ bỏ bản cũ nhất khi đầy), nên sự cố ngắn không làm mất dữ liệu.
    /// </summary>
    private async Task WriteWithRetryAsync(List<Reading> batch, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await WriteAsync(batch, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < RetryDelays.Length)
            {
                _logger.LogWarning("[ReadingWriter] Write of {Count} readings failed ({Message}), retry in {Delay}s",
                    batch.Count, ex.Message, RetryDelays[attempt].TotalSeconds);
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Add(ref _lost, batch.Count);
                _logger.LogError("[ReadingWriter] Gave up writing {Count} readings after {Attempts} attempts: {Message}",
                    batch.Count, attempt + 1, ex.Message);
                return;
            }
        }
    }

    private async Task DrainOnShutdownAsync(List<Reading> batch)
    {
        using var timeout = new CancellationTokenSource(ShutdownDrainTimeout);
        while (_queue.Reader.TryRead(out var reading))
        {
            batch.Add(reading);
        }

        try
        {
            foreach (var chunk in batch.Chunk(_options.BatchSize))
            {
                await WriteAsync([.. chunk], timeout.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("[ReadingWriter] Could not flush {Count} readings on shutdown: {Message}", batch.Count, ex.Message);
        }
    }

    private async Task WriteAsync(List<Reading> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<TimeSeriesDbContext>();
        if (db is null)
        {
            return; // TimeSeries:Enabled = false, dữ liệu chỉ được phát realtime.
        }

        db.Readings.AddRange(batch);
        await db.SaveChangesAsync(ct);
        Interlocked.Add(ref _written, batch.Count);
    }
}
