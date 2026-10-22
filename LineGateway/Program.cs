using LineGateway;
using LineGateway.Gsan;
using LineGateway.Mqtt;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSystemd();
builder.Services.Configure<LineGatewayOptions>(builder.Configuration.GetSection(LineGatewayOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NodeSessionTable>();
builder.Services.AddSingleton<DownlinkRouter>();
builder.Services.AddSingleton<MqttLineUplink>();
builder.Services.AddSingleton<ILineUplink>(sp => sp.GetRequiredService<MqttLineUplink>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttLineUplink>());
builder.Services.AddHostedService<GsanServer>();
// Task 12 thêm: CameraForwarderPool, VideoReceiver, MetaReceiver.

builder.Build().Run();
