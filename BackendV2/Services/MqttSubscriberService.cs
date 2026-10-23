using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using BackendV2.Data;
using BackendV2.Hubs;
using BackendV2.Models;
using MQTTnet;
using WireProtocol;


namespace BackendV2.Services
{
    /// <summary>
    /// Subscribe topic "sensors/{nodeId}/reading" — mỗi message là 1 khung nhị phân theo
    /// "GIAO THỨC TRUYỀN TIN NODE-GATEWAY" (Start 0x53 .. Stop 0x4D), Node ID nằm trong header
    /// nên "+" ở topic filter giờ đại diện cho node, không còn là sensorId như bản JSON cũ.
    /// </summary>
    public class MqttSubscriberService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHubContext<SensorHub> _hubContext;

        public MqttSubscriberService(IServiceScopeFactory scopeFactory, IHubContext<SensorHub> hubContext)
        {
            _scopeFactory = scopeFactory;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var mqttFactory = new MqttClientFactory();
            using var mqttClient = mqttFactory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("localhost", 1883)
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += HandleMessageAsync;

            await mqttClient.ConnectAsync(options, stoppingToken);

            var subscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter("sensors/+/reading")
                .Build();

            await mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var bytes = e.ApplicationMessage.Payload.ToArray();

            if (!WireFrameCodec.TryDecode(bytes, out var frame, out var error) || frame is null)
            {
                Console.WriteLine($"[MQTT] Discarding invalid frame: {error}");
                return;
            }

            if (frame.Command != (byte)NodeCommand.SensorData)
            {
                return; // Luồng này chỉ xử lý 0xA7 (truyền dữ liệu cảm biến); các mã lệnh khác bỏ qua.
            }

            var readings = DecodeSensorReadings(frame.NodeId, frame.Payload);
            if (readings.Count == 0) return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TimeSeriesDbContext>();

                db.Readings.AddRange(readings);
                await db.SaveChangesAsync();

                foreach (var reading in readings)
                {
                    await _hubContext.Clients.All.SendAsync("NewReading", reading);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving reading: {ex.Message}");
            }
        }

        // Mỗi TLV entry giải mã theo mục III/IV "Cấu trúc dữ liệu của các cảm biến, đối tượng
        // điều khiển": type byte 0x01-0x04 = cảm biến ánh sáng/mực nước/nhiệt-ẩm/radar.
        private static List<Reading> DecodeSensorReadings(byte nodeId, byte[] payload)
        {
            var readings = new List<Reading>();
            var timestamp = DateTime.UtcNow;

            foreach (var entry in SensorTlvCodec.Parse(payload))
            {
                switch (entry.TypeByte)
                {
                    case (byte)SensorTypeByte.Light:
                    {
                        if (entry.Value.Length < 4) break;
                        var lux = LightSensorValue.FromBytes(entry.Value).Lux;
                        readings.Add(BuildReading(nodeId, entry, "light", timestamp, lux, "Cường độ ánh sáng (lx)"));
                        break;
                    }

                    case (byte)SensorTypeByte.WaterLevel:
                    {
                        if (entry.Value.Length < 4) break;
                        var depth = WaterLevelValue.FromBytes(entry.Value).DepthMeters;
                        readings.Add(BuildReading(nodeId, entry, "water", timestamp, depth, "Độ sâu mực nước (m)"));
                        break;
                    }

                    case (byte)SensorTypeByte.TemperatureHumidity:
                    {
                        if (entry.Value.Length < 8) break;
                        var th = TemperatureHumidityValue.FromBytes(entry.Value);
                        readings.Add(BuildReading(nodeId, entry, "temp", timestamp, th.TemperatureC, "Nhiệt độ (°C)"));
                        readings.Add(BuildReading(nodeId, entry, "hum", timestamp, th.HumidityPercent, "Độ ẩm (%)"));
                        break;
                    }

                    case (byte)SensorTypeByte.Radar:
                    {
                        var radar = RadarValue.FromBytes(entry.Value);
                        var reading = BuildReading(
                            nodeId, entry, "radar", timestamp, radar.Objects.Count, "Radar — số đối tượng phát hiện");
                        reading.Description += " | " + JsonSerializer.Serialize(radar.Objects);
                        readings.Add(reading);
                        break;
                    }

                    default:
                        Console.WriteLine($"[MQTT] Unknown sensor type byte 0x{entry.TypeByte:X2} — skipped");
                        break;
                }
            }

            return readings;
        }

        private static Reading BuildReading(
            byte nodeId, SensorTlvEntry entry, string suffix, DateTime timestamp, double value, string description) =>
            new()
            {
                // Không nhúng entry.Seq vào đây — Seq đổi mỗi lần publish nên sẽ không bao giờ
                // khớp Sensor.Id tĩnh trong AppDbContext (xem TopologySeeder, seed "node-{id}-{suffix}").
                SensorId = $"node-{nodeId}-{suffix}",
                NodeId = nodeId.ToString(),
                NodeByteId = nodeId,
                SensorByteId = entry.TypeByte,
                Seq = entry.Seq,
                Timestamp = timestamp,
                Value = value,
                Description = description,
                Crc8Ok = true // Frame đã qua CRC16 check ở WireFrameCodec.TryDecode trước khi tới đây.
            };
    }
}