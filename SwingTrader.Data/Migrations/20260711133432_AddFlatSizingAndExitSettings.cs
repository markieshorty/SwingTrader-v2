using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFlatSizingAndExitSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FlatPositionPct",
                table: "AccountRiskProfiles",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0.10m);

            migrationBuilder.AddColumn<int>(
                name: "SizingMode",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "StopLossPct",
                table: "AccountRiskProfiles",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0.05m);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetPct",
                table: "AccountRiskProfiles",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0.08m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlatPositionPct",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "SizingMode",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "StopLossPct",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "TargetPct",
                table: "AccountRiskProfiles");
        }
    }
}
