using System.Net.WebSockets;
using System.Threading.Channels;

namespace LineGateway.Video;

/// <summary>
/// Một WebSocket tới ingest camera trên PC cho một node. Hàng đợi 4 bản tin, đầy thì bỏ bản cũ nhất.
/// Tự kết thúc sau 30 giây không có dữ liệu (node tắt camera hoặc rời tuyến), pool tạo lại khi có frame mới.
/// </summary>
public sealed class CameraForwarder
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly Channel<(WebSocketMessageType Type, byte[] Data)> _queue =
        Channel.CreateBounded<(WebSocketMessageType, byte[])>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Uri _ingest;
    private readonly ILogger _logger;

    public CameraForwarder(Uri ingest, ILogger logger)
    {
        _ingest = ingest;
        _logger = logger;
    }

    public bool TryPost(WebSocketMessageType type, byte[] data) => _queue.Writer.TryWrite((type, data));

    public void Complete() => _queue.Writer.TryComplete();

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(_ingest, ct);
                _logger.LogInformation("[Camera] streaming to {Uri}", _ingest);
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);
                    var (type, data) = await _queue.Reader.ReadAsync(idle.Token);
                    await socket.SendAsync(data, type, endOfMessage: true, ct);
                }
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("[Camera] {Uri}: idle, closing", _ingest);
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
            {
                _logger.LogWarning("[Camera] {Uri}: {Error}, retrying", _ingest, ex.Message);
                await Task.Delay(ReconnectDelay, ct);
            }
        }
    }
}
