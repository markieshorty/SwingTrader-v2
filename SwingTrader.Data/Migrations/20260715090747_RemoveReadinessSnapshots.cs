using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveReadinessSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReadinessSnapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadinessSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ObservedWinRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    RegimeBearCount = table.Column<int>(type: "int", nullable: false),
                    RegimeBullCount = table.Column<int>(type: "int", nullable: false),
                    RegimeNeutralCount = table.Column<int>(type: "int", nullable: false),
                    ScoredClosedTrades = table.Column<int>(type: "int", nullable: false),
                    SnapshotDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SystemRunningDays = table.Column<int>(type: "int", nullable: false),
                    TradesPerWeekWeighted = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    TradingMode = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadinessSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadinessSnapshots_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReadinessSnapshots_AccountId_SnapshotDate",
                table: "ReadinessSnapshots",
                columns: new[] { "AccountId", "SnapshotDate" },
                unique: true);
        }
    }
}
