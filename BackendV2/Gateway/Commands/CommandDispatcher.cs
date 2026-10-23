using System.Collections.Concurrent;
using BackendV2.Gateway.Mqtt;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace BackendV2.Gateway.Commands;

public enum CommandStatus
{
    /// <summary>Node đã phản hồi; xem <see cref="CommandOutcome.Result"/> và <see cref="CommandOutcome.Payload"/>.</summary>
    Completed,

    /// <summary>Lệnh phát cho toàn tuyến (Node ID 255), không chờ phản hồi từng node.</summary>
    Broadcast,

    TimedOut,
    NotConnected
}

public sealed record CommandOutcome(CommandStatus Status, int Attempts, byte[]? Payload = null, CommandResultCode? Result = null);

/// <summary>
/// Gửi lệnh xuống node qua <c>ts/{line}/n/{node}/down</c> và chờ phản hồi cùng mã lệnh (docs mục 4.6).
/// Khung không có mã giao dịch nên mỗi (tuyến, node, mã lệnh) chỉ có một lệnh đang chờ; các lệnh sau xếp hàng.
/// Khóa theo node nên các tuyến/node độc lập không chặn nhau.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly IMqttPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<CommandDispatcher> _logger;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly ConcurrentDictionary<(int, byte, NodeCommand), SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<(int, byte, NodeCommand), TaskCompletionSource<byte[]>> _waiting = new();

    public CommandDispatcher(
        IMqttPublisher publisher, IOptions<GatewayOptions> options, TimeProvider time, ILogger<CommandDispatcher> logger)
    {
        _publisher = publisher;
        _time = time;
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);
        _maxAttempts = Math.Max(1, options.Value.CommandMaxAttempts);
    }

    public async Task<CommandOutcome> SendAsync(int lineId, byte nodeId, NodeCommand command, byte[] payload, CancellationToken ct)
    {
        var frame = WireFrameCodec.Encode((byte)command, nodeId, payload);
        var topic = GatewayTopics.NodeDownlink(lineId, nodeId);

        if (nodeId == GatewayTopics.BroadcastNodeId)
        {
            return await TryPublishAsync(topic, frame, ct)
                ? new CommandOutcome(CommandStatus.Broadcast, 1)
                : new CommandOutcome(CommandStatus.NotConnected, 1);
        }

        var key = (lineId, nodeId, command);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting[key] = tcs;
                try
                {
                    if (!await TryPublishAsync(topic, frame, ct))
                    {
                        return new CommandOutcome(CommandStatus.NotConnected, attempt);
                    }

                    var response = await tcs.Task.WaitAsync(_timeout, _time, ct);
                    CommandResultCode? code = CommandAck.TryDecode(response, out var c) ? c : null;
                    return new CommandOutcome(CommandStatus.Completed, attempt, response, code);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("[Command] {Command} to {Node} timed out (attempt {Attempt}/{Max})",
                        command, DeviceIds.Node(lineId, nodeId), attempt, _maxAttempts);
                }
                finally
                {
                    _waiting.TryRemove(KeyValuePair.Create(key, tcs));
                }
            }

            return new CommandOutcome(CommandStatus.TimedOut, _maxAttempts);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Gọi từ router khi node phản hồi. Trả về false nếu không có lệnh nào đang chờ phản hồi này.</summary>
    public bool OnResponse(int lineId, byte nodeId, NodeCommand command, byte[] payload) =>
        _waiting.TryGetValue((lineId, nodeId, command), out var tcs) && tcs.TrySetResult(payload);

    private async Task<bool> TryPublishAsync(string topic, byte[] frame, CancellationToken ct)
    {
        try
        {
            await _publisher.PublishAsync(topic, frame, MqttQos.AtLeastOnce, false, ct);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("[Command] Publish to {Topic} failed: {Message}", topic, ex.Message);
            return false;
        }
    }
}
