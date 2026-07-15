using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCapitalTierLadder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TierEvaluationRecords");

            migrationBuilder.DropColumn(
                name: "CurrentTier",
                table: "PortfolioSnapshots");

            migrationBuilder.DropColumn(
                name: "MaxPositionPctOfActive",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "Tier1UnlockMinTrades",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "Tier1UnlockMinWinRate",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "Tier2UnlockMinTrades",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "Tier2UnlockMinWinRate",
                table: "AccountRiskProfiles");

            // Sizing mode enum changed from {TierLadder=0, Flat=1} to
            // {Flat=0, Funnel=1}. Put every existing book on Flat (0): old
            // TierLadder falls back to Flat, and old Flat stays Flat rather
            // than being misread as the new Funnel value. Funnel is opt-in
            // per book from the UI (and inert anyway while aggressiveness = 0).
            migrationBuilder.Sql("UPDATE AccountRiskProfiles SET SizingMode = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentTier",
                table: "PortfolioSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxPositionPctOfActive",
                table: "AccountRiskProfiles",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Tier1UnlockMinTrades",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Tier1UnlockMinWinRate",
                table: "AccountRiskProfiles",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Tier2UnlockMinTrades",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Tier2UnlockMinWinRate",
                table: "AccountRiskProfiles",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "TierEvaluationRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    ActualTierAfter = table.Column<int>(type: "int", nullable: false),
                    AvgReturnPct = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CurrentTier = table.Column<int>(type: "int", nullable: false),
                    EvaluatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EvaluationPeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    EvaluationPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    MaxDrawdownPct = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SharpeRatio = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SuggestedTier = table.Column<int>(type: "int", nullable: false),
                    TotalTrades = table.Column<int>(type: "int", nullable: false),
                    TradingMode = table.Column<int>(type: "int", nullable: false),
                    UnlockCriteriaMet = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WasApplied = table.Column<bool>(type: "bit", nullable: false),
                    WinRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierEvaluationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TierEvaluationRecords_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TierEvaluationRecords_AccountId_EvaluatedAt",
                table: "TierEvaluationRecords",
                columns: new[] { "AccountId", "EvaluatedAt" });
        }
    }
}
