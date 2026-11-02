using BackendV2.Gateway;
using BackendV2.Gateway.Commands;
using BackendV2.Gateway.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WireProtocol;

namespace BackendV2.Tests;

public class CommandDispatcherTests
{
    private readonly FakePublisher _publisher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CommandDispatcher _dispatcher;

    public CommandDispatcherTests()
    {
        var options = Options.Create(new GatewayOptions { CommandTimeoutSeconds = 3, CommandMaxAttempts = 3 });
        _dispatcher = new CommandDispatcher(_publisher, options, _time, NullLogger<CommandDispatcher>.Instance);
    }

    [Fact]
    public async Task Publishes_frame_to_node_downlink_and_completes_on_matching_response()
    {
        var send = _dispatcher.SendAsync(2, 7, NodeCommand.Reset, [], CancellationToken.None);
        await _publisher.WaitForCountAsync(1);

        var (topic, payload) = _publisher.Messages.Single();
        Assert.Equal("ts/2/n/7/down", topic);
        Assert.True(WireFrameCodec.TryDecode(payload, out var frame, out _));
        Assert.Equal((byte)NodeCommand.Reset, frame!.Command);
        Assert.Equal(7, frame.NodeId);

        Assert.True(_dispatcher.OnResponse(2, 7, NodeCommand.Reset, CommandAck.Encode(CommandResultCode.Ok)));
        var outcome = await send;

        Assert.Equal(CommandStatus.Completed, outcome.Status);
        Assert.Equal(CommandResultCode.Ok, outcome.Result);
        Assert.Equal(1, outcome.Attempts);
    }

    [Fact]
    public async Task Retries_on_timeout_then_gives_up()
    {
        var send = _dispatcher.SendAsync(1, 1, NodeCommand.Control, [1], CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await _publisher.WaitForCountAsync(attempt);
            _time.Advance(TimeSpan.FromSeconds(3));
        }

        var outcome = await send;
        Assert.Equal(CommandStatus.TimedOut, outcome.Status);
        Assert.Equal(3, _publisher.Messages.Count);
    }

    [Fact]
    public async Task Response_for_another_node_or_command_does_not_complete_the_request()
    {
        var send = _dispatcher.SendAsync(1, 1, NodeCommand.Reset, [], CancellationToken.None);
        await _publisher.WaitForCountAsync(1);

        Assert.False(_dispatcher.OnResponse(1, 2, NodeCommand.Reset, [0]));
        Assert.False(_dispatcher.OnResponse(1, 1, NodeCommand.Control, [0]));
        Assert.False(_dispatcher.OnResponse(2, 1, NodeCommand.Reset, [0]));
        Assert.False(send.IsCompleted);

        _dispatcher.OnResponse(1, 1, NodeCommand.Reset, [0]);
        Assert.Equal(CommandStatus.Completed, (await send).Status);
    }

    [Fact]
    public async Task Same_command_to_same_node_is_serialized()
    {
        var first = _dispatcher.SendAsync(1, 1, NodeCommand.Control, [1], CancellationToken.None);
        var second = _dispatcher.SendAsync(1, 1, NodeCommand.Control, [2], CancellationToken.None);
        await _publisher.WaitForCountAsync(1);
        await Task.Delay(50);
        Assert.Single(_publisher.Messages);

        _dispatcher.OnResponse(1, 1, NodeCommand.Control, [0]);
        await first;
        await _publisher.WaitForCountAsync(2);

        _dispatcher.OnResponse(1, 1, NodeCommand.Control, [0]);
        Assert.Equal(CommandStatus.Completed, (await second).Status);
    }

    [Fact]
    public async Task Broadcast_does_not_wait_for_a_response()
    {
        var outcome = await _dispatcher.SendAsync(1, GatewayTopics.BroadcastNodeId, NodeCommand.Reset, [], CancellationToken.None);

        Assert.Equal(CommandStatus.Broadcast, outcome.Status);
        Assert.Equal("ts/1/n/255/down", _publisher.Messages.Single().Topic);
    }

    [Fact]
    public async Task Reports_not_connected_when_publish_fails()
    {
        _publisher.Connected = false;

        var outcome = await _dispatcher.SendAsync(1, 1, NodeCommand.Reset, [], CancellationToken.None);

        Assert.Equal(CommandStatus.NotConnected, outcome.Status);
    }

    private sealed class FakePublisher : IMqttPublisher
    {
        private readonly object _gate = new();
        private readonly List<(string Topic, byte[] Payload)> _messages = [];

        public bool Connected { get; set; } = true;

        public bool IsConnected => Connected;

        public IReadOnlyList<(string Topic, byte[] Payload)> Messages
        {
            get
            {
                lock (_gate)
                {
                    return _messages.ToList();
                }
            }
        }

        public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, MqttQos qos, bool retain, CancellationToken ct)
        {
            if (!Connected)
            {
                throw new InvalidOperationException("not connected");
            }

            lock (_gate)
            {
                _messages.Add((topic, payload.ToArray()));
            }

            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Messages.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Expected {count} published messages, got {Messages.Count}");
                }

                await Task.Delay(5);
            }
        }
    }
}
