using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using BackendV2.Data;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Mqtt;
using BackendV2.Gateway.Realtime;
using BackendV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace BackendV2.Gateway.Provisioning;

public sealed record PendingDevice(uint PhysicalId, int LineId, string Reason, DateTimeOffset LastSeen);

/// <summary>
/// Thêm node vào tuyến bằng xác thực thách đố A1 rồi cấp Node ID bằng A3 (docs mục 4.5).
/// Node chưa khai báo không được vào; nó được đưa vào danh sách chờ để operator khai báo khóa.
/// Node đã từng tham gia (cùng ID vật lý) nhận lại đúng Node ID cũ.
/// </summary>
public sealed class NodeJoinService
{
    public const int MaxPendingDevices = 1024;
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeviceKeyStore _keys;
    private readonly IMqttPublisher _publisher;
    private readonly TopologyCache _topology;
    private readonly DeviceHealthService _health;
    private readonly RealtimeNotifier _notifier;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<NodeJoinService> _logger;
    private readonly ConcurrentDictionary<(int LineId, uint PhysicalId), (uint Nonce, DateTimeOffset Expires)> _challenges = new();
    private readonly ConcurrentDictionary<(int LineId, uint PhysicalId), byte> _assigning = new();
    private readonly ConcurrentDictionary<uint, PendingDevice> _pendingDevices = new();
    private readonly SemaphoreSlim _allocationLock = new(1, 1);

    // MAC chỉ 4 byte theo tài liệu giao thức, nên giới hạn số lần đoán sai trên mỗi thiết bị.
    private readonly ConcurrentDictionary<uint, (int Failures, DateTimeOffset LockedUntil)> _failures = new();

    public NodeJoinService(
        IServiceScopeFactory scopeFactory,
        DeviceKeyStore keys,
        IMqttPublisher publisher,
        TopologyCache topology,
        DeviceHealthService health,
        RealtimeNotifier notifier,
        IOptions<GatewayOptions> options,
        TimeProvider time,
        ILogger<NodeJoinService> logger)
    {
        _scopeFactory = scopeFactory;
        _keys = keys;
        _publisher = publisher;
        _topology = topology;
        _health = health;
        _notifier = notifier;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<PendingDevice> PendingDevices
    {
        get
        {
            var cutoff = _time.GetUtcNow() - TimeSpan.FromSeconds(_options.PendingRequestTtlSeconds);
            foreach (var stale in _pendingDevices.Values.Where(p => p.LastSeen < cutoff))
            {
                _pendingDevices.TryRemove(stale.PhysicalId, out _);
            }

            return _pendingDevices.Values.OrderBy(p => p.PhysicalId).ToList();
        }
    }

    public void ForgetPendingDevice(uint physicalId) => _pendingDevices.TryRemove(physicalId, out _);

    public async Task OnJoinAsync(int lineId, byte[] payload, CancellationToken ct)
    {
        if (!JoinProtocol.TryDecodeUplink(payload, out var uplink))
        {
            _logger.LogWarning("[Join] L{Line}: malformed A1 payload ({Length} bytes)", lineId, payload.Length);
            return;
        }

        if (uplink.Mac is { } mac)
        {
            await OnChallengeResponseAsync(lineId, uplink.PhysicalId, mac, ct);
        }
        else
        {
            await OnJoinRequestAsync(lineId, uplink.PhysicalId, ct);
        }
    }

    public async Task OnAssignmentConfirmAsync(int lineId, byte[] payload)
    {
        if (!NodeAssignment.TryDecodeConfirm(payload, out var physicalId, out var result))
        {
            _logger.LogWarning("[Join] L{Line}: malformed A3 confirm", lineId);
            return;
        }

        if (!_assigning.TryRemove((lineId, physicalId), out var nodeByteId))
        {
            _logger.LogWarning("[Join] L{Line}: unexpected A3 confirm from {Phys:X8}", lineId, physicalId);
            return;
        }

        if (result != CommandResultCode.Ok)
        {
            _logger.LogWarning("[Join] L{Line}: device {Phys:X8} refused assignment N{Node}: {Result}", lineId, physicalId, nodeByteId, result);
            return;
        }

        var nodeId = DeviceIds.Node(lineId, nodeByteId);
        _topology.UpsertNode(new NodeEntry(lineId, nodeByteId, nodeId, physicalId));
        _health.OnNodeSeen(lineId, nodeByteId);
        _logger.LogInformation("[Join] Device {Phys:X8} joined as {Node}", physicalId, nodeId);
        await _notifier.BroadcastAsync(RealtimeNotifier.NodeJoined, new { lineId, nodeByteId, nodeId, physicalId });
    }

    /// <summary>
    /// Tách thiết bị khỏi node đang gắn (khi thu hồi khóa): ngừng nhận dữ liệu của node ngay, giữ lịch sử.
    /// Thiết bị phải tham gia lại (và sẽ bị từ chối nếu khóa đã thu hồi).
    /// </summary>
    public async Task DetachDeviceAsync(uint physicalId, CancellationToken ct)
    {
        await _allocationLock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var node = await db.Nodes.FirstOrDefaultAsync(n => n.PhysicalId == physicalId, ct);
            if (node is null)
            {
                return;
            }

            Detach(node);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[Join] Device {Phys:X8} detached from {Node}", physicalId, node.Id);
        }
        finally
        {
            _allocationLock.Release();
        }
    }

    private void Detach(Node node)
    {
        node.PhysicalId = null;
        node.Status = NodeStatus.Offline;
        if (node is { LineId: { } line, NodeByteId: { } nodeByteId })
        {
            _topology.RemoveNode(line, nodeByteId);
            _health.RemoveNode(line, nodeByteId);
        }
    }

    private bool IsLockedOut(uint physicalId) =>
        _failures.TryGetValue(physicalId, out var f) && f.LockedUntil > _time.GetUtcNow();

    private void RecordFailure(uint physicalId)
    {
        var now = _time.GetUtcNow();
        var (failures, lockedUntil) = _failures.GetValueOrDefault(physicalId);
        if (lockedUntil != default && lockedUntil <= now)
        {
            (failures, lockedUntil) = (0, default); // Hết thời gian khóa: đếm lại từ đầu.
        }

        failures++;
        if (failures >= MaxFailedAttempts)
        {
            lockedUntil = now + LockoutDuration;
            _logger.LogWarning("[Join] Device {Phys:X8} locked out for {Minutes} min after {Count} failed authentications",
                physicalId, LockoutDuration.TotalMinutes, failures);
        }

        _failures[physicalId] = (failures, lockedUntil);
    }

    private async Task OnJoinRequestAsync(int lineId, uint physicalId, CancellationToken ct)
    {
        if (IsLockedOut(physicalId))
        {
            return;
        }

        var lookup = await _keys.FindAsync(physicalId, ct);
        if (lookup.Status != DeviceKeyStatus.Found)
        {
            var reason = lookup.Status == DeviceKeyStatus.Revoked ? "revoked" : "unknown";
            var isNew = !_pendingDevices.ContainsKey(physicalId);
            if (isNew && PendingDevices.Count >= MaxPendingDevices)
            {
                return;
            }

            var pending = new PendingDevice(physicalId, lineId, reason, _time.GetUtcNow());
            _pendingDevices[physicalId] = pending;
            if (isNew)
            {
                _logger.LogWarning("[Join] L{Line}: {Reason} device {Phys:X8} tried to join", lineId, reason, physicalId);
                await _notifier.BroadcastAsync(RealtimeNotifier.PendingDevice, pending);
            }

            return;
        }

        _pendingDevices.TryRemove(physicalId, out _);
        var nonce = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        _challenges[(lineId, physicalId)] = (nonce, _time.GetUtcNow() + TimeSpan.FromSeconds(_options.JoinChallengeTtlSeconds));
        await SendToUnassignedAsync(lineId, NodeCommand.JoinRequest, JoinProtocol.EncodeChallenge(physicalId, nonce), ct);
    }

    private async Task OnChallengeResponseAsync(int lineId, uint physicalId, uint mac, CancellationToken ct)
    {
        // Thách đố dùng một lần: lấy ra rồi xóa ngay, kể cả khi xác thực thất bại.
        if (!_challenges.TryRemove((lineId, physicalId), out var challenge) || challenge.Expires < _time.GetUtcNow())
        {
            _logger.LogWarning("[Join] L{Line}: response from {Phys:X8} without a valid challenge", lineId, physicalId);
            return;
        }

        var lookup = await _keys.FindAsync(physicalId, ct);
        if (lookup.Key is null || !JoinProtocol.VerifyMac(lookup.Key, challenge.Nonce, mac))
        {
            _logger.LogWarning("[Join] L{Line}: authentication FAILED for device {Phys:X8}", lineId, physicalId);
            RecordFailure(physicalId);
            return;
        }

        _failures.TryRemove(physicalId, out _);

        var nodeByteId = await AllocateAsync(lineId, physicalId, ct);
        _assigning[(lineId, physicalId)] = nodeByteId;

        var config = new NodeConfig(
            NodeId: nodeByteId,
            SampleIntervalMs: _options.DefaultSampleIntervalMs,
            IpAddress: NodeAddress(lineId, nodeByteId));
        await SendToUnassignedAsync(lineId, NodeCommand.WriteConfig, NodeAssignment.Encode(physicalId, nodeByteId, config), ct);
    }

