using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingModeToTradingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TradingMode",
                table: "Trades",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TradingMode",
                table: "TradeApprovals",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TradingMode",
                table: "TierEvaluationRecords",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TradingMode",
                table: "RefinementSuggestions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TradingMode",
                table: "ReadinessSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TradingMode",
                table: "DailyReports",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "TradeApprovals");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "TierEvaluationRecords");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "RefinementSuggestions");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "ReadinessSnapshots");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "DailyReports");
        }
    }
}
