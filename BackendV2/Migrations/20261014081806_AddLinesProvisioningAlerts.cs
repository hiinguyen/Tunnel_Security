using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendV2.Migrations
{
    /// <inheritdoc />
    public partial class AddLinesProvisioningAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Ordinal",
                table: "Sensors",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LineId",
                table: "Nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PhysicalId",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceIdentities",
                columns: table => new
                {
                    PhysicalId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProtectedKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceIdentities", x => x.PhysicalId);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    NodeByteIds = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Operator = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GatewayHardwareId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GatewayIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GatewayMac = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GatewayVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Rule = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<int>(type: "int", nullable: true),
                    NodeByteId = table.Column<short>(type: "smallint", nullable: true),
                    NodeId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SensorId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SensorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SensorOrdinal = table.Column<short>(type: "smallint", nullable: true),
                    Value = table.Column<double>(type: "float", nullable: true),
                    Threshold = table.Column<double>(type: "float", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_LineId_NodeByteId",
                table: "Nodes",
                columns: new[] { "LineId", "NodeByteId" },
                unique: true,
                filter: "[LineId] IS NOT NULL AND [NodeByteId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_PhysicalId",
                table: "Nodes",
                column: "PhysicalId",
                unique: true,
                filter: "[PhysicalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_IncidentId",
                table: "Alerts",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Key",
                table: "Alerts",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_LineId_CreatedAt",
                table: "Alerts",
                columns: new[] { "LineId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_State_CreatedAt",
                table: "Alerts",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_State_CreatedAt",
                table: "Incidents",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Lines_GatewayHardwareId",
                table: "Lines",
                column: "GatewayHardwareId",
                unique: true,
                filter: "[RetiredAt] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Lines_LineId",
                table: "Nodes",
                column: "LineId",
                principalTable: "Lines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Lines_LineId",
                table: "Nodes");

            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "DeviceIdentities");

            migrationBuilder.DropTable(
                name: "Lines");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_LineId_NodeByteId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_PhysicalId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "Ordinal",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "LineId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "PhysicalId",
                table: "Nodes");
        }
    }
}
