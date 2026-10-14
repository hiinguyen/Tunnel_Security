using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace BackendV2.Gateway.Mqtt;

/// <summary>
/// Quản lý tài khoản và ACL trên Mosquitto qua plugin Dynamic Security (topic <c>$CONTROL/dynamic-security/v1</c>),
/// để thêm/gỡ tuyến khi đang chạy mà không sửa file cấu hình hay khởi động lại broker (docs mục 4.4, 4.9).
/// </summary>
public sealed class DynamicSecurityClient
{
    private const string CommandTopic = "$CONTROL/dynamic-security/v1";
    private const string ProvisioningRole = "provisioning";
    private const string FullAccessRole = "client";

    private readonly IMqttPublisher _publisher;
    private readonly MqttOptions _options;
    private readonly ILogger<DynamicSecurityClient> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DynSecResult>> _pending = new();

    public DynamicSecurityClient(IMqttPublisher publisher, IOptions<MqttOptions> options, ILogger<DynamicSecurityClient> logger)
    {
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    public bool Enabled => _options.DynamicSecurity.Enabled;

    public static string LineUsername(int lineId) => $"line-{lineId}";

    /// <summary>Chạy mỗi lần kết nối: nhận phản hồi, cấp quyền đầy đủ cho chính backend, bảo đảm tài khoản cấp phát.</summary>
    public async Task OnConnectedAsync(MqttConnectionService connection, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        await connection.SubscribeAsync(MqttConnectionService.DynSecResponseTopic, ct);

        // Tài khoản admin do Mosquitto tạo chỉ có quyền $CONTROL; thêm role "client" để đọc/ghi cây ts/... .
        // Broker ngắt kết nối client ngay khi role của nó đổi, nên chỉ thêm một lần rồi để vòng kết nối lại xử lý tiếp.
        var selfRoles = await GetClientRolesAsync(_options.Username!, ct)
            ?? throw new InvalidOperationException($"DynSec client '{_options.Username}' not found");
        if (!selfRoles.Contains(FullAccessRole))
        {
            await _publisher.PublishAsync(CommandTopic, JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["commands"] = new JsonArray(AddClientRole(_options.Username!, FullAccessRole, priority: 10))
            }), MqttQos.AtLeastOnce, false, ct);
            throw new InvalidOperationException($"Granted role '{FullAccessRole}' to '{_options.Username}', broker will reconnect this client");
        }

        await EnsureDefaultDenyAsync(ct);

        await ExecuteIgnoringAsync(["already exists"], ct, new JsonObject
        {
            ["command"] = "createRole",
            ["rolename"] = ProvisioningRole,
            ["textdescription"] = "NM chưa được cấp tuyến: chỉ gửi yêu cầu và nhận kết quả của chính mình",
            ["acls"] = new JsonArray(
                Acl("publishClientSend", "ts/prov/%c/req"),
                Acl("subscribePattern", "ts/prov/%c/resp"))
        });

        var provisioningPassword = _options.DynamicSecurity.ProvisioningPassword;
        if (string.IsNullOrEmpty(provisioningPassword))
        {
            _logger.LogWarning("[DynSec] Mqtt:DynamicSecurity:ProvisioningPassword is empty, new gateways cannot connect");
            return;
        }

