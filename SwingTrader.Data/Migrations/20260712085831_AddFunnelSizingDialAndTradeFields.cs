using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFunnelSizingDialAndTradeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ForwardScoreAtEntry",
                table: "Trades",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SizeMultiplier",
                table: "Trades",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SizingAggressiveness",
                table: "AccountRiskProfiles",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForwardScoreAtEntry",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "SizeMultiplier",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "SizingAggressiveness",
                table: "AccountRiskProfiles");
        }
    }
}
