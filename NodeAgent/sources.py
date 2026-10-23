"""Nguồn radar/camera cho agent: bọc cell thật trên Pi, hoặc giả lập để chạy E2E trên máy dev."""
import io
import math
import time


class CellRadar:
    def __init__(self, source):
        from S_13_Radar_Cell import RadarCell   # chỉ có trên Pi
        self.cell = RadarCell(source=source).start()

    def read(self):
        f = self.cell.poll()
        return f.targets if f is not None and f.is_real else None


class CellCamera:
    def __init__(self, source, jpeg_quality):
        import cv2                               # chỉ cần trên Pi
        from S_10_Camera_Cell import CameraCell
        self._cv2 = cv2
        self._quality = int(jpeg_quality)
        self.cell = CameraCell(source=source).start()

    def read(self):
        f = self.cell.poll()
        if f is None or not f.is_real:
            return None
        ok, buf = self._cv2.imencode(".jpg", f.frame, [self._cv2.IMWRITE_JPEG_QUALITY, self._quality])
        return (buf.tobytes(), f.detections or [], int(f.info.get("alert_level", 0))) if ok else None


class FakeRadar:
    """Hai mục tiêu đi qua lại, cùng dạng dict với RadarCell (tests/fixtures/radar_target_sample.json)."""

    def read(self):
        t = time.time()
        out = []
        for i, phase in enumerate((0.0, 2.0)):
            x, y = 3 * math.sin(t / 3 + phase), 6 + 2 * math.cos(t / 4 + phase)
            out.append({"id": i, "x": x, "y": y, "z": 0.2, "speed": -0.4, "range_mm": 1000 * math.hypot(x, y),
                        "false_alarm": 5.0, "confirmed": True})
        time.sleep(0.05)
        return out


class FakeCamera:
    """Ảnh 640x480 có một khối di chuyển; detection trùng khối đó để kiểm tra vẽ bbox."""

    def __init__(self, fps):
        from PIL import Image, ImageDraw
        self._image, self._draw = Image, ImageDraw
        self._period = 1.0 / fps
        self._n = 0

    def read(self):
        time.sleep(self._period)
        self._n += 1
        x = 40 + (self._n * 8) % 480
        img = self._image.new("RGB", (640, 480), (30, 40, 60))
        self._draw.Draw(img).rectangle([x, 160, x + 100, 360], fill=(220, 180, 40))
        buf = io.BytesIO()
        img.save(buf, "JPEG", quality=70)
        det = {"box": [x, 160, x + 100, 360], "conf": 0.9, "class_id": 0, "track_id": 1, "confirmed": True}
        return buf.getvalue(), [det], 2