        await UpsertClientAsync(_options.DynamicSecurity.ProvisioningUsername, provisioningPassword, ProvisioningRole, ct);
        _logger.LogInformation("[DynSec] Backend role and provisioning account ensured");
    }

    /// <summary>Tạo (hoặc cấp lại mật khẩu cho) tài khoản <c>line-{k}</c> chỉ được dùng cây <c>ts/{k}/...</c>.</summary>
    public async Task<string> EnsureLineAccountAsync(int lineId, CancellationToken ct)
    {
        var role = LineUsername(lineId);
        await ExecuteIgnoringAsync(["already exists"], ct, new JsonObject
        {
            ["command"] = "createRole",
            ["rolename"] = role,
            ["textdescription"] = $"NM tuyến {lineId}",
            ["acls"] = new JsonArray(
                Acl("publishClientSend", $"ts/{lineId}/gw/#"),
                Acl("publishClientSend", $"ts/{lineId}/n/+/up/#"),
                Acl("subscribePattern", GatewayTopics.LineDownlinkFilter(lineId)))
        });

        var password = GeneratePassword();
        await UpsertClientAsync(LineUsername(lineId), password, role, ct);
        return password;
    }

    public async Task DeleteLineAccountAsync(int lineId, CancellationToken ct)
    {
        await ExecuteIgnoringAsync(["not found"], ct,
            new JsonObject { ["command"] = "deleteClient", ["username"] = LineUsername(lineId) },
            new JsonObject { ["command"] = "deleteRole", ["rolename"] = LineUsername(lineId) });
    }

    public void OnResponse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var response in doc.RootElement.GetProperty("responses").EnumerateArray())
            {
                var correlation = response.TryGetProperty("correlationData", out var c) ? c.GetString() : null;
                if (correlation is null || !_pending.TryRemove(correlation, out var tcs))
                {
                    continue;
                }

                var command = response.GetProperty("command").GetString() ?? "";
                var error = response.TryGetProperty("error", out var e) ? e.GetString() : null;
                JsonElement? data = response.TryGetProperty("data", out var d) ? d.Clone() : null;
                tcs.TrySetResult(new DynSecResult(command, error, data));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning("[DynSec] Malformed response: {Message}", ex.Message);
        }
    }

    private async Task UpsertClientAsync(string username, string password, string role, CancellationToken ct)
    {
        var roles = await GetClientRolesAsync(username, ct);
        if (roles is null)
        {
            await ExecuteIgnoringAsync([], ct, new JsonObject
            {
                ["command"] = "createClient",
                ["username"] = username,
                ["password"] = password,
                ["roles"] = new JsonArray(new JsonObject { ["rolename"] = role })
            });
            return;
        }

        await ExecuteIgnoringAsync([], ct,
            new JsonObject { ["command"] = "setClientPassword", ["username"] = username, ["password"] = password });

        // Gán lại role đã có làm Mosquitto 2.1 trả "Internal error", nên chỉ gán khi còn thiếu.
        if (!roles.Contains(role))
        {
            await ExecuteIgnoringAsync([], ct, AddClientRole(username, role, priority: 0));
        }
    }

    /// <summary>
    /// Cách ly giữa các tuyến dựa hoàn toàn vào ACL, nên mặc định phải là từ chối publish và subscribe
    /// (chỉ cho phép những gì role cho phép), không phụ thuộc vào mặc định của phiên bản Mosquitto.
    /// Chỉ ghi khi khác, vì đổi ACL mặc định có thể làm broker ngắt kết nối các client.
    /// </summary>
    private async Task EnsureDefaultDenyAsync(CancellationToken ct)
    {
        var current = (await ExecuteAsync(ct, new JsonObject { ["command"] = "getDefaultACLAccess" }))[0];
        var wanted = new Dictionary<string, bool>
        {
            ["publishClientSend"] = false,
            ["publishClientReceive"] = true,
            ["subscribe"] = false,
            ["unsubscribe"] = true
        };

        var actual = new Dictionary<string, bool>();
        if (current.Data is { } data && data.TryGetProperty("acls", out var acls))
        {
            foreach (var acl in acls.EnumerateArray())
            {
                actual[acl.GetProperty("acltype").GetString()!] = acl.GetProperty("allow").GetBoolean();
            }
        }

        if (wanted.All(w => actual.TryGetValue(w.Key, out var a) && a == w.Value))
        {
            return;
        }

        _logger.LogWarning("[DynSec] Default ACL was not deny-by-default, fixing it");
        await ExecuteIgnoringAsync([], ct, new JsonObject
        {
            ["command"] = "setDefaultACLAccess",
            ["acls"] = new JsonArray(wanted.Select(w => (JsonNode)new JsonObject { ["acltype"] = w.Key, ["allow"] = w.Value }).ToArray())
        });
    }

    /// <summary>Danh sách role của client, hoặc null nếu client chưa tồn tại.</summary>
    private async Task<IReadOnlySet<string>?> GetClientRolesAsync(string username, CancellationToken ct)
    {
        var result = (await ExecuteAsync(ct, new JsonObject { ["command"] = "getClient", ["username"] = username }))[0];
        if (result.Error is not null)
        {
            return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new InvalidOperationException($"DynSec getClient {username} failed: {result.Error}");
        }

        var roles = new HashSet<string>();
        if (result.Data is { } data && data.TryGetProperty("client", out var client) && client.TryGetProperty("roles", out var list))
        {
            foreach (var r in list.EnumerateArray())
            {
                if (r.TryGetProperty("rolename", out var name) && name.GetString() is { } n)
                {
                    roles.Add(n);
                }
            }
        }

        return roles;
    }

    /// <summary>Thực thi, chấp nhận lỗi có chứa một trong các chuỗi cho trước (ví dụ "already exists"), ném lỗi còn lại.</summary>
    private async Task ExecuteIgnoringAsync(string[] tolerated, CancellationToken ct, params JsonObject[] commands)
    {
        var results = await ExecuteAsync(ct, commands);
        foreach (var r in results)
        {
            if (r.Error is not null && !tolerated.Any(t => r.Error.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"DynSec {r.Command} failed: {r.Error}");
            }
        }
    }

    private async Task<IReadOnlyList<DynSecResult>> ExecuteAsync(CancellationToken ct, params JsonObject[] commands)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Dynamic security is disabled");
        }

        var waits = new List<Task<DynSecResult>>(commands.Length);
        var ids = new List<string>(commands.Length);
        foreach (var command in commands)
        {
            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<DynSecResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            command["correlationData"] = id;
            ids.Add(id);
            waits.Add(tcs.Task);
        }

        try
        {
            var body = new JsonObject { ["commands"] = new JsonArray(commands.Cast<JsonNode>().ToArray()) };
            await _publisher.PublishAsync(CommandTopic, JsonSerializer.SerializeToUtf8Bytes(body), MqttQos.AtLeastOnce, false, ct);
            return await Task.WhenAll(waits)
                .WaitAsync(TimeSpan.FromSeconds(_options.DynamicSecurity.CommandTimeoutSeconds), ct);
        }
        finally
        {
            foreach (var id in ids)
            {
                _pending.TryRemove(id, out _);
            }
        }
    }

    private static JsonObject AddClientRole(string username, string role, int priority) => new()
    {
        ["command"] = "addClientRole",
        ["username"] = username,
        ["rolename"] = role,
        ["priority"] = priority
    };

    private static JsonObject Acl(string type, string topic) => new()
    {
        ["acltype"] = type,
        ["topic"] = topic,
        ["priority"] = 0,
        ["allow"] = true
    };

    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');

    private readonly record struct DynSecResult(string Command, string? Error, JsonElement? Data = null);
}
