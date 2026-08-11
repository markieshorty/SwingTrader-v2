using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameThresholdsForClarity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "BuyThreshold",
                table: "StrategyWeights",
                newName: "GateThreshold");

            migrationBuilder.RenameColumn(
                name: "ForwardVetoFloor",
                table: "AccountRiskProfiles",
                newName: "ForwardBuyThreshold");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GateThreshold",
                table: "StrategyWeights",
                newName: "BuyThreshold");

            migrationBuilder.RenameColumn(
                name: "ForwardBuyThreshold",
                table: "AccountRiskProfiles",
                newName: "ForwardVetoFloor");
        }
    }
}
