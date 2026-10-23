namespace WireProtocol
{
    /// <summary>
    /// Format bản tin — mục V "GIAO THỨC TRUYỀN TIN NODE-GATEWAY":
    /// Start(1) | Command(1) | Length(2, uint16 LE) | NodeId(1) | Payload(Length) | CRC(2, uint16 LE) | Stop(1)
    ///
    /// "Byte thấp trước, byte cao sau" → little-endian cho Length và CRC.
    /// "Length" ở đây được hiểu là độ dài Payload (không tính Start/Command/Length/NodeId/CRC/Stop),
    /// nhất quán với cách dùng "Length" trong TLV cảm biến bên trong Payload.
    /// CRC tính trên toàn bộ byte từ Start tới hết Payload ("checksum các byte trước nó").
    /// </summary>
    public static class WireFrameCodec
    {
        public const byte StartByte = 0x53;
        public const byte StopByte = 0x4D;

        private const int HeaderLength = 5; // Start + Command + Length(2) + NodeId
        private const int TrailerLength = 3; // CRC(2) + Stop

        public static byte[] Encode(byte command, byte nodeId, byte[] payload)
        {
            var length = (ushort)payload.Length;
            var frame = new byte[HeaderLength + payload.Length + TrailerLength];

            var i = 0;
            frame[i++] = StartByte;
            frame[i++] = command;
            frame[i++] = (byte)(length & 0xFF);
            frame[i++] = (byte)((length >> 8) & 0xFF);
            frame[i++] = nodeId;

            Array.Copy(payload, 0, frame, i, payload.Length);
            i += payload.Length;

            var crc = Crc16.Compute(frame.AsSpan(0, i));
            frame[i++] = (byte)(crc & 0xFF);
            frame[i++] = (byte)((crc >> 8) & 0xFF);
            frame[i] = StopByte;

            return frame;
        }

        public static bool TryDecode(ReadOnlySpan<byte> data, out WireFrame? frame, out string? error)
        {
            frame = null;
            error = null;

            var minLength = HeaderLength + TrailerLength;
            if (data.Length < minLength)
            {
                error = $"Frame too short: {data.Length} bytes (minimum {minLength})";
                return false;
            }

            if (data[0] != StartByte)
            {
                error = $"Invalid start byte 0x{data[0]:X2} (expected 0x{StartByte:X2})";
                return false;
            }

            if (data[^1] != StopByte)
            {
                error = $"Invalid stop byte 0x{data[^1]:X2} (expected 0x{StopByte:X2})";
                return false;
            }

            var command = data[1];
            var length = (ushort)(data[2] | (data[3] << 8));
            var nodeId = data[4];

            var expectedTotal = HeaderLength + length + TrailerLength;
            if (data.Length != expectedTotal)
            {
                error = $"Length mismatch: header says {length} payload bytes, frame is {data.Length} bytes " +
                        $"(expected {expectedTotal})";
                return false;
            }

            var payload = data.Slice(HeaderLength, length).ToArray();

            var crcReceived = (ushort)(data[HeaderLength + length] | (data[HeaderLength + length + 1] << 8));
            var crcComputed = Crc16.Compute(data[..(HeaderLength + length)]);
            if (crcReceived != crcComputed)
            {
                error = $"CRC mismatch: received 0x{crcReceived:X4}, computed 0x{crcComputed:X4}";
                return false;
            }

            frame = new WireFrame(command, nodeId, payload);
            return true;
        }
    }
}
