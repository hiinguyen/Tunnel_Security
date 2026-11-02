using System.Buffers.Binary;
using System.Text.Json;

namespace WireProtocol
{
    /// <summary>Header 16 byte của một mảnh video UDP (SDD Bảng 4.8).</summary>
    public readonly record struct VideoChunkHeader(uint Seq, double Timestamp, ushort ChunkIndex, ushort TotalChunks)
    {
        public const int Size = 16;
        public const int MaxChunkData = 1400;

        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, Seq);
            BinaryPrimitives.WriteDoubleLittleEndian(destination[4..], Timestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], ChunkIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], TotalChunks);
        }

        public static bool TryRead(ReadOnlySpan<byte> source, out VideoChunkHeader header)
        {
            header = default;
            if (source.Length < Size) return false;
            header = new VideoChunkHeader(
                BinaryPrimitives.ReadUInt32LittleEndian(source),
                BinaryPrimitives.ReadDoubleLittleEndian(source[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[12..]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[14..]));
            return true;
        }
    }

    public sealed record AssembledFrame(uint Seq, double Timestamp, byte[] Jpeg);

    /// <summary>
    /// Ghép các mảnh UDP của một nguồn thành JPEG. Chỉ giữ một frame đang ghép:
    /// mảnh của frame mới hơn bỏ frame dở dang, mảnh của frame cũ hơn bị bỏ qua (UDP có thể đảo thứ tự).
    /// Không thread-safe: mỗi nguồn một instance, gọi từ một luồng nhận.
    /// </summary>
    public sealed class JpegFrameAssembler
    {
        public const int MaxChunks = 512;

        private byte[]?[] _chunks = [];
        private uint _seq;
        private double _timestamp;
        private int _received;
        private bool _active;
        private bool _hasLast;
        private uint _lastCompleted;

        public bool TryAdd(ReadOnlySpan<byte> datagram, out AssembledFrame? frame)
        {
            frame = null;
            if (!VideoChunkHeader.TryRead(datagram, out var h)) return false;
            var data = datagram[VideoChunkHeader.Size..];
            if (h.TotalChunks == 0 || h.TotalChunks > MaxChunks || h.ChunkIndex >= h.TotalChunks
                || data.Length > VideoChunkHeader.MaxChunkData)
            {
                return false;
            }

            if (_hasLast && (int)(h.Seq - _lastCompleted) <= 0) return false;

            if (!_active || h.Seq != _seq)
            {
                if (_active && (int)(h.Seq - _seq) < 0) return false;
                _seq = h.Seq;
                _timestamp = h.Timestamp;
                _chunks = new byte[]?[h.TotalChunks];
                _received = 0;
                _active = true;
            }

            if (h.TotalChunks != _chunks.Length || _chunks[h.ChunkIndex] is not null) return false;

            _chunks[h.ChunkIndex] = data.ToArray();
            if (++_received < _chunks.Length) return false;

            var jpeg = new byte[_chunks.Sum(c => c!.Length)];
            var offset = 0;
            foreach (var chunk in _chunks)
            {
                chunk!.CopyTo(jpeg, offset);
                offset += chunk.Length;
            }

            frame = new AssembledFrame(_seq, _timestamp, jpeg);
            _active = false;
            _hasLast = true;
            _lastCompleted = _seq;
            _chunks = [];
            return true;
        }
    }

    public sealed record YoloObject(byte ClassId, byte Confidence, ushort X1, ushort Y1, ushort X2, ushort Y2, ushort TrackId);

    /// <summary>Gói metadata YOLO trên TCP 6002 (SDD Bảng 4.9, 4.10): header 14 byte + 12 byte mỗi đối tượng.</summary>
    public sealed record YoloMetadata(uint Seq, double Timestamp, IReadOnlyList<YoloObject> Objects)
    {
        public const int HeaderSize = 14;
        public const int ObjectSize = 12;
        public const int MaxObjects = 256;

        /// <summary>Số đối tượng khai báo trong header; -1 nếu vượt <see cref="MaxObjects"/>.</summary>
        public static int ObjectCount(ReadOnlySpan<byte> header)
        {
            var n = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
            return n > MaxObjects ? -1 : n;
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[HeaderSize + Objects.Count * ObjectSize];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Seq);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(4), Timestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)Objects.Count);
            var o = HeaderSize;
            foreach (var obj in Objects)
            {
                bytes[o] = obj.ClassId;
                bytes[o + 1] = obj.Confidence;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o + 2), obj.X1);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o + 4), obj.Y1);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o + 6), obj.X2);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o + 8), obj.Y2);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o + 10), obj.TrackId);
                o += ObjectSize;
            }

            return bytes;
        }

        public static bool TryParse(ReadOnlySpan<byte> data, out YoloMetadata? meta)
        {
            meta = null;
            if (data.Length < HeaderSize) return false;
            var n = ObjectCount(data);
            if (n < 0 || data.Length != HeaderSize + n * ObjectSize) return false;

            var objects = new List<YoloObject>(n);
            for (var o = HeaderSize; o < data.Length; o += ObjectSize)
            {
                objects.Add(new YoloObject(data[o], data[o + 1],
                    BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 2)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 4)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 6)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 8)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(data[(o + 10)..])));
            }

            meta = new YoloMetadata(BinaryPrimitives.ReadUInt32LittleEndian(data),
                BinaryPrimitives.ReadDoubleLittleEndian(data[4..]), objects);
            return true;
        }

        /// <summary>Bản tin text gửi cho viewer; conf lượng tử hóa 0..255 được đổi về 0..1.</summary>
        public string ToJson() => JsonSerializer.Serialize(new
        {
            type = "detections",
            seq = Seq,
            ts = Timestamp,
            objects = Objects.Select(o => new
            {
                cls = o.ClassId,
                conf = Math.Round(o.Confidence / 255.0, 3),
                box = new[] { o.X1, o.Y1, o.X2, o.Y2 },
                track = o.TrackId
            })
        });
    }
}
