# NodeAgent

Agent thu thập dữ liệu radar và camera trên node Raspberry Pi, gửi lên gateway theo SDD IF-01, IF-02 và IF-03.
Mỗi dòng dưới đây là một bước cài đặt trên Pi.

## Cài đặt trên Pi

- Chép các module cell (`S_*.py`) vào `/opt/aiot/cells`.
- Chép thư mục `NodeAgent` vào `/opt/aiot/NodeAgent`.
- Tạo `/etc/aiot/device.key` chứa khóa hex 32 byte do PC cấp bằng `JoinProtocol.DeriveDeviceKey(khóa gốc, ID vật lý)`.
- Đặt quyền `600` cho `/etc/aiot/device.key`.
- Sửa `/etc/aiot/node_config.json` theo địa chỉ gateway và nguồn cảm biến thực tế.
- Chạy `systemctl enable --now aiot-node-agent`.

## Chạy thử trên máy dev

- Chạy `python3 node_agent.py --config node_config.example.json --fake` để dùng radar và camera giả lập.
- Chạy `python3 -m pytest -q` trong thư mục `NodeAgent` để kiểm thử.
