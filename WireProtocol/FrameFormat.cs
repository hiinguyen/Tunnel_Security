namespace WireProtocol
{
    public delegate ushort CrcFunction(ReadOnlySpan<byte> data);

    /// <summary>
    /// Cùng khung 0x53 ... 0x4D nhưng hai chặng dùng CRC khác nhau:
    /// node - NM theo SDD (CRC16-Modbus trên [cmd..payload]), NM - PC theo WireProtocol (CCITT-FALSE trên [Start..payload]).
    /// </summary>
    public sealed class FrameFormat
    {
        public static readonly FrameFormat Lan = new(Crc16.Compute, crcStartOffset: 0);
        public static readonly FrameFormat Gsan = new(Crc16Modbus.Compute, crcStartOffset: 1);

        private readonly CrcFunction _crc;
        private readonly int _crcStartOffset;

        private FrameFormat(CrcFunction crc, int crcStartOffset)
        {
            _crc = crc;
            _crcStartOffset = crcStartOffset;
        }

        /// <param name="headerAndPayload">Khung từ Start tới hết Payload.</param>
        public ushort ComputeCrc(ReadOnlySpan<byte> headerAndPayload) => _crc(headerAndPayload[_crcStartOffset..]);
    }
}
