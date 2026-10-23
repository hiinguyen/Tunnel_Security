using System.Buffers.Binary;

namespace WireProtocol
{
    /// <summary>
    /// TLV siêu dữ liệu, dùng byte loại 0x00 (chưa được gán trong "Cấu trúc khung truyền":
    /// 0x01-0x7F cảm biến, 0x80-0xFF ngoại vi), nên parser cũ chỉ coi đây là loại không biết và bỏ qua.
    /// </summary>
    public static class MetaTlv
    {
        public const byte TypeByte = 0x00;

        /// <summary>Thời điểm đo: uint64 LE, Unix time mili giây, UTC. Đứng đầu payload A7 nếu có.</summary>
        public const byte TimestampOrdinal = 0x01;

        /// <summary>Chế độ nguồn của node trong lệnh A6: 1 byte <see cref="NodePowerState"/>.</summary>
        public const byte PowerModeOrdinal = 0x02;

        private const int TimestampLength = 8;

        public static SensorTlvEntry Timestamp(DateTimeOffset at)
        {
            var bytes = new byte[TimestampLength];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)at.ToUnixTimeMilliseconds());
            return new SensorTlvEntry(TypeByte, TimestampOrdinal, bytes);
        }

        public static bool TryGetTimestamp(IEnumerable<SensorTlvEntry> entries, out DateTimeOffset at)
        {
            foreach (var entry in entries)
            {
                if (entry.TypeByte == TypeByte && entry.Seq == TimestampOrdinal && entry.Value.Length == TimestampLength)
                {
                    var millis = BinaryPrimitives.ReadUInt64LittleEndian(entry.Value);
                    if (millis <= (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                    {
                        at = DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
                        return true;
                    }
                }
            }

            at = default;
            return false;
        }
    }
}
