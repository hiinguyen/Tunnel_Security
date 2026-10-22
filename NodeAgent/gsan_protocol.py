"""Khung GSAN-CONG giữa node và gateway (SDD AIOT-NODE-SDD-001 mục 4.2)."""
import hashlib
import hmac
import struct

START, STOP = 0x53, 0x4D
CMD_REGISTER = 0xA1
CMD_WRITE_CONFIG = 0xA3
CMD_HEARTBEAT = 0xA4
CMD_SENSOR_DATA = 0xA7

FLAG_RADAR, FLAG_VXL, FLAG_CAMERA = 1, 2, 4
ASSIGNMENT_OK = b"OK"
MAX_PAYLOAD = 16 * 1024
_HEADER, _TRAILER = 5, 3


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def encode_frame(cmd: int, node_id: int, payload: bytes) -> bytes:
    body = bytes([cmd]) + struct.pack("<H", len(payload)) + bytes([node_id]) + payload
    return bytes([START]) + body + struct.pack("<H", crc16_modbus(body)) + bytes([STOP])


class FrameReader:
    """Tách khung từ luồng TCP; khung lỗi bị bỏ từng byte tới Start kế tiếp."""

    def __init__(self):
        self._buf = bytearray()

    def feed(self, data: bytes) -> list:
        self._buf.extend(data)
        frames = []
        while True:
            start = self._buf.find(START)
            if start < 0:
                self._buf.clear()
                return frames
            del self._buf[:start]
            if len(self._buf) < _HEADER:
                return frames
            length = self._buf[2] | (self._buf[3] << 8)
            if length > MAX_PAYLOAD:
                del self._buf[0]
                continue
            total = _HEADER + length + _TRAILER
            if len(self._buf) < total:
                return frames
            frame = bytes(self._buf[:total])
            crc = struct.unpack_from("<H", frame, _HEADER + length)[0]
            if frame[-1] == STOP and crc == crc16_modbus(frame[1:_HEADER + length]):
                frames.append((frame[1], frame[4], frame[_HEADER:_HEADER + length]))
                del self._buf[:total]
            else:
                del self._buf[0]


def tlv(type_: int, instance: int, value: bytes) -> bytes:
    return bytes([type_, instance]) + struct.pack("<H", len(value)) + value


def radar_value(targets) -> bytes:
    """TLV 0x04: u8 số mục tiêu + 6 float32 (x, y, z, vel, range m, false_alarm) mỗi mục tiêu."""
    out = bytearray([len(targets)])
    for t in targets:
        out += struct.pack("<6f", *t)
    return bytes(out)


def heartbeat_payload(status: int, error: int, flags: int, cpu_temp: float) -> bytes:
    return bytes([status, error, flags]) + struct.pack("<f", cpu_temp)


def join_response(key: bytes, challenge: bytes) -> bytes:
    return hmac.new(key, challenge, hashlib.sha256).digest()[:4]
