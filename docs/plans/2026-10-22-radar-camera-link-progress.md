# Tiến độ: kết nối dữ liệu radar và camera theo SDD

Kế hoạch: [2026-10-22-radar-camera-link-plan.md](2026-10-22-radar-camera-link-plan.md).
Cập nhật lần cuối: 23/10/2026.
Cách làm: mỗi task giao cho opencode (`opencode-go/deepseek-v4.1-flash#max`), Claude review diff và tự chạy lại test sau mỗi task.

## Trạng thái task

| Task | Nội dung | Commit | Review |
|---|---|---|---|
| 1 | Vector chung, CRC16-Modbus, `FrameFormat` | `a84fe6d` | Đạt |
| 2 | `FrameStreamReader` | `22d6fde` | Đạt |
| 3 | Payload A1/A3/A4, MAC theo SDD, pin NaN | `3899ca5` | Đạt |
| 4 | Mảnh video, ghép JPEG, metadata YOLO | `b73ec8a` | Đạt |
| 5 | Relay khung YOLO, khóa gửi theo viewer | `3cc9b38` | Đạt |
| 6 | Tự tạo bản ghi camera khi node tham gia | `96f8b43` | Đạt (Station chưa build, cần Windows) |
| 7 | NodeAgent: codec GSAN, ánh xạ radar | `504892c` | Đạt (fixture radar là mẫu giả định) |
| 8 | NodeAgent: client IF-01 | `828a823` | Đạt |
| 9 | NodeAgent: gửi video, nguồn cell/giả lập, chương trình chính | `4e67236`, `48cc600` | Đạt (Claude bổ sung bước cài systemd trong README) |
| 10 | LineGateway: phiên GSAN, bảng phiên, định tuyến | `5192733` | Đạt |
| 11 | LineGateway: MQTT lên PC, máy chủ TCP 9300 | `902f5b7` | Đạt |
| 12 | LineGateway: nhận video, đẩy lên PC | `74f53c7` | Đạt (test socket chạy 3 lần ổn định) |
| 13 | Vẽ khung YOLO (Station, `camera-test.html`) | ecbb00b | Đạt (Haiku 5.5 viết, Opus 5.5 review; `node --check` đạt, chưa kiểm thử trình duyệt, để Task 14) |
| 14 | Kiểm thử E2E trên máy dev | `4f8fed7`, `03392ad` | Đạt (Step 7 Station trên Windows chưa làm; sửa khung YOLO còn sót và log ngắt WebSocket) |
| 15 | Cập nhật tài liệu (lan doc, SDD v1.3) | chưa commit | Đạt (opus-reviewer đối chiếu mã, đã sửa 5 điểm) |

Kết quả test tại `74f53c7`: WireProtocol 61/61, BackendV2 47/47, LineGateway 11/11, NodeAgent (pytest) 12/12.

## Tiếp tục ở phiên sau

1. Chạy Task 13 bằng opencode với cờ `--standalone` (lần chạy không có cờ này bị treo hơn 50 phút, log trống, có lỗi `EPIPE` trong `~/.local/share/opencode/log/opencode.log`).
   Lệnh mẫu:
   ```bash
   cd /home/hii/Documents/Tunnel_Security
   opencode run --standalone -m "opencode-go/deepseek-v4.1-flash#max" --auto --title "radar-camera Task 13" "<prompt>"
   ```
   Prompt gồm: chỉ làm Task 13 theo kế hoạch; bỏ Step 3 (kiểm tra trình duyệt để dành cho Task 14); kiểm tra cú pháp JS bằng `node --check` (trích `<script>` của `camera-test.html`; với Station trích JS trong `BuildWebSocketStreamPage`, đổi `{{`/`}}` thành `{`/`}` và `{wsUrl}` thành chuỗi); chỉ stage đúng file của task; không đụng thay đổi chưa commit của người dùng.
2. Review Task 13, rồi dừng chờ người dùng ra lệnh trước Task 14.
3. Task 14 cần docker (`docker compose --profile sqlserver up -d`), `~/.cache/tunnel-security-dotnet/run-backend.sh`, LineGateway (Development) và `NodeAgent/node_agent.py --fake`.

## Lưu ý

- Thay đổi chưa commit của người dùng, không được stage: `.claude/settings.local.json`, `BackendV2/Gateway/GatewayMessageRouter.cs`, `BackendV2/Gateway/Provisioning/GatewayProvisioningService.cs`, file `.docx`, `F407_SS/`, `S_*.py`, `docs/nm-provisioning-design.md`.
- `dotnet build Tunnel_Security.sln` lỗi vì project `Backend` cũ đã xóa; build từng project.
- Trước Task 14 nên lấy mẫu mục tiêu radar thật trên Pi (mục "Điều kiện tiên quyết" của kế hoạch) để thay fixture giả định.
