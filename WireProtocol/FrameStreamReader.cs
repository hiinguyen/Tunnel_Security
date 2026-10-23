namespace WireProtocol
{
    /// <summary>
    /// Tách khung 0x53 ... 0x4D từ một luồng byte (TCP). Khung lỗi bị bỏ từng byte cho tới Start kế tiếp,
    /// nên một khung hỏng không làm mất các khung sau. Bộ đệm không vượt quá một khung lớn nhất cộng một lần đọc.
    /// </summary>
    public sealed class FrameStreamReader
    {
        public const int DefaultMaxPayload = 16 * 1024;

        private readonly FrameFormat _format;
        private readonly int _maxPayload;
        private byte[] _buffer = new byte[4096];
        private int _count;

        public FrameStreamReader(FrameFormat format, int maxPayload = DefaultMaxPayload)
        {
            _format = format;
            _maxPayload = maxPayload;
        }

        public long DiscardedBytes { get; private set; }

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_count + data.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + data.Length));
            }

            data.CopyTo(_buffer.AsSpan(_count));
            _count += data.Length;
        }

        public bool TryRead(out WireFrame? frame)
        {
            frame = null;
            while (true)
            {
                var start = Array.IndexOf(_buffer, WireFrameCodec.StartByte, 0, _count);
                if (start < 0)
                {
                    Discard(_count);
                    return false;
                }

                Discard(start);
                if (_count < WireFrameCodec.HeaderLength)
                {
                    return false;
                }

                var length = _buffer[2] | (_buffer[3] << 8);
                if (length > _maxPayload)
                {
                    Discard(1);
                    continue;
                }

                var total = WireFrameCodec.HeaderLength + length + WireFrameCodec.TrailerLength;
                if (_count < total)
                {
                    return false;
                }

                if (WireFrameCodec.TryDecode(_buffer.AsSpan(0, total), out frame, out _, _format))
                {
                    Consume(total);
                    return true;
                }

                Discard(1);
            }
        }

        private void Discard(int n)
        {
            DiscardedBytes += n;
            Consume(n);
        }

        private void Consume(int n)
        {
            if (n == 0) return;
            Buffer.BlockCopy(_buffer, n, _buffer, 0, _count - n);
            _count -= n;
        }
    }
}
