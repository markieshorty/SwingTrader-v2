using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFunnelShadowScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ForwardScore",
                table: "StockSignals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForwardScoreDegraded",
                table: "StockSignals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "GateScore",
                table: "StockSignals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WouldBeVetoed",
                table: "StockSignals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WouldPassGate",
                table: "StockSignals",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForwardScore",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "ForwardScoreDegraded",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "GateScore",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "WouldBeVetoed",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "WouldPassGate",
                table: "StockSignals");
        }
    }
}
