using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskProfileTradingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EarningsGateDays",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "MaxHoldDays",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<double>(
                name: "TrailingActivationPct",
                table: "AccountRiskProfiles",
                type: "float",
                nullable: false,
                defaultValue: 0.05);

            migrationBuilder.AddColumn<double>(
                name: "TrailingDistancePct",
                table: "AccountRiskProfiles",
                type: "float",
                nullable: false,
                defaultValue: 0.03);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                column: "GlobalRefinementOptIn",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EarningsGateDays",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "MaxHoldDays",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "TrailingActivationPct",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "TrailingDistancePct",
                table: "AccountRiskProfiles");

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                column: "GlobalRefinementOptIn",
                value: false);
        }
    }
}
