import socket
import struct

import camera_stream as cs


def test_meta_matches_shared_vector(vectors):
    v = vectors["yoloMeta"]
    o = v["objects"][0]
    dets = [{"box": [o["x1"], o["y1"], o["x2"], o["y2"]], "conf": o["conf"] / 255, "class_id": o["cls"],
             "track_id": o["track"]}]
    assert cs.encode_meta(v["seq"], v["ts"], dets).hex() == v["hex"]


def test_meta_clamps_boxes_and_caps_object_count():
    dets = [{"box": [-5.4, 3.6, 70000, 10.2], "conf": 1.7, "class_id": 2}] * 300
    data = cs.encode_meta(1, 0.0, dets)
    assert struct.unpack_from("<H", data, 12)[0] == cs.MAX_OBJECTS
    assert struct.unpack_from("<BBHHHHH", data, 14) == (2, 255, 0, 4, 65535, 10, 0)


def test_jpeg_is_split_into_mtu_sized_chunks_with_headers(vectors):
    jpeg = bytes(range(256)) * 12  # 3072 B → 3 mảnh
    grams = cs.chunk_datagrams(jpeg, seq=1, ts=1.5)

    assert len(grams) == 3
    assert all(len(g) <= 16 + cs.MAX_CHUNK for g in grams)
    assert grams[0][:16].hex() == "01000000000000000000f83f00000300"
    assert b"".join(g[16:] for g in grams) == jpeg


def test_video_sender_sends_datagrams_to_host():
    rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    rx.bind(("127.0.0.1", 0))
    rx.settimeout(2)
    sender = cs.VideoSender(port=rx.getsockname()[1])

    seq = sender.send("127.0.0.1", b"\xff\xd8" + b"x" * 2000, 2.0)

    first = rx.recv(2048)
    assert struct.unpack_from("<IdHH", first) == (seq, 2.0, 0, 2)
