using System.Buffers.Binary;

namespace WireProtocol
{
    /// <summary>Payload bắt tay A1/A3 giữa node và gateway theo SDD 4.2.3.</summary>
    public static class GsanJoin
    {
        public static readonly byte[] AssignmentOk = "OK"u8.ToArray();

        public static byte[] EncodeRequest(uint physicalId) => U32(physicalId);

        public static bool TryDecodeRequest(ReadOnlySpan<byte> payload, out uint physicalId) => TryU32(payload, out physicalId);

        public static byte[] EncodeChallenge(uint nonce) => U32(nonce);

        public static bool TryDecodeChallenge(ReadOnlySpan<byte> payload, out uint nonce) => TryU32(payload, out nonce);

        public static byte[] EncodeResponse(uint mac) => U32(mac);

        public static bool TryDecodeResponse(ReadOnlySpan<byte> payload, out uint mac) => TryU32(payload, out mac);

        /// <summary>A3 cấp địa chỉ: Node ID trong khung và payload 1 byte đều là ID mới.</summary>
        public static byte[] EncodeAssignment(byte nodeId) => [nodeId];

        public static bool IsAssignmentOk(ReadOnlySpan<byte> payload) => payload.SequenceEqual(AssignmentOk);

        private static byte[] U32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return bytes;
        }

        private static bool TryU32(ReadOnlySpan<byte> payload, out uint value)
        {
            value = payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
            return payload.Length == 4;
        }
    }

    [Flags]
    public enum GsanSensorFlags : byte
    {
        None = 0,
        Radar = 1 << 0,
        Vxl = 1 << 1,
        Camera = 1 << 2
    }

    /// <summary>Heartbeat 0xA4 của node, 7 byte (SDD Bảng 5.3).</summary>
    public readonly record struct GsanHeartbeat(byte Status, byte Error, GsanSensorFlags Sensors, float CpuTempC)
    {
        public const int Length = 7;

        public byte[] ToBytes()
        {
            var bytes = new byte[Length];
            bytes[0] = Status;
            bytes[1] = Error;
            bytes[2] = (byte)Sensors;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(3), CpuTempC);
            return bytes;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out GsanHeartbeat heartbeat)
        {
            heartbeat = default;
            if (payload.Length != Length)
            {
                return false;
            }

            heartbeat = new GsanHeartbeat(payload[0], payload[1], (GsanSensorFlags)payload[2],
                BinaryPrimitives.ReadSingleLittleEndian(payload[3..]));
            return true;
        }

        /// <summary>
        /// Đổi sang trạng thái A4 của giao thức NM - PC. Pi không có pin nên pin là NaN ("không có").
        /// Thiếu radar hoặc camera (hai thiết bị bắt buộc) được báo là lỗi cảm biến.
        /// </summary>
        public KeepAliveStatus ToKeepAlive()
        {
            var state = Status <= (byte)NodePowerState.Busy ? (NodePowerState)Status : NodePowerState.On;
            var errors = (NodeErrorFlags)Error;
            const GsanSensorFlags required = GsanSensorFlags.Radar | GsanSensorFlags.Camera;
            if ((Sensors & required) != required)
            {
                errors |= NodeErrorFlags.SensorFault;
            }

            return new KeepAliveStatus(float.NaN, state, errors, []);
        }
    }
}
