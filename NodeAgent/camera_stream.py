"""Video hai luồng tới gateway (SDD 4.3): JPEG phân mảnh qua UDP 6000, metadata YOLO qua TCP 6002."""
import logging
import socket
import struct

log = logging.getLogger("VIDEO")
MAX_CHUNK = 1400
MAX_OBJECTS = 256
_HEADER = struct.Struct("<IdHH")
_META_HEADER = struct.Struct("<IdH")
_OBJECT = struct.Struct("<BBHHHHH")


def _u16(v):
    return max(0, min(65535, int(round(float(v)))))


def chunk_datagrams(jpeg: bytes, seq: int, ts: float) -> list:
    total = max(1, -(-len(jpeg) // MAX_CHUNK))
    return [_HEADER.pack(seq, ts, i, total) + jpeg[i * MAX_CHUNK:(i + 1) * MAX_CHUNK] for i in range(total)]


def encode_meta(seq: int, ts: float, detections: list) -> bytes:
    dets = list(detections or [])[:MAX_OBJECTS]
    out = bytearray(_META_HEADER.pack(seq, ts, len(dets)))
    for d in dets:
        x1, y1, x2, y2 = d["box"]
        conf = max(0, min(255, int(round(float(d.get("conf", 0.0)) * 255))))
        out += _OBJECT.pack(int(d.get("class_id", 0)) & 0xFF, conf, _u16(x1), _u16(y1), _u16(x2), _u16(y2),
                            _u16(d.get("track_id") or 0))
    return bytes(out)


class VideoSender:
    def __init__(self, port=6000):
        self.port = port
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._seq = 0

    def send(self, host: str, jpeg: bytes, ts: float) -> int:
        self._seq = (self._seq + 1) & 0xFFFFFFFF
        for gram in chunk_datagrams(jpeg, self._seq, ts):
            self._sock.sendto(gram, (host, self.port))
        return self._seq


class MetaSender:
    def __init__(self, port=6002):
        self.port = port
        self._sock = None
        self._host = None

    def send(self, host: str, seq: int, ts: float, detections: list) -> None:
        try:
            if self._sock is None or self._host != host:
                self.close()
                self._sock = socket.create_connection((host, self.port), timeout=2)
                self._host = host
            self._sock.sendall(encode_meta(seq, ts, detections))
        except OSError as e:
            log.warning("[VIDEO] meta to %s:%d failed: %r", host, self.port, e)
            self.close()

    def close(self):
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
        self._sock = None
