using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace WireProtocol
{
    /// <summary>Trạng thái node, mục II "Các thuộc tính của Node" (OFF, SLEEP, ON, BUSY).</summary>
    public enum NodePowerState : byte
    {
        Off = 0,
        Sleep = 1,
        On = 2,
        Busy = 3
    }

    /// <summary>Mã lỗi bit-wise của node, mục II. Ý nghĩa từng bit do docs/lan-architecture-protocol.md đề xuất.</summary>
    [Flags]
    public enum NodeErrorFlags : byte
    {
        None = 0,
        SensorFault = 1 << 0,
        ActuatorFault = 1 << 1,
        LowBattery = 1 << 2,
        StorageFault = 1 << 3,
        Rs485Fault = 1 << 4,
        ClockUnsynced = 1 << 5
    }

    /// <summary>Kết quả xử lý lệnh, 1 byte trong bản tin xác nhận của node.</summary>
    public enum CommandResultCode : byte
    {
        Ok = 0,
        Rejected = 1,
        InvalidPayload = 2,
        Busy = 3,
        Failed = 4
    }

    /// <summary>Loại ngoại vi trong bản tin A4: 4 bit cao của byte 1 (Đèn 0xAn, Còi 0xBn).</summary>
    public enum PeripheralKind : byte
    {
        Light = 0xA,
        Horn = 0xB
    }

    public readonly record struct PeripheralState(PeripheralKind Kind, byte Index, bool IsOn);

    /// <summary>
    /// Payload phản hồi duy trì kết nối A4 (Node → Gateway):
    /// Pin(float 4) | Trạng thái(1) | Mã lỗi(1) | n × [Loại+STT(1) | Bật/Tắt(1)].
    /// </summary>
    public sealed record KeepAliveStatus(
        float BatteryPercent,
        NodePowerState State,
        NodeErrorFlags Errors,
        IReadOnlyList<PeripheralState> Peripherals)
    {
        private const int FixedLength = 6;
        private const int BytesPerPeripheral = 2;

        public byte[] ToBytes()
        {
            var bytes = new byte[FixedLength + Peripherals.Count * BytesPerPeripheral];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, BatteryPercent);
            bytes[4] = (byte)State;
            bytes[5] = (byte)Errors;

            var offset = FixedLength;
            foreach (var p in Peripherals)
            {
                bytes[offset++] = (byte)(((byte)p.Kind << 4) | (p.Index & 0x0F));
                bytes[offset++] = p.IsOn ? (byte)1 : (byte)0;
            }

            return bytes;
        }

        public static bool TryParse(ReadOnlySpan<byte> data, out KeepAliveStatus? status)
        {
            status = null;
            if (data.Length < FixedLength || (data.Length - FixedLength) % BytesPerPeripheral != 0)
            {
                return false;
            }

            var peripherals = new List<PeripheralState>((data.Length - FixedLength) / BytesPerPeripheral);
            for (var offset = FixedLength; offset < data.Length; offset += BytesPerPeripheral)
            {
                peripherals.Add(new PeripheralState(
                    (PeripheralKind)(data[offset] >> 4), (byte)(data[offset] & 0x0F), data[offset + 1] != 0));
            }

            status = new KeepAliveStatus(
                BinaryPrimitives.ReadSingleLittleEndian(data),
                (NodePowerState)data[4],
                (NodeErrorFlags)data[5],
                peripherals);
            return true;
        }
    }

    /// <summary>
    /// Cấu hình node (A2 tải lên, A3 ghi xuống) dưới dạng TLV siêu dữ liệu 0x00 0x1n.
    /// Trường null nghĩa là không đổi (khi ghi) hoặc node không báo (khi đọc).
    /// </summary>
    public sealed record NodeConfig(
        byte? NodeId = null,
        uint? SampleIntervalMs = null,
        byte? NodeType = null,
        IPAddress? IpAddress = null)
    {
        public const byte NodeIdOrdinal = 0x10;
        public const byte SampleIntervalOrdinal = 0x11;
        public const byte NodeTypeOrdinal = 0x12;
        public const byte IpAddressOrdinal = 0x13;

        public IEnumerable<SensorTlvEntry> ToEntries()
        {
            if (NodeId is { } nodeId)
            {
                yield return new SensorTlvEntry(MetaTlv.TypeByte, NodeIdOrdinal, [nodeId]);
            }

            if (SampleIntervalMs is { } interval)
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, interval);
                yield return new SensorTlvEntry(MetaTlv.TypeByte, SampleIntervalOrdinal, bytes);
            }

            if (NodeType is { } nodeType)
            {
                yield return new SensorTlvEntry(MetaTlv.TypeByte, NodeTypeOrdinal, [nodeType]);
            }

            if (IpAddress is { } ip)
            {
                yield return new SensorTlvEntry(MetaTlv.TypeByte, IpAddressOrdinal, ip.MapToIPv4().GetAddressBytes());
            }
        }

        public byte[] ToBytes() => SensorTlvCodec.Encode(ToEntries());

        public static NodeConfig FromEntries(IEnumerable<SensorTlvEntry> entries)
        {
            var config = new NodeConfig();
            foreach (var e in entries)
            {
                if (e.TypeByte != MetaTlv.TypeByte)
                {
                    continue;
                }

                config = e.Seq switch
                {
                    NodeIdOrdinal when e.Value.Length == 1 => config with { NodeId = e.Value[0] },
                    SampleIntervalOrdinal when e.Value.Length == 4 =>
                        config with { SampleIntervalMs = BinaryPrimitives.ReadUInt32LittleEndian(e.Value) },
                    NodeTypeOrdinal when e.Value.Length == 1 => config with { NodeType = e.Value[0] },
                    IpAddressOrdinal when e.Value.Length == 4 => config with { IpAddress = new IPAddress(e.Value) },
                    _ => config
                };
            }

            return config;
        }

        // IPAddress không so sánh theo giá trị trong record equality mặc định, nên so sánh thủ công.
        public bool Equals(NodeConfig? other) =>
            other is not null
            && NodeId == other.NodeId
            && SampleIntervalMs == other.SampleIntervalMs
            && NodeType == other.NodeType
            && Equals(IpAddress, other.IpAddress);

        public override int GetHashCode() => HashCode.Combine(NodeId, SampleIntervalMs, NodeType, IpAddress);
    }

    /// <summary>Phần uplink A1: yêu cầu tham gia (4 byte) hoặc trả lời thách đố (8 byte).</summary>
    public readonly record struct JoinUplink(uint PhysicalId, uint? Mac);

    /// <summary>
    /// Lệnh A1, thêm node có xác thực thách đố (docs/lan-architecture-protocol.md mục 4.5).
    /// Mọi node chưa có ID đều dùng Node ID 0, nên payload luôn mang ID vật lý để node chỉ xử lý thông điệp của mình.
    /// </summary>
    public static class JoinProtocol
    {
        private const int RequestLength = 4;
        private const int PairLength = 8;

        public static byte[] EncodeRequest(uint physicalId)
        {
            var bytes = new byte[RequestLength];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, physicalId);
            return bytes;
        }

        public static byte[] EncodeChallenge(uint physicalId, uint nonce) => EncodePair(physicalId, nonce);

        public static byte[] EncodeResponse(uint physicalId, uint mac) => EncodePair(physicalId, mac);

        public static bool TryDecodeUplink(ReadOnlySpan<byte> payload, out JoinUplink uplink)
        {
            uplink = default;
            switch (payload.Length)
            {
                case RequestLength:
                    uplink = new JoinUplink(BinaryPrimitives.ReadUInt32LittleEndian(payload), null);
                    return true;
                case PairLength:
                    uplink = new JoinUplink(
                        BinaryPrimitives.ReadUInt32LittleEndian(payload),
                        BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]));
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryDecodeChallenge(ReadOnlySpan<byte> payload, out uint physicalId, out uint nonce)
        {
            physicalId = 0;
            nonce = 0;
            if (payload.Length != PairLength)
            {
                return false;
            }

            physicalId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            nonce = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            return true;
        }

        /// <summary>4 byte đầu (LE) của HMAC-SHA256(key, physicalId LE ‖ nonce LE).</summary>
        public static uint ComputeMac(ReadOnlySpan<byte> key, uint physicalId, uint nonce)
        {
            Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
            HMACSHA256.HashData(key, EncodePair(physicalId, nonce), hash);
            return BinaryPrimitives.ReadUInt32LittleEndian(hash);
        }

        public static bool VerifyMac(ReadOnlySpan<byte> key, uint physicalId, uint nonce, uint mac)
        {
            Span<byte> expected = stackalloc byte[4];
            Span<byte> actual = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(expected, ComputeMac(key, physicalId, nonce));
            BinaryPrimitives.WriteUInt32LittleEndian(actual, mac);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        /// <summary>
        /// Dẫn xuất khóa riêng của thiết bị từ một khóa gốc: HMAC-SHA256(masterSecret, physicalId LE).
        /// Cho phép nạp khóa hàng loạt khi sản xuất mà vẫn mỗi thiết bị một khóa khác nhau.
        /// </summary>
        public static byte[] DeriveDeviceKey(ReadOnlySpan<byte> masterSecret, uint physicalId)
        {
            Span<byte> id = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(id, physicalId);
            return HMACSHA256.HashData(masterSecret, id);
        }

        private static byte[] EncodePair(uint first, uint second)
        {
            var bytes = new byte[PairLength];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, first);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), second);
            return bytes;
        }
    }

    /// <summary>
    /// A3 với Node ID 0 trong khung, cấp Node ID cho node vừa xác thực:
    /// xuống: ID vật lý(4) | Node ID mới(1) | TLV cấu hình; lên: ID vật lý(4) | kết quả(1).
    /// </summary>
    public static class NodeAssignment
    {
        public static byte[] Encode(uint physicalId, byte nodeId, NodeConfig? config = null)
        {
            var configBytes = config?.ToBytes() ?? [];
            var bytes = new byte[5 + configBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, physicalId);
            bytes[4] = nodeId;
            configBytes.CopyTo(bytes, 5);
            return bytes;
        }

        public static bool TryDecode(ReadOnlySpan<byte> payload, out uint physicalId, out byte nodeId, out NodeConfig config)
        {
            physicalId = 0;
            nodeId = 0;
            config = new NodeConfig();
            if (payload.Length < 5)
            {
                return false;
            }

            physicalId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            nodeId = payload[4];
            config = NodeConfig.FromEntries(SensorTlvCodec.Parse(payload[5..]));
            return true;
        }

        public static byte[] EncodeConfirm(uint physicalId, CommandResultCode result)
        {
            var bytes = new byte[5];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, physicalId);
            bytes[4] = (byte)result;
            return bytes;
        }

        public static bool TryDecodeConfirm(ReadOnlySpan<byte> payload, out uint physicalId, out CommandResultCode result)
        {
            physicalId = 0;
            result = CommandResultCode.Failed;
            if (payload.Length != 5)
            {
                return false;
            }

            physicalId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            result = (CommandResultCode)payload[4];
            return true;
        }
    }

    /// <summary>Payload lệnh A6: danh sách TLV, mỗi mục là chế độ nguồn hoặc trạng thái một ngoại vi.</summary>
    public static class ControlPayload
    {
        public static SensorTlvEntry PowerMode(NodePowerState state) =>
            new(MetaTlv.TypeByte, MetaTlv.PowerModeOrdinal, [(byte)state]);

        public static SensorTlvEntry Actuator(ActuatorTypeByte type, byte ordinal, bool on) =>
            new((byte)type, ordinal, [on ? (byte)1 : (byte)0]);
    }

    /// <summary>Payload xác nhận A3/A5/A6 của node đã có ID: 1 byte <see cref="CommandResultCode"/>.</summary>
    public static class CommandAck
    {
        public static byte[] Encode(CommandResultCode code) => [(byte)code];

        public static bool TryDecode(ReadOnlySpan<byte> payload, out CommandResultCode code)
        {
            code = CommandResultCode.Failed;
            if (payload.Length != 1)
            {
                return false;
            }

            code = (CommandResultCode)payload[0];
            return true;
        }
    }
}
