# Tổng quan kiến trúc hệ thống Tunnel Security

## 1. Giới thiệu

Hệ thống **Tunnel Security** là giải pháp giám sát an ninh và môi trường cho hệ thống cống ngầm tại Hà Nội. Hệ thống bao gồm các thành phần chính: **BackendV2** (máy chủ trung tâm), **Station** (ứng dụng giám sát desktop), **CameraPublisherSim** và **NodePublisherSim** (công cụ mô phỏng thiết bị IoT phục vụ phát triển và kiểm thử).

---

## 2. Kiến trúc tổng thể

```mermaid
graph TB
    subgraph "IoT Simulators (Development Tools)"
        NPS[NodePublisherSim<br/>.NET 8 Console App]
        CPS[CameraPublisherSim<br/>.NET 8 Console App]
    end

    subgraph "Message Infrastructure"
        MQTT[MQTT Broker<br/>Eclipse Mosquitto<br/>:1883]
    end

    subgraph "Backend Server"
        BV2[BackendV2<br/>ASP.NET Core 10]
        
        subgraph "API Layer"
            REST[REST API Controllers<br/>/api/auth, /api/node, /api/sensor, /api/camera, /api/reading]
            SIGNALR[SignalR Hub<br/>/hubs/sensors]
        end
        
        subgraph "WebSocket Layer"
            INGEST[CameraIngestMiddleware<br/>/ws/camera/{id}/ingest]
            VIEW[CameraViewMiddleware<br/>/ws/camera/{id}/view]
            RELAY[CameraRelayRegistry<br/>In-Memory Frame Relay]
        end
        
        subgraph "Background Services"
            MQTT_SUB[MqttSubscriberService<br/>Subscribe: sensors/+/reading]
        end
        
        subgraph "Data Layer"
            SQL[SQL Server<br/>TunnelSecurityV2<br/>Topology & Auth]
            TSDB[TimescaleDB<br/>tunnel_v2_timeseries<br/>Sensor Readings]
        end
    end

    subgraph "Monitoring Station"
        STATION[Station<br/>WinUI 3 Desktop App]
        
        subgraph "Services"
            SVC1[DataService V2<br/>REST + SignalR]
            SVC2[MockDataService<br/>Self-contained Sim]
            LOCATOR[DataServiceLocator]
        end
        
        subgraph "UI Pages"
            DASH[MonitoringDashboard]
            ALERTS[AlertsPage]
            DEV[DevicesPage]
            VIDEO[LiveVideoPage]
            DATA[DataPage]
        end
    end

    NPS -- MQTT publish --> MQTT
    MQTT -- subscribe --> MQTT_SUB
    MQTT_SUB -- Write --> TSDB
    MQTT_SUB -- Broadcast --> SIGNALR
    
    CPS -- WebSocket binary JPEG --> INGEST
    INGEST -- Relay --> RELAY
    RELAY -- Push --> VIEW
    
    STATION -- REST API --> REST
    STATION -- SignalR --> SIGNALR
    STATION -- WebSocket view --> VIEW
    REST -- CRUD --> SQL
```

---

## 3. Mô tả chi tiết các thành phần

### 3.1 BackendV2

