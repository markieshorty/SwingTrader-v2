using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSetupTactics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SetupTactics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SetupType = table.Column<int>(type: "int", nullable: false),
                    StopLossPct = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    TargetPct = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    GuideHoldDays = table.Column<int>(type: "int", nullable: false),
                    TrailingActivationPct = table.Column<double>(type: "float", nullable: false),
                    TrailingDistancePct = table.Column<double>(type: "float", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupTactics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SetupTactics_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SetupTactics_AccountId_SetupType",
                table: "SetupTactics",
                columns: new[] { "AccountId", "SetupType" },
                unique: true);

            // Continuity backfill: seed one tactics row per tradable setup
            // (OversoldRecovery=0, Breakout=1, MomentumContinuation=2,
            // VolumeSpike=3, TrendFollowing=4) for every existing account, copied
            // from that account's Neutral risk book (Regime=1). Every setup
            // starts identical to today's live exits, so behaviour is unchanged
            // until the owner differentiates them.
            migrationBuilder.Sql(@"
                INSERT INTO SetupTactics
                    (AccountId, SetupType, StopLossPct, TargetPct, GuideHoldDays,
                     TrailingActivationPct, TrailingDistancePct, CreatedAt, UpdatedAt)
                SELECT p.AccountId, s.SetupType, p.StopLossPct, p.TargetPct, p.MaxHoldDays,
                       p.TrailingActivationPct, p.TrailingDistancePct, SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM AccountRiskProfiles p
                CROSS JOIN (VALUES (0),(1),(2),(3),(4)) AS s(SetupType)
                WHERE p.Regime = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SetupTactics");
        }
    }
}
