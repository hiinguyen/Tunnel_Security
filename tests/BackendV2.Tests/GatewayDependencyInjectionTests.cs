using BackendV2.Gateway;
using BackendV2.Gateway.Mqtt;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackendV2.Tests;

public class GatewayDependencyInjectionTests
{
    [Fact]
    public async Task All_gateway_services_resolve_without_cycles()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddGateway(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Vòng phụ thuộc qua factory không bị DI phát hiện mà đệ quy vô hạn, nên giới hạn thời gian để test không treo.
        var resolve = Task.Run(() =>
        {
            var hosted = provider.GetServices<IHostedService>().ToList();
            var handler = provider.GetRequiredService<IMqttMessageHandler>();
            return (hosted, handler);
        });

        var (hosted, router) = await resolve.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<GatewayMessageRouter>(router);
        Assert.Contains(hosted, h => h is MqttConnectionService);
        Assert.Equal(8, hosted.Count(h => h.GetType().Namespace!.StartsWith("BackendV2.Gateway")));
    }
}
