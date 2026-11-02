using System.Text;
using BackendV2.Data;
using BackendV2.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace BackendV2.Gateway.Provisioning;

public enum DeviceKeyStatus
{
    Found,
    Unknown,
    Revoked
}

public sealed record DeviceKeyLookup(DeviceKeyStatus Status, byte[]? Key);

/// <summary>
/// Khóa bí mật của từng thiết bị, lưu đã mã hóa bằng ASP.NET Data Protection.
/// Khi <see cref="SimulationOptions.DeviceSecret"/> có giá trị (chỉ khi phát triển), thiết bị trong dải ID giả lập
/// được tự khai báo với khóa dẫn xuất từ secret đó, để NodePublisherSim chạy mà không cần khai báo tay.
/// </summary>
public sealed class DeviceKeyStore
{
    public const uint SimulationRangeStart = 0x5100_0000;
    public const uint SimulationRangeEnd = 0x51FF_FFFF;
    public const int MinKeyBytes = 16;

    private readonly IDataProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly byte[]? _simulationSecret;

    public DeviceKeyStore(IDataProtectionProvider protection, IServiceScopeFactory scopeFactory, IOptions<SimulationOptions> simulation)
    {
        _protector = protection.CreateProtector("TunnelSecurity.DeviceIdentity.Key.v1");
        _scopeFactory = scopeFactory;
        var secret = simulation.Value.DeviceSecret;
        _simulationSecret = string.IsNullOrEmpty(secret) ? null : Encoding.UTF8.GetBytes(secret);
    }

    public string Protect(byte[] key) => Convert.ToBase64String(_protector.Protect(key));

    public async Task<DeviceKeyLookup> FindAsync(uint physicalId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identity = await db.DeviceIdentities.AsNoTracking().FirstOrDefaultAsync(d => d.PhysicalId == physicalId, ct);

        if (identity is not null)
        {
            return identity.RevokedAt is null
                ? new DeviceKeyLookup(DeviceKeyStatus.Found, _protector.Unprotect(Convert.FromBase64String(identity.ProtectedKey)))
                : new DeviceKeyLookup(DeviceKeyStatus.Revoked, null);
        }

        if (_simulationSecret is null || physicalId is < SimulationRangeStart or > SimulationRangeEnd)
        {
            return new DeviceKeyLookup(DeviceKeyStatus.Unknown, null);
        }

        var key = JoinProtocol.DeriveDeviceKey(_simulationSecret, physicalId);
        db.DeviceIdentities.Add(new DeviceIdentity
        {
            PhysicalId = physicalId,
            Label = "NodePublisherSim",
            ProtectedKey = Protect(key)
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Hai yêu cầu cùng lúc cho cùng thiết bị: bản kia đã ghi trước, khóa giống hệt nên bỏ qua.
        }

        return new DeviceKeyLookup(DeviceKeyStatus.Found, key);
    }
}
