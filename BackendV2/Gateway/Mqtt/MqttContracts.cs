namespace BackendV2.Gateway.Mqtt;

public enum MqttQos
{
    AtMostOnce = 0,
    AtLeastOnce = 1
}

/// <summary>Gửi bản tin lên broker. Ném <see cref="InvalidOperationException"/> khi đang mất kết nối.</summary>
public interface IMqttPublisher
{
    bool IsConnected { get; }

    Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, MqttQos qos, bool retain, CancellationToken ct);
}

/// <summary>Xử lý một bản tin nhận được. Phải tự bắt lỗi, không được làm sập vòng nhận MQTT.</summary>
public interface IMqttMessageHandler
{
    Task HandleAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct);
}
