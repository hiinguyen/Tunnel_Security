"""Ánh xạ mục tiêu của RadarCell (S_13) sang bộ 6 giá trị của TLV 0x04 (SDD Bảng 4.6).

Tên khóa lấy theo tests/fixtures/radar_target_sample.json, xuất từ RadarCell thật trên Pi.
Nếu S_3_Radar_Processor đổi tên khóa, chỉ sửa các hằng dưới đây.
"""
import math

MAX_TARGETS = 40   # 1 + 40*24 B, đủ nhỏ cho một khung 0xA7 400 ms

KEY_X, KEY_Y, KEY_Z = "x", "y", "z"
KEY_SPEED = "speed"
KEY_RANGE_MM = "range_mm"
KEY_FALSE_ALARM = "false_alarm"
KEY_CONFIRMED = "confirmed"


def to_radar_tuples(targets):
    out = []
    for t in targets or []:
        if not t.get(KEY_CONFIRMED):
            continue
        x, y, z = float(t[KEY_X]), float(t[KEY_Y]), float(t.get(KEY_Z, 0.0))
        rng_mm = t.get(KEY_RANGE_MM)
        rng = float(rng_mm) / 1000.0 if rng_mm is not None else math.sqrt(x * x + y * y + z * z)
        out.append((x, y, z, float(t.get(KEY_SPEED, 0.0)), rng, float(t.get(KEY_FALSE_ALARM, 0.0))))
        if len(out) == MAX_TARGETS:
            break
    return out
