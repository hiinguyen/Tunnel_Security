using MQTTnet;
using WireProtocol;

var brokerHost = Environment.GetEnvironmentVariable("MQTT_BROKER_HOST") ?? "localhost";
var brokerPort = int.Parse(Environment.GetEnvironmentVariable("MQTT_BROKER_PORT") ?? "1883");
var nodeId = byte.Parse(Environment.GetEnvironmentVariable("NODE_ID") ?? "1");
var intervalSeconds = double.Parse(Environment.GetEnvironmentVariable("PUBLISH_INTERVAL_SECONDS") ?? "1");

var mqttFactory = new MqttClientFactory();
using var mqttClient = mqttFactory.CreateMqttClient();

var options = new MqttClientOptionsBuilder()
    .WithClientId($"node-sim-{nodeId}")
    .WithTcpServer(brokerHost, brokerPort)
    .Build();

await mqttClient.ConnectAsync(options, CancellationToken.None);
Console.WriteLine($"[NodePublisherSim] Connected to {brokerHost}:{brokerPort} as node {nodeId}");

var random = new Random();
var topic = $"sensors/{nodeId}/reading";
byte seq = 0;

while (true)
{
    var entries = new List<SensorTlvEntry>
    {
        new((byte)SensorTypeByte.Light, seq, new LightSensorValue(NextInRange(random, 50, 800)).ToBytes()),
        new((byte)SensorTypeByte.WaterLevel, seq, new WaterLevelValue(NextInRange(random, 0, 3)).ToBytes()),
        new(
            (byte)SensorTypeByte.TemperatureHumidity,
            seq,
            new TemperatureHumidityValue(NextInRange(random, 20, 35), NextInRange(random, 40, 90)).ToBytes()),
        new((byte)SensorTypeByte.Radar, seq, BuildRadarValue(random).ToBytes())
    };

    var payload = SensorTlvCodec.Encode(entries);
    var frame = WireFrameCodec.Encode((byte)NodeCommand.SensorData, nodeId, payload);

    var message = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(frame)
        .Build();

    await mqttClient.PublishAsync(message, CancellationToken.None);
    Console.WriteLine($"[NodePublisherSim] Published seq={seq} to {topic} ({frame.Length} bytes)");

    seq = unchecked((byte)(seq + 1));
    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
}

static float NextInRange(Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);

static RadarValue BuildRadarValue(Random random)
{
    var count = random.Next(0, 4);
    var objects = new List<RadarObject>(count);

    for (var i = 0; i < count; i++)
    {
        objects.Add(new RadarObject(
            X: NextInRange(random, -5, 5),
            Y: NextInRange(random, -5, 5),
            Z: NextInRange(random, 0, 3),
            VelocityMps: NextInRange(random, 0, 2),
            DistanceM: NextInRange(random, 1, 50),
            FalseAlarmProbabilityPercent: NextInRange(random, 0, 5)));
    }

    return new RadarValue(objects);
}
