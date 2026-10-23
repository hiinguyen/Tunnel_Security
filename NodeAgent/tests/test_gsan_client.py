import socket
import struct
import threading
import time

import gsan_protocol as gp
from gsan_client import GsanClient

KEY = bytes(range(32))


def fake_gateway(server, result):
    conn, _ = server.accept()
    conn.settimeout(5)
    reader = gp.FrameReader()

    def recv_frames():
        frames = []
        while not frames:
            frames = reader.feed(conn.recv(4096))
        return frames

    cmd, node, payload = recv_frames()[0]
    result["request"] = (cmd, node, struct.unpack("<I", payload)[0])
    challenge = struct.pack("<I", 0xCAFEBABE)
    conn.sendall(gp.encode_frame(gp.CMD_REGISTER, 0, challenge))
    cmd, node, payload = recv_frames()[0]
    result["mac_ok"] = payload == gp.join_response(KEY, challenge)
    conn.sendall(gp.encode_frame(gp.CMD_WRITE_CONFIG, 7, bytes([7])))
    result["ack"] = recv_frames()[0]

    seen = []
    deadline = time.time() + 1.5
    while time.time() < deadline:
        try:
            seen += reader.feed(conn.recv(4096))
        except socket.timeout:
            break
    result["after"] = seen
    conn.close()


def test_registers_then_sends_heartbeats_and_sensor_data():
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    result = {}
    gw = threading.Thread(target=fake_gateway, args=(server, result), daemon=True)
    gw.start()

    stop = threading.Event()
    client = GsanClient(["127.0.0.1"], server.getsockname()[1], 0x12345678, KEY,
                        status_fn=lambda: (2, 0, gp.FLAG_RADAR | gp.FLAG_CAMERA, 45.0, 2),
                        data_fn=lambda: gp.tlv(0x04, 1, gp.radar_value([])),
                        data_period=0.1, heartbeat_alert=0.2)
    t = threading.Thread(target=client.run, args=(stop,), daemon=True)
    t.start()
    gw.join(timeout=10)
    stop.set()
    t.join(timeout=5)

    assert result["request"] == (gp.CMD_REGISTER, 0, 0x12345678)
    assert result["mac_ok"]
    assert result["ack"] == (gp.CMD_WRITE_CONFIG, 7, gp.ASSIGNMENT_OK)
    cmds = [c for c, n, _ in result["after"] if n == 7]
    assert cmds.count(gp.CMD_SENSOR_DATA) >= 5
    assert cmds.count(gp.CMD_HEARTBEAT) >= 3   # alert_level 2 → heartbeat nhanh
    assert client.active_host is None           # đã ngắt sau khi gateway đóng