    /// <summary>Node ID cũ nếu thiết bị đã từng ở tuyến này, ngược lại Node ID nhỏ nhất còn trống.</summary>
    private async Task<byte> AllocateAsync(int lineId, uint physicalId, CancellationToken ct)
    {
        await _allocationLock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existing = await db.Nodes.FirstOrDefaultAsync(n => n.PhysicalId == physicalId, ct);
            if (existing is { NodeByteId: { } knownId } && existing.LineId == lineId)
            {
                await EnsureCameraAsync(db, existing, ct);
                await db.SaveChangesAsync(ct);
                return knownId;
            }

            if (existing is not null)
            {
                // Thiết bị chuyển tuyến: bản ghi cũ giữ lịch sử nhưng không còn gắn với thiết bị.
                Detach(existing);
            }

            // Bản ghi node cũ (kể cả đã chuyển tuyến) vẫn giữ Node ID để khóa chính L{k}-N{n} không bị trùng.
            var used = (await db.Nodes.Where(n => n.LineId == lineId && n.NodeByteId != null)
                .Select(n => n.NodeByteId!.Value).ToListAsync(ct)).ToHashSet();
            var free = Enumerable.Range(1, 254).Select(i => (byte)i).FirstOrDefault(i => !used.Contains(i));
            if (free == 0)
            {
                throw new InvalidOperationException($"Line {lineId} has no free node id; add a new line");
            }

            var nodeId = DeviceIds.Node(lineId, free);
            var node = new Node
            {
                Id = nodeId,
                Code = nodeId,
                Name = $"Node {free} tuyến {lineId}",
                Description = string.Empty,
                LineId = lineId,
                NodeByteId = free,
                PhysicalId = physicalId,
                HardwareId = physicalId.ToString("X8"),
                IpAddress = NodeAddress(lineId, free).ToString(),
                Status = NodeStatus.Unknown
            };
            db.Nodes.Add(node);
            await EnsureCameraAsync(db, node, ct);
            await db.SaveChangesAsync(ct);
            return free;
        }
        finally
        {
            _allocationLock.Release();
        }
    }

    /// <summary>Mọi node đều có camera: tạo bản ghi CAM-L{k}-N{n} nếu chưa có và gắn vào node.</summary>
    private static async Task EnsureCameraAsync(AppDbContext db, Node node, CancellationToken ct)
    {
        var cameraId = DeviceIds.Camera(node.LineId!.Value, node.NodeByteId!.Value);
        node.CameraId = cameraId;
        if (await db.Cameras.AnyAsync(c => c.Id == cameraId, ct))
        {
            return;
        }

        db.Cameras.Add(new Camera
        {
            Id = cameraId,
            Name = $"Camera node {node.NodeByteId} tuyến {node.LineId}",
            Description = string.Empty,
            NodeId = node.Id,
            Protocol = CameraProtocol.WebSocket,
            StreamUrl = $"/ws/camera/{cameraId}/view",
            Status = CameraStatus.Offline
        });
    }

    /// <summary>Quy hoạch IP theo docs mục 3: node n của tuyến k là 10.k.1.n.</summary>
    public static IPAddress NodeAddress(int lineId, byte nodeByteId) => new([10, (byte)lineId, 1, nodeByteId]);

    private Task SendToUnassignedAsync(int lineId, NodeCommand command, byte[] payload, CancellationToken ct) =>
        _publisher.PublishAsync(
            GatewayTopics.NodeDownlink(lineId, GatewayTopics.UnassignedNodeId),
            WireFrameCodec.Encode((byte)command, GatewayTopics.UnassignedNodeId, payload),
            MqttQos.AtLeastOnce, false, ct);
}