| Thuộc tính | Giá trị |
|---|---|
| **Công nghệ** | ASP.NET Core 10 (C# 12) |
| **Entry point** | `Program.cs` |
| **Cổng mặc định** | HTTP 5080, HTTPS 7089 |

**Vai trò:** Máy chủ trung tâm xử lý toàn bộ logic nghiệp vụ, nhận dữ liệu cảm biến từ các node IoT qua MQTT, nhận luồng camera qua WebSocket, cung cấp REST API quản lý, và real-time push qua SignalR.

**Các thành phần chính:**

- **REST API Controllers:** Cung cấp CRUD cho nodes, sensors, cameras; xác thực phân quyền với JWT + RBAC (6 nhóm chức năng).
- **SignalR Hub** (`/hubs/sensors`): Push real-time sensor readings đến các client đã kết nối.
- **CameraIngestMiddleware** (`/ws/camera/{id}/ingest`): Nhận binary JPEG frames từ camera nodes, ghép multi-chunk messages.
- **CameraViewMiddleware** (`/ws/camera/{id}/view`): Phát luồng camera đến các viewer (Station).
- **CameraRelayRegistry:** Singleton in-memory, chuyển tiếp frame từ ingest đến tất cả viewer của từng camera. Lưu frame cuối để viewer mới không bị màn hình đen.
- **MqttSubscriberService:** Background service kết nối MQTT broker, subscribe topic `sensors/+/reading`, giải mã WireProtocol, ghi vào TimescaleDB, broadcast qua SignalR.
- **AuthService:** Đăng ký/đăng nhập, JWT token management, refresh token, RBAC với function groups.

**Kiến trúc database:**
- **SQL Server** (`TunnelSecurityV2`): Dữ liệu quan hệ (nodes, sensors, cameras, users, roles, permissions, audit logs).
- **TimescaleDB** (`tunnel_v2_timeseries`): Dữ liệu chuỗi thời gian (sensor readings) với hypertable phân vùng theo ngày.

---

### 3.2 Station

| Thuộc tính | Giá trị |
|---|---|
| **Công nghệ** | WinUI 3 (Windows App SDK 1.8), .NET 8 |
| **Kiến trúc** | MVVM (CommunityToolkit.Mvvm) |
| **Mục đích** | Ứng dụng giám sát cho operator |

**Vai trò:** Ứng dụng desktop cho operator tại mỗi trạm quan trắc. Hiển thị real-time sensors, camera, quản lý thiết bị, cảnh báo và báo cáo.

**Các service kết nối:**

| Service | Kết nối đến | Mục đích |
|---|---|---|
| `DataService` (ServicesV2) | BackendV2 (REST + SignalR) | Kênh chính - lấy dữ liệu thực |
| `RealDataService` (Services) | Backend v1 (REST + SignalR) | Tương thích ngược |
| `MockDataService` | Tự sinh dữ liệu giả lập | Phát triển/demo |
| `ApiClient` | BackendV2 `/api/Node`, `/api/Sensor`, `/api/Camera` | REST calls |
| `HubClient` | BackendV2 `/hubs/sensors` | SignalR real-time |
| `UserApiService` | BackendV2 `/api/auth/*` | Quản lý người dùng |

**Luồng xử lý:**
1. Khởi động: `App.xaml.cs` → load `.env` → `DataServiceLocator.Initialize()` → chọn data source (mock/api)
2. Đăng nhập → nhận JWT → lưu vào `AuthSession`
3. Kết nối SignalR → nhận `NewReading` events
4. Operator tương tác qua các trang UI (Dashboard, Alerts, Devices, Live Video...)

**Các trang chính:**
- `MonitoringDashboardPage`: Tổng quan, KPI, mini charts
- `LiveVideoPage`: Grid camera 1x1 đến 4x4
- `AlertsPage`: Danh sách cảnh báo, thống kê
- `DevicesPage`: Quản lý thiết bị (nodes, sensors, cameras)
- `DataPage`: Dữ liệu cảm biến real-time
- `AnalyticsReportPage`: Báo cáo lịch sử

---

### 3.3 NodePublisherSim

| Thuộc tính | Giá trị |
|---|---|
| **Công nghệ** | .NET 8 Console App, MQTTnet 5.2 |
| **Mục đích** | Mô phỏng node cảm biến IoT |

**Vai trò:** Giả lập một node IoT thực tế, sinh dữ liệu ngẫu nhiên cho 4 loại cảm biến và publish lên MQTT broker theo WireProtocol.

**Dữ liệu mô phỏng:**

| Loại cảm biến | Byte type | Giá trị | Kích thước |
|---|---|---|---|
| Light | 0x01 | 50–800 lux | 4 bytes (float) |
| WaterLevel | 0x02 | 0–3 m | 4 bytes (float) |
| TemperatureHumidity | 0x03 | 20–35°C, 40–90% RH | 8 bytes (2 floats) |
| Radar | 0x04 | 0–3 objects | 1 + N×24 bytes |

**Luồng xử lý:**
```
Sinh dữ liệu → TLV Encode → WireFrame Encode → MQTT Publish
                                                   ↓
                                            Topic: sensors/{nodeId}/reading
```

**Giao thức WireProtocol:**
- Start byte `0x53` | Command `0xA7` (SensorData) | Length (LE) | NodeId | Payload (TLV entries) | CRC-16 (LE) | Stop byte `0x4D`

**Biến môi trường:**
- `NODE_ID`: ID node (mặc định: `1`)
- `PUBLISH_INTERVAL_SECONDS`: Tần suất publish (mặc định: `1`)
- `MQTT_BROKER_HOST`: Broker host (mặc định: `localhost`)
- `MQTT_BROKER_PORT`: Broker port (mặc định: `1883`)

---

### 3.4 CameraPublisherSim

| Thuộc tính | Giá trị |
|---|---|
| **Công nghệ** | .NET 8 Console App, SixLabors.ImageSharp 3.1 |
| **Mục đích** | Mô phỏng luồng camera IP |

**Vai trò:** Giả lập camera gửi luồng JPEG frames qua WebSocket đến BackendV2.

**Ba chế độ hoạt động (ưu tiên: video > ảnh tĩnh > synthetic):**
1. **Synthetic frames (mặc định):** Sinh ảnh JPEG 640×480 procedural bằng ImageSharp, mỗi frame có màu nền thay đổi (hue cycling), scan line trượt, marker đỏ nhấp nháy.
2. **Static image:** Nếu set biến `CAMERA_IMAGE_PATH`, đọc file ảnh tĩnh và gửi lặp lại.
3. **Local video:** Nếu set biến `CAMERA_VIDEO_PATH`, spawn tiến trình `ffmpeg` để decode video (loop vô hạn, đọc theo tốc độ thực `-re`) thành chuỗi JPEG qua pipe MJPEG (`VideoFrameSource`, cùng kỹ thuật với `Backend/Services/VideoFrameReader.cs`), rồi gửi từng frame qua WebSocket theo đúng nhịp `-r {CAMERA_FPS}`. Cần có `ffmpeg` trong PATH hoặc set `FFMPEG_PATH`.

**Luồng xử lý:**
```
GenerateFrame (synthetic/static) hoặc VideoFrameSource (video) → WebSocket SendAsync (binary) → BackendV2
                                                                                                     ↓
                                                                                          CameraRelayRegistry
                                                                                                     ↓
                                                                                          CameraView → Station
```

**Tự động kết nối lại:** Khi mất kết nối, đợi 5s và thử lại.

**Biến môi trường:**
- `CAMERA_BACKEND_WS`: URL backend (mặc định: `ws://localhost:5080/ws/camera`)
- `CAMERA_ID`: ID camera (mặc định: `CAM-HUB-01`)
- `CAMERA_FPS`: Số frame/giây (mặc định: `5`)
- `CAMERA_IMAGE_PATH`: Đường dẫn ảnh tĩnh (tùy chọn)
- `CAMERA_VIDEO_PATH`: Đường dẫn video local để giả lập (tùy chọn, ưu tiên cao hơn `CAMERA_IMAGE_PATH`)
- `FFMPEG_PATH`: Đường dẫn tới `ffmpeg.exe` nếu không nằm trong PATH (chỉ dùng khi có `CAMERA_VIDEO_PATH`)

---

## 4. Luồng dữ liệu chính

### 4.1 Sensor Data Flow

```
NodePublisherSim                    BackendV2                           Station
     │                                 │                                  │
     │──MQTT publish─────────────────▶│                                  │
     │  sensors/{id}/reading          │                                  │
     │                                │──MqttSubscriberService──────────▶│
     │                                │  decode WireProtocol             │
     │                                │                                  │
     │                                │──ghi TimescaleDB                 │
     │                                │                                  │
     │                                │──SignalR broadcast──────────────▶│
     │                                │  "NewReading" event              │
     │                                │                                  │
     │◀────REST API────────────────────│                                  │
     │  /api/sensor, /api/node        │                                  │
```

### 4.2 Camera Data Flow

```
CameraPublisherSim                  BackendV2                           Station
     │                                 │                                  │
     │──WebSocket binary JPEG────────▶│                                  │
     │  /ws/camera/{id}/ingest        │                                  │
     │                                │──CameraRelayRegistry────────────▶│
     │                                │                                  │
     │                                │◀──WebSocket view────────────────│
     │                                │  /ws/camera/{id}/view            │
```

### 4.3 Authentication Flow

```
Station                             BackendV2
     │                                 │
     │──POST /api/auth/login──────────▶│
     │  { username, password }         │──Verify credentials
     │                                 │──Generate JWT + Refresh Token
     │◀──{ accessToken, refreshToken }─│
     │                                 │
     │──GET /api/node (Bearer JWT)────▶│──Validate JWT
     │                                 │──Authorize (role check)
     │◀──Node list─────────────────────│
     │                                 │
     │──POST /api/auth/refresh────────▶│──Validate refresh token
     │◀──New access token──────────────│
```

---

## 5. Cơ sở hạ tầng (Docker)

| Service | Image | Port | Mục đích |
|---|---|---|---|
| timescaledb | timescaledb-ha:pg18 | 5433 | Time-series sensor data |
| mosquitto | eclipse-mosquitto:2 | 1883 | MQTT message broker |

---

## 6. Công nghệ sử dụng

| Thành phần | Công nghệ |
|---|---|
| **BackendV2** | ASP.NET Core 10, EF Core 10, SignalR, MQTTnet, JWT Bearer |
| **Station** | WinUI 3, CommunityToolkit.Mvvm 8.4, LiveChartsCore, SignalR Client, WebView2 |
| **NodePublisherSim** | .NET 8, MQTTnet 5.2, WireProtocol |
| **CameraPublisherSim** | .NET 8, SixLabors.ImageSharp 3.1, ClientWebSocket |
| **WireProtocol** | .NET 8 Class Library (shared: frame codec, TLV codec, CRC-16) |
| **Database** | SQL Server (relational), TimescaleDB/PostgreSQL (time-series) |
| **Message Broker** | Eclipse Mosquitto 2 (MQTT) |
