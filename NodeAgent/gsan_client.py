"""Client IF-01 GSAN-CONG: đăng ký A1/A3, heartbeat A4, đẩy dữ liệu A7 (SDD 4.2.3, 4.2.4)."""
import logging
import socket
import struct
import time

import gsan_protocol as gp

log = logging.getLogger("GSAN")
RECONNECT_DELAY_S = 2.0


class HandshakeError(Exception):
    pass


class GsanClient:
    def __init__(self, hosts, port, physical_id, key, status_fn, data_fn, *, data_period=0.4,
                 heartbeat_normal=5.0, heartbeat_alert=1.0, join_timeout=10.0):
        self.hosts = list(hosts)
        self.port = port
        self.physical_id = physical_id
        self.key = key
        self.status_fn = status_fn
        self.data_fn = data_fn
        self.data_period = data_period
        self.heartbeat_normal = heartbeat_normal
        self.heartbeat_alert = heartbeat_alert
        self.join_timeout = join_timeout
        self.active_host = None
        self.node_id = None

    def run(self, stop):
        index = 0
        while not stop.is_set():
            host = self.hosts[index % len(self.hosts)]   # BM-06: luân phiên gateway khi mất kết nối
            index += 1
            try:
                with socket.create_connection((host, self.port), timeout=5) as sock:
                    reader = gp.FrameReader()
                    self.node_id = self._handshake(sock, reader)
                    self.active_host = host
                    log.info("[GSAN] %s: registered as node %d", host, self.node_id)
                    self._serve(sock, reader, stop)
            except (OSError, HandshakeError) as e:
                log.warning("[GSAN] %s: %r, retry in %.0fs", host, e, RECONNECT_DELAY_S)
            finally:
                self.active_host = None
            stop.wait(RECONNECT_DELAY_S)

    def _expect(self, sock, reader, cmd, timeout):
        sock.settimeout(timeout)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            data = sock.recv(4096)
            if not data:
                raise HandshakeError("gateway closed during handshake")
            for c, node, payload in reader.feed(data):
                if c == cmd:
                    return node, payload
        raise HandshakeError(f"timeout waiting for 0x{cmd:02X}")

    def _handshake(self, sock, reader):
        sock.sendall(gp.encode_frame(gp.CMD_REGISTER, 0, struct.pack("<I", self.physical_id)))
        _, challenge = self._expect(sock, reader, gp.CMD_REGISTER, self.join_timeout)
        if len(challenge) != 4:
            raise HandshakeError("bad challenge length")
        sock.sendall(gp.encode_frame(gp.CMD_REGISTER, 0, gp.join_response(self.key, challenge)))
        node, payload = self._expect(sock, reader, gp.CMD_WRITE_CONFIG, self.join_timeout)
        if not 1 <= node <= 254 or payload != bytes([node]):
            raise HandshakeError("bad node id assignment")
        sock.sendall(gp.encode_frame(gp.CMD_WRITE_CONFIG, node, gp.ASSIGNMENT_OK))
        return node

    def _heartbeat(self):
        status, error, flags, cpu_temp, _ = self.status_fn()
        return gp.encode_frame(gp.CMD_HEARTBEAT, self.node_id, gp.heartbeat_payload(status, error, flags, cpu_temp))

    def _serve(self, sock, reader, stop):
        sock.settimeout(0.05)
        next_hb = next_data = time.monotonic()
        while not stop.is_set():
            now = time.monotonic()
            if now >= next_hb:
                sock.sendall(self._heartbeat())
                alert = self.status_fn()[4]
                next_hb = now + (self.heartbeat_alert if alert >= 2 else self.heartbeat_normal)
            if now >= next_data:
                payload = self.data_fn()
                if payload:
                    sock.sendall(gp.encode_frame(gp.CMD_SENSOR_DATA, self.node_id, payload))
                next_data = now + self.data_period
            try:
                data = sock.recv(4096)
            except socket.timeout:
                continue
            if not data:
                raise OSError("gateway closed connection")
            for cmd, node, _ in reader.feed(data):
                if cmd == gp.CMD_HEARTBEAT:
                    sock.sendall(self._heartbeat())   # SDD 5.2: trả trạng thái khi gateway hỏi
                else:
                    log.info("[GSAN] ignoring command 0x%02X (not in this release)", cmd)
