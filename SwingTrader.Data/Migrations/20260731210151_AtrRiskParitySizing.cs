using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AtrRiskParitySizing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Atr14",
                table: "StockSignals",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AtrStopMultiple",
                table: "AccountRiskProfiles",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 2.0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AtrTargetMultiple",
                table: "AccountRiskProfiles",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 3.5m);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskPerTradePct",
                table: "AccountRiskProfiles",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0.01m);

            migrationBuilder.AddColumn<int>(
                name: "SizingStyle",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Atr14",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "AtrStopMultiple",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "AtrTargetMultiple",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "RiskPerTradePct",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "SizingStyle",
                table: "AccountRiskProfiles");
        }
    }
}
