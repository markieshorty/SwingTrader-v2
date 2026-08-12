using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShadowOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShadowOutcomes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SignalId = table.Column<int>(type: "int", nullable: true),
                    Symbol = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    SignalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SetupType = table.Column<int>(type: "int", nullable: false),
                    Membership = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    DialSetVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DatasetVersion = table.Column<int>(type: "int", nullable: false),
                    ReplayedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StopLossPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    TargetPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    GuideHoldDays = table.Column<int>(type: "int", nullable: false),
                    TrailingActivationPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    TrailingDistancePct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    ExitDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    ExitReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ReturnPct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    TradingDaysHeld = table.Column<int>(type: "int", nullable: true),
                    StillOpen = table.Column<bool>(type: "bit", nullable: false),
                    Fwd5Pct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    Fwd20Pct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    Fwd40Pct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    MaxFavorablePct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    MaxAdversePct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    HitPlus25Within40 = table.Column<bool>(type: "bit", nullable: true),
                    HitMinus25Within40 = table.Column<bool>(type: "bit", nullable: true),
                    SectorFwd40Pct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    SectorMoveAtSignalPct = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShadowOutcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShadowOutcomes_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShadowOutcomes_AccountId",
                table: "ShadowOutcomes",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ShadowOutcomes_DialSetVersion_SetupType",
                table: "ShadowOutcomes",
                columns: new[] { "DialSetVersion", "SetupType" });

            migrationBuilder.CreateIndex(
                name: "IX_ShadowOutcomes_Identity",
                table: "ShadowOutcomes",
                columns: new[] { "Symbol", "SignalDate", "SetupType", "DialSetVersion", "DatasetVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShadowOutcomes_SignalDate",
                table: "ShadowOutcomes",
                column: "SignalDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShadowOutcomes");
        }
    }
}
