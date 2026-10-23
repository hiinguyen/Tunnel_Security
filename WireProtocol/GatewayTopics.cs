using System.Text.RegularExpressions;

namespace WireProtocol
{
    /// <summary>Loại topic MQTT trong cây <c>ts/...</c> giữa NM (Gateway tuyến) và PC.</summary>
    public enum TopicKind
    {
        GatewayState,
        GatewayStatus,
        NodeUplink,
        NodeDownlink,
        ProvisionRequest,
        ProvisionResponse
    }

    /// <summary>Hậu tố của topic uplink node: <c>ts/{line}/n/{node}/up/{kind}</c>.</summary>
    public enum UplinkKind
    {
        Data,
        Status,
        Config,
        Ack,
        Join
    }

    /// <summary>Topic đã phân tích. <see cref="Uplink"/> chỉ có khi Kind = NodeUplink, <see cref="HardwareId"/> chỉ có ở topic cấp phát.</summary>
    public readonly record struct TopicAddress(TopicKind Kind, int LineId, byte NodeId, UplinkKind? Uplink, string? HardwareId);

    /// <summary>
    /// Cây topic MQTT giữa NM và PC (docs/lan-architecture-protocol.md mục 4.1). Dùng chung cho
    /// BackendV2 và trình giả lập để hai phía không thể lệch nhau về cách đặt tên topic.
    /// </summary>
    public static partial class GatewayTopics
    {
        public const int MinLineId = 1;
        public const int MaxLineId = 254;
        public const byte UnassignedNodeId = 0;
        public const byte BroadcastNodeId = 255;

        /// <summary>Filter cho PC: trạng thái và thông tin của mọi NM.</summary>
        public const string AllGatewaysFilter = "ts/+/gw/+";

        /// <summary>Filter cho PC: mọi uplink của mọi node trên mọi tuyến.</summary>
        public const string AllNodeUplinksFilter = "ts/+/n/+/up/+";

        /// <summary>Filter cho PC: yêu cầu cấp tuyến của NM mới.</summary>
        public const string AllProvisionRequestsFilter = "ts/prov/+/req";

        private const string Root = "ts";
        private const string ProvisionSegment = "prov";

        public static string GatewayState(int lineId) => $"{Root}/{CheckLine(lineId)}/gw/state";

        public static string GatewayStatus(int lineId) => $"{Root}/{CheckLine(lineId)}/gw/status";

        public static string NodeUplink(int lineId, byte nodeId, UplinkKind kind) =>
            $"{Root}/{CheckLine(lineId)}/n/{nodeId}/up/{UplinkSegment(kind)}";

        public static string Join(int lineId) => NodeUplink(lineId, UnassignedNodeId, UplinkKind.Join);

        public static string NodeDownlink(int lineId, byte nodeId) => $"{Root}/{CheckLine(lineId)}/n/{nodeId}/down";

        /// <summary>Filter cho NM: mọi lệnh xuống node của tuyến mình.</summary>
        public static string LineDownlinkFilter(int lineId) => $"{Root}/{CheckLine(lineId)}/n/+/down";

        public static string ProvisionRequest(string hardwareId) => $"{Root}/{ProvisionSegment}/{CheckHardwareId(hardwareId)}/req";

        public static string ProvisionResponse(string hardwareId) => $"{Root}/{ProvisionSegment}/{CheckHardwareId(hardwareId)}/resp";

        public static bool IsValidHardwareId(string? hardwareId) =>
            hardwareId is not null && HardwareIdPattern().IsMatch(hardwareId);

        public static bool TryParse(string topic, out TopicAddress address)
        {
            address = default;
            var parts = topic.Split('/');
            if (parts.Length < 4 || parts[0] != Root)
            {
                return false;
            }

            if (parts[1] == ProvisionSegment)
            {
                return TryParseProvision(parts, out address);
            }

            if (!TryParseLine(parts[1], out var lineId))
            {
                return false;
            }

            return parts[2] switch
            {
                "gw" => TryParseGateway(parts, lineId, out address),
                "n" => TryParseNode(parts, lineId, out address),
                _ => false
            };
        }

        private static bool TryParseProvision(string[] parts, out TopicAddress address)
        {
            address = default;
            if (parts.Length != 4 || !IsValidHardwareId(parts[2]))
            {
                return false;
            }

            TopicKind? kind = parts[3] switch
            {
                "req" => TopicKind.ProvisionRequest,
                "resp" => TopicKind.ProvisionResponse,
                _ => null
            };
            if (kind is null)
            {
                return false;
            }

            address = new TopicAddress(kind.Value, 0, 0, null, parts[2]);
            return true;
        }

        private static bool TryParseGateway(string[] parts, int lineId, out TopicAddress address)
        {
            address = default;
            if (parts.Length != 4)
            {
                return false;
            }

            TopicKind? kind = parts[3] switch
            {
                "state" => TopicKind.GatewayState,
                "status" => TopicKind.GatewayStatus,
                _ => null
            };
            if (kind is null)
            {
                return false;
            }

            address = new TopicAddress(kind.Value, lineId, 0, null, null);
            return true;
        }

        private static bool TryParseNode(string[] parts, int lineId, out TopicAddress address)
        {
            address = default;
            if (!byte.TryParse(parts[3], out var nodeId) || parts[3] != nodeId.ToString())
            {
                return false;
            }

            if (parts.Length == 5 && parts[4] == "down")
            {
                address = new TopicAddress(TopicKind.NodeDownlink, lineId, nodeId, null, null);
                return true;
            }

            if (parts.Length != 6 || parts[4] != "up" || !TryParseUplink(parts[5], out var uplink))
            {
                return false;
            }

            address = new TopicAddress(TopicKind.NodeUplink, lineId, nodeId, uplink, null);
            return true;
        }

        private static bool TryParseLine(string segment, out int lineId) =>
            int.TryParse(segment, out lineId)
            && segment == lineId.ToString()
            && lineId is >= MinLineId and <= MaxLineId;

        private static bool TryParseUplink(string segment, out UplinkKind kind)
        {
            UplinkKind? parsed = segment switch
            {
                "data" => UplinkKind.Data,
                "status" => UplinkKind.Status,
                "config" => UplinkKind.Config,
                "ack" => UplinkKind.Ack,
                "join" => UplinkKind.Join,
                _ => null
            };
            kind = parsed.GetValueOrDefault();
            return parsed is not null;
        }

        private static string UplinkSegment(UplinkKind kind) => kind switch
        {
            UplinkKind.Data => "data",
            UplinkKind.Status => "status",
            UplinkKind.Config => "config",
            UplinkKind.Ack => "ack",
            UplinkKind.Join => "join",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        private static int CheckLine(int lineId) =>
            lineId is >= MinLineId and <= MaxLineId
                ? lineId
                : throw new ArgumentOutOfRangeException(nameof(lineId), lineId, $"Line id must be {MinLineId}..{MaxLineId}");

        private static string CheckHardwareId(string hardwareId) =>
            IsValidHardwareId(hardwareId)
                ? hardwareId
                : throw new ArgumentException($"Invalid hardware id '{hardwareId}'", nameof(hardwareId));

        // Không cho '/', '+', '#' hay khoảng trắng: chúng sẽ phá cấu trúc topic hoặc thành wildcard.
        [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
        private static partial Regex HardwareIdPattern();
    }
}
