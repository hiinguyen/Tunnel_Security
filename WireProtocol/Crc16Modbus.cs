namespace WireProtocol
{
    /// <summary>CRC-16/MODBUS (poly phản xạ 0xA001, init 0xFFFF), dùng cho khung GSAN-CONG của node (SDD 4.2.1).</summary>
    public static class Crc16Modbus
    {
        public static ushort Compute(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
                }
            }

            return crc;
        }
    }
}
