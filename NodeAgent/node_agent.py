"""Agent node AIoT trên Pi: radar + camera → gateway theo SDD (IF-01, IF-02, IF-03).

Chạy: python3 node_agent.py --config /etc/aiot/node_config.json
Dev:  python3 node_agent.py --config node_config.example.json --fake
"""
import argparse
import json
import logging
import pathlib
import threading
import time

import gsan_protocol as gp
from camera_stream import MetaSender, VideoSender
from gsan_client import GsanClient
from radar_mapping import to_radar_tuples
import sources

log = logging.getLogger("AGENT")
STALE_S = 3.0


def physical_id_from_cpu_serial():
    for line in pathlib.Path("/proc/cpuinfo").read_text().splitlines():
        if line.startswith("Serial"):
            return int(line.split(":")[1].strip(), 16) & 0xFFFFFFFF
    raise RuntimeError("no CPU serial; set physical_id in config")


def cpu_temp():
    try:
        return int(pathlib.Path("/sys/class/thermal/thermal_zone0/temp").read_text()) / 1000.0
    except OSError:
        return 0.0


class Agent:
    def __init__(self, cfg, fake):
        self.cfg = cfg
        self.radar = sources.FakeRadar() if fake else sources.CellRadar(cfg["radar_source"])
        self.camera = (sources.FakeCamera(cfg["video_fps"]) if fake
                       else sources.CellCamera(cfg["camera_source"], cfg["jpeg_quality"]))
        self.lock = threading.Lock()
        self.radar_tuples, self.radar_ts = [], 0.0
        self.camera_ts, self.alert = 0.0, 0
        pid = cfg["physical_id"]
        key = bytes.fromhex(pathlib.Path(cfg["device_key_file"]).read_text().strip())
        self.client = GsanClient(cfg["gateway_hosts"], cfg["gsan_port"],
                                 physical_id_from_cpu_serial() if pid == "auto" else int(pid, 0), key,
                                 status_fn=self.status, data_fn=self.data)
        self.video = VideoSender(cfg["video_port"])
        self.meta = MetaSender(cfg["meta_port"])

    def status(self):
        now = time.time()
        with self.lock:
            flags = (gp.FLAG_RADAR if now - self.radar_ts < STALE_S else 0) | \
                    (gp.FLAG_CAMERA if now - self.camera_ts < STALE_S else 0)
            return 2, 0, flags, cpu_temp(), self.alert   # status 2 = ON

    def data(self):
        with self.lock:
            if time.time() - self.radar_ts >= STALE_S:
                return None                         # radar im lặng: không gửi số liệu cũ
            return gp.tlv(0x04, 1, gp.radar_value(self.radar_tuples))

    def radar_loop(self, stop):
        while not stop.is_set():
            targets = self.radar.read()
            if targets is not None:
                with self.lock:
                    self.radar_tuples, self.radar_ts = to_radar_tuples(targets), time.time()
            else:
                time.sleep(0.005)

    def camera_loop(self, stop):
        while not stop.is_set():
            item = self.camera.read()
            if item is None:
                time.sleep(0.005)
                continue
            jpeg, dets, alert = item
            ts = time.time()
            with self.lock:
                self.camera_ts, self.alert = ts, alert
            host = self.client.active_host
            if host is None:
                continue                            # chưa đăng ký xong: gateway chưa biết node này
            seq = self.video.send(host, jpeg, ts)
            self.meta.send(host, seq, ts, dets)

    def run(self):
        stop = threading.Event()
        for target in (self.radar_loop, self.camera_loop, self.client.run):
            threading.Thread(target=target, args=(stop,), daemon=True, name=target.__name__).start()
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            stop.set()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--fake", action="store_true", help="dùng radar/camera giả lập (máy dev)")
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(message)s")
    Agent(json.loads(pathlib.Path(args.config).read_text()), args.fake).run()


if __name__ == "__main__":
    main()
