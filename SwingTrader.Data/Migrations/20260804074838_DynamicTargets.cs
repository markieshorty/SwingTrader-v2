using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class DynamicTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TargetBandCeilingPct",
                table: "AccountRiskProfiles",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0.25m);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetBandFloorPct",
                table: "AccountRiskProfiles",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0.05m);

            migrationBuilder.AddColumn<int>(
                name: "TargetMode",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetBandCeilingPct",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "TargetBandFloorPct",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "TargetMode",
                table: "AccountRiskProfiles");
        }
    }
}
