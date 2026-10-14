# BackendV2 — Sơ đồ kiến trúc: luồng Sensor & luồng Camera

Tài liệu mô tả 2 luồng dữ liệu chính trong `BackendV2`: **Sensor (MQTT → SignalR)** và **Camera (WebSocket relay)**.

---

## 1. Luồng Sensor (MQTT → hàng đợi → TimescaleDB + SignalR)

Luồng này được thay bằng giao thức PC - NM theo tuyến; chi tiết đầy đủ ở `docs/lan-architecture-protocol.md` (mục 4, 7, 8).

```mermaid
sequenceDiagram
    participant NM as NM (Gateway tuyến)
    participant Broker as Mosquitto (Dynamic Security)
    participant Conn as MqttConnectionService
    participant Router as GatewayMessageRouter
    participant Ing as SensorIngestion
    participant Writer as ReadingWriter
    participant Alerts as AlertEngine
    participant Hub as SensorHub
    participant Station

    NM->>Broker: PUBLISH ts/{line}/n/{node}/up/data (khung A7 nhị phân)
    Broker-->>Conn: bản tin (subscribe ts/+/n/+/up/+)
    Conn->>Conn: xếp vào hàng đợi có giới hạn
    Conn->>Router: worker xử lý theo thứ tự
    Router->>Router: kiểm tra tuyến đã cấp, CRC, Node ID khung = topic, node đã tham gia
    Router->>Ing: HandleAsync(node, payload)
    Ing->>Writer: Enqueue(reading) (ghi theo lô 1.000 dòng / 500 ms)
    Ing->>Hub: NewReading tới group all / line-{k} / node-{k}-{n}
    Hub-->>Station: push realtime
    Ing->>Alerts: SensorLevelChanged (chỉ khi mức đã lọc dao động thay đổi)
```

### Ghi chú
- Kết nối MQTT tự kết nối lại; mỗi lần kết nối, `DynamicSecurityClient` bảo đảm quyền của backend và tài khoản cấp phát trước khi subscribe.
- Callback nhận chỉ xếp hàng, nên handler có thể chờ phản hồi broker mà không tự khóa vòng nhận.
- Không còn ghi CSDL cho từng bản tin; dữ liệu và realtime tách rời, CSDL chậm không làm chậm Station.

---

## 2. Luồng Camera (WebSocket relay qua 2 middleware)

### Thành phần liên quan
- `CameraRelayRegistry` (singleton) — `ConcurrentDictionary<cameraId, ConcurrentDictionary<WebSocket, bool>>`, giữ danh sách viewer đang xem theo từng camera, có `AddViewer` / `RemoveViewer` / `BroadcastFrameAsync`
- `CameraIngestMiddleware` — nhận frame từ Node tại `/ws/camera/{cameraId}/ingest`
- `CameraViewMiddleware` — đăng ký Station làm viewer tại `/ws/camera/{cameraId}/view`
- Cả 2 middleware match route bằng `StartsWith` / `EndsWith` thủ công (không dùng route template/Regex), đăng ký sau `app.UseWebSockets()`

### Sequence diagram

```mermaid
sequenceDiagram
    participant Node as Node (camera)
    participant Ingest as CameraIngestMiddleware
    participant Reg as CameraRelayRegistry (singleton)
    participant View as CameraViewMiddleware
    participant Station as Station (viewer)

    Station->>View: WS connect /ws/camera/{id}/view
    View->>Reg: AddViewer(cameraId, socket)
    Note over View,Station: giữ socket mở, chỉ chờ Close (không đọc data)

    Node->>Ingest: WS connect /ws/camera/{id}/ingest
    loop mỗi frame ảnh
        Node->>Ingest: gửi binary message (có thể nhiều chunk)
        Ingest->>Ingest: ReceiveAsync loop → ghép vào MemoryStream tới khi EndOfMessage
        Ingest->>Reg: BroadcastFrameAsync(cameraId, frameBytes)
        Reg-->>Station: SendAsync(frame, Binary) tới từng viewer đang Open
    end

    Station-->>View: đóng kết nối / mất mạng
    View->>Reg: RemoveViewer(cameraId, socket)  (finally — luôn chạy)
```

### Component / data-flow diagram

```mermaid
flowchart LR
    Node["Node<br/>(gửi JPEG frame)"] -- "WS binary<br/>/ws/camera/{id}/ingest" --> Ingest[CameraIngestMiddleware]
    Ingest -- "BroadcastFrameAsync(cameraId, bytes)" --> Reg[("CameraRelayRegistry<br/>(singleton, in-memory)")]
    Reg -- "SendAsync(Binary)" --> View[CameraViewMiddleware]
    View -- "WS binary<br/>/ws/camera/{id}/view" --> Station["Station<br/>(hiển thị ảnh)"]
    Station -. "AddViewer/RemoveViewer" .-> Reg
```

### Ghi chú
- **Chiều dữ liệu 2 middleware khác nhau hoàn toàn**: Ingest = đọc frame + broadcast; View = chỉ đăng ký viewer + chờ Close, không đọc/parse dữ liệu ảnh.
- Registry là **in-memory, singleton** — không persist, mất frame nếu không có viewer nào đang mở tại thời điểm broadcast (không buffer/replay).
- `RemoveViewer` luôn nằm trong `finally` ở View middleware → đảm bảo dọn dẹp kể cả khi client rớt mạng đột ngột, không chỉ khi đóng sạch (`Close` message).
- Ghép frame trong Ingest dùng `do { ... } while (!result.EndOfMessage)` vì buffer nhận chỉ 8KB, 1 JPEG frame thường lớn hơn nên phải nhận nhiều chunk mới đủ 1 message hoàn chỉnh.
- Cả 2 middleware dùng `context.RequestAborted` làm `CancellationToken` cho `ReceiveAsync`/`SendAsync` — tự hủy khi client ngắt kết nối HTTP.

---

## So sánh nhanh 2 luồng

| | Sensor | Camera |
|---|---|---|
| Giao thức nhận từ Node | MQTT qua NM của tuyến (cây topic `ts/...`) | WebSocket (custom middleware) |
| Nơi lưu trữ | TimescaleDB (`Readings`, ghi theo lô) | Không lưu - chỉ relay trực tiếp |
| Cách phát tới client | SignalR Hub, group theo phạm vi (`SetScope`) | WebSocket binary trực tiếp tới từng viewer |
| Trạng thái giữ ở đâu | Không giữ danh sách client thủ công (SignalR tự quản lý) | `CameraRelayRegistry` tự quản lý viewer theo `cameraId` |
| Độ bền dữ liệu | Có (ghi theo lô, phát realtime song song) | Không (mất nếu không ai đang xem) |