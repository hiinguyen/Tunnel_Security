# Chay NodePublisherSim voi cau hinh dev, thay cho viec set bien moi truong bang tay.
# Cach dung: .\run-node-sim.ps1
# Ghi de gia tri mac dinh bang tham so, vi du: .\run-node-sim.ps1 -Lines 3 -NodesPerLine 5

param(
    [string]$BrokerHost = "localhost",
    [int]$BrokerPort = 1883,
    [string]$ProvisionUsername = "provision",
    # Khop BackendV2/appsettings.Development.json: Mqtt:DynamicSecurity:ProvisioningPassword
    [string]$ProvisionPassword = "dev-provision-password",
    # Khop Simulation:DeviceSecret cua BackendV2
    [string]$DeviceSecret = "dev-sim-secret-change-me",
    # Mac dinh de trong: node gia lap chinh la cac ho ga trong Station/Assets/Map/nodes.json.
    # Chi truyen -Lines/-NodesPerLine khi can topology tong hop (kiem thu tai).
    [int]$Lines,
    [int]$NodesPerLine,
    [double]$PublishIntervalSeconds = 1
)

$env:MQTT_BROKER_HOST = $BrokerHost
$env:MQTT_BROKER_PORT = "$BrokerPort"
$env:MQTT_PROVISION_USERNAME = $ProvisionUsername
$env:MQTT_PROVISION_PASSWORD = $ProvisionPassword
$env:SIM_DEVICE_SECRET = $DeviceSecret
if ($PSBoundParameters.ContainsKey('Lines')) { $env:LINES = "$Lines" } else { Remove-Item Env:LINES -ErrorAction SilentlyContinue }
if ($PSBoundParameters.ContainsKey('NodesPerLine')) { $env:NODES_PER_LINE = "$NodesPerLine" } else { Remove-Item Env:NODES_PER_LINE -ErrorAction SilentlyContinue }
$env:PUBLISH_INTERVAL_SECONDS = "$PublishIntervalSeconds"

Set-Location $PSScriptRoot
dotnet run --project NodePublisherSim
