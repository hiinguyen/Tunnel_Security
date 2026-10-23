import struct

import gsan_protocol as gp


def test_crc_matches_modbus_check_value(vectors):
    check = vectors["crcModbusCheck"]
    assert gp.crc16_modbus(check["ascii"].encode()) == check["crc"]


def test_frames_match_shared_vectors(vectors):
    for f in vectors["frames"]:
        frame = gp.encode_frame(f["cmd"], f["node"], bytes.fromhex(f["payload"]))
        assert frame.hex() == f["hex"], f["name"]
        assert gp.FrameReader().feed(frame) == [(f["cmd"], f["node"], bytes.fromhex(f["payload"]))]


def test_reader_handles_split_glued_and_corrupted_frames():
    good = gp.encode_frame(0xA7, 1, b"\x04\x01\x01\x00\x00")
    bad = bytearray(gp.encode_frame(0xA7, 2, b"\x04\x01\x01\x00\x00"))
    bad[-3] ^= 0xFF
    reader = gp.FrameReader()

    out = reader.feed(b"\x00\x53\xff\xff" + bytes(bad) + good[:4])  # rác có độ dài 0x53ff > MAX_PAYLOAD
    out += reader.feed(good[4:] + good)

    assert [node for _, node, _ in out] == [1, 1]


def test_heartbeat_and_join_response_match_vectors(vectors):
    hb = vectors["heartbeat"]
    payload = gp.heartbeat_payload(hb["status"], hb["error"], hb["flags"], hb["cpuTemp"])
    assert gp.encode_frame(gp.CMD_HEARTBEAT, 7, payload).hex() == hb["hex"]

    key = bytes.fromhex(vectors["deviceKeyHex"])
    challenge = struct.pack("<I", vectors["joinMac"]["nonce"])
    assert gp.join_response(key, challenge).hex() == vectors["joinMac"]["macHex"]


def test_radar_tlv_matches_a7_vector(vectors):
    a7 = next(f for f in vectors["frames"] if f["name"] == "A7 one target")
    payload = gp.tlv(0x04, 1, gp.radar_value([(1.0, 5.0, 0.2, -0.5, 5.1, 2.0)]))
    assert payload.hex() == a7["payload"]
