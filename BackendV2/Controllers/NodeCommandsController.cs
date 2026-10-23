using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BackendV2.Auth;
using BackendV2.Data;
using BackendV2.Gateway;
using BackendV2.Gateway.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireProtocol;

namespace BackendV2.Controllers;

public sealed record ActuatorCommand(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ActuatorTypeByte Type, [Range(1, 255)] byte Ordinal, bool On);

public sealed record ControlRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] NodePowerState? PowerMode,
    IReadOnlyList<ActuatorCommand>? Actuators);

public sealed record WriteConfigRequest([Range(100, 3_600_000)] uint? SampleIntervalMs, byte? NodeType);

public sealed record CommandResultView(string Status, int Attempts, string? Result, object? Data = null);

/// <summary>
/// Lệnh xuống node (A2, A3, A5, A6). <c>nodeId = 255</c> phát cho toàn tuyến (chỉ reset và điều khiển),
/// kết quả kiểm tra lại qua chu kỳ A4 tiếp theo.
/// </summary>
[ApiController]
[Route("api/lines/{lineId:int}/nodes/{nodeId:int}/commands")]
[Authorize(Policy = Policies.DeviceManagement)]
public class NodeCommandsController : ControllerBase
{
    private readonly CommandDispatcher _commands;
    private readonly TopologyCache _topology;
    private readonly AppDbContext _db;

    public NodeCommandsController(CommandDispatcher commands, TopologyCache topology, AppDbContext db)
    {
        _commands = commands;
        _topology = topology;
        _db = db;
    }

    [HttpPost("reset")]
    public Task<ActionResult<CommandResultView>> Reset(int lineId, int nodeId, CancellationToken ct) =>
        SendAsync(lineId, nodeId, NodeCommand.Reset, [], allowBroadcast: true, "node.reset", null, ct);

    [HttpPost("control")]
    public Task<ActionResult<CommandResultView>> Control(int lineId, int nodeId, ControlRequest request, CancellationToken ct)
    {
        var entries = new List<SensorTlvEntry>();
        if (request.PowerMode is { } mode)
        {
            entries.Add(ControlPayload.PowerMode(mode));
        }

        foreach (var a in request.Actuators ?? [])
        {
            entries.Add(ControlPayload.Actuator(a.Type, a.Ordinal, a.On));
        }

        if (entries.Count == 0)
        {
            return Task.FromResult<ActionResult<CommandResultView>>(BadRequest(new { message = "Nothing to control" }));
        }

        return SendAsync(lineId, nodeId, NodeCommand.Control, SensorTlvCodec.Encode(entries), allowBroadcast: true, "node.control", request, ct);
    }

    [HttpGet("config")]
    public async Task<ActionResult<CommandResultView>> ReadConfig(int lineId, int nodeId, CancellationToken ct)
    {
        var result = await SendAsync(lineId, nodeId, NodeCommand.ReadConfig, [], allowBroadcast: false, null, null, ct);
        if (result.Result is OkObjectResult { Value: CommandResultView { Data: byte[] payload } view })
        {
            var config = NodeConfig.FromEntries(SensorTlvCodec.Parse(payload));
            return Ok(view with
            {
                Result = null,
                Data = new { config.NodeId, config.SampleIntervalMs, config.NodeType, IpAddress = config.IpAddress?.ToString() }
            });
        }

        return result;
    }

    [HttpPut("config")]
    public Task<ActionResult<CommandResultView>> WriteConfig(int lineId, int nodeId, WriteConfigRequest request, CancellationToken ct)
    {
        var config = new NodeConfig(SampleIntervalMs: request.SampleIntervalMs, NodeType: request.NodeType);
        return SendAsync(lineId, nodeId, NodeCommand.WriteConfig, config.ToBytes(), allowBroadcast: false, "node.config", request, ct);
    }

    private async Task<ActionResult<CommandResultView>> SendAsync(
        int lineId, int nodeId, NodeCommand command, byte[] payload, bool allowBroadcast, string? auditAction, object? auditValue,
        CancellationToken ct)
    {
        var isBroadcast = nodeId == GatewayTopics.BroadcastNodeId;
        if (!_topology.IsActiveLine(lineId)
            || nodeId is < 1 or > 255
            || (isBroadcast && !allowBroadcast)
            || (!isBroadcast && !_topology.TryGetNode(lineId, (byte)nodeId, out _)))
        {
            return NotFound(new { message = $"Node {nodeId} on line {lineId} not found" });
        }

        if (auditAction is not null)
        {
            _db.Audit(User, auditAction, "Node", isBroadcast ? $"L{lineId}-all" : DeviceIds.Node(lineId, (byte)nodeId), auditValue);
            await _db.SaveChangesAsync(ct);
        }

        var outcome = await _commands.SendAsync(lineId, (byte)nodeId, command, payload, ct);
        // Bản tin xác nhận chỉ có 1 byte mã kết quả; payload thô chỉ có ý nghĩa với phản hồi mang dữ liệu (A2).
        var view = new CommandResultView(
            outcome.Status.ToString(), outcome.Attempts, outcome.Result?.ToString(), outcome.Result is null ? outcome.Payload : null);

        return outcome.Status switch
        {
            CommandStatus.Completed or CommandStatus.Broadcast => Ok(view),
            CommandStatus.TimedOut => StatusCode(StatusCodes.Status504GatewayTimeout, view),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, view)
        };
    }
}
