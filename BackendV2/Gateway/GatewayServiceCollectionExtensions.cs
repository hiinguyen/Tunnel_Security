using BackendV2.Data;
using BackendV2.Gateway.Alerts;
using BackendV2.Gateway.Commands;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Ingestion;
using BackendV2.Gateway.Mqtt;
using BackendV2.Gateway.Provisioning;
using BackendV2.Gateway.Realtime;
using BackendV2.Gateway.Uplink;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BackendV2.Gateway;

public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký toàn bộ phía PC của giao thức PC - NM. Thứ tự AddHostedService là thứ tự khởi động:
    /// nạp topology và trạng thái trước, kết nối MQTT sau cùng để không bản tin nào tới khi service chưa sẵn sàng.
    /// </summary>
    public static IServiceCollection AddGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MqttOptions>(configuration.GetSection(MqttOptions.Section));
        services.Configure<GatewayOptions>(configuration.GetSection(GatewayOptions.Section));
        services.Configure<StationOptions>(configuration.GetSection(StationOptions.Section));
        services.Configure<ReadingWriterOptions>(configuration.GetSection(ReadingWriterOptions.Section));
        services.Configure<SimulationOptions>(configuration.GetSection(SimulationOptions.Section));

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<TopologyCache>();
        services.AddSingleton(sp => new SensorLevelTracker(sp.GetRequiredService<IOptions<GatewayOptions>>().Value.LevelRecoverySamples));
        services.AddSingleton<RealtimeNotifier>();
        services.AddSingleton<SensorIngestion>();
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<DynamicSecurityClient>();
        services.AddSingleton<DeviceKeyStore>();
        services.AddSingleton<GatewayProvisioningService>();
        services.AddSingleton<NodeJoinService>();
        services.AddSingleton<IMqttMessageHandler, GatewayMessageRouter>();

        services.AddSingleton<SensorCatalog>();
        services.AddSingleton<ReadingWriter>();
        services.AddSingleton<UplinkPublisher>();
        services.AddSingleton<AlertEngine>();
        services.AddSingleton<DeviceHealthService>();
        services.AddSingleton(sp =>
        {
            var connection = ActivatorUtilities.CreateInstance<MqttConnectionService>(sp);
            // DynamicSecurityClient cần IMqttPublisher (chính connection này), nên chỉ resolve khi đã kết nối,
            // không resolve trong factory, nếu không DI sẽ đệ quy vô hạn.
            connection.OnConnected(ct => sp.GetRequiredService<DynamicSecurityClient>().OnConnectedAsync(connection, ct));
            connection.HandleInline(MqttConnectionService.DynSecResponseTopic,
                payload => sp.GetRequiredService<DynamicSecurityClient>().OnResponse(payload));
            return connection;
        });
        services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttConnectionService>());

        services.AddHostedService<TopologyLoader>();
        services.AddHostedService(sp => sp.GetRequiredService<SensorCatalog>());
        services.AddHostedService(sp => sp.GetRequiredService<ReadingWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<AlertEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<DeviceHealthService>());
        services.AddHostedService(sp => sp.GetRequiredService<UplinkPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<GatewayProvisioningService>());
        services.AddHostedService(sp => sp.GetRequiredService<MqttConnectionService>());

        return services;
    }

    private sealed class TopologyLoader(TopologyCache topology, IServiceScopeFactory scopeFactory, ILogger<TopologyLoader> logger)
        : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            await topology.LoadAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken);
            logger.LogInformation("[Topology] Loaded {Lines} lines, {Nodes} nodes", topology.Lines.Count, topology.Nodes.Count);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
