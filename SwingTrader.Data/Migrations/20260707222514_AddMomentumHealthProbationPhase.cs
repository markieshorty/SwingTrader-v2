using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMomentumHealthProbationPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MomentumHealthCheckedAt",
                table: "Trades",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MomentumHealthReasoning",
                table: "Trades",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MomentumHealthScore",
                table: "Trades",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MomentumHealthVerdict",
                table: "Trades",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Phase",
                table: "Trades",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhaseConfirmedAt",
                table: "Trades",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinHoldDays",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<decimal>(
                name: "MomentumHealthThreshold",
                table: "AccountRiskProfiles",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0.35m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MomentumHealthCheckedAt",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "MomentumHealthReasoning",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "MomentumHealthScore",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "MomentumHealthVerdict",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "PhaseConfirmedAt",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "MinHoldDays",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "MomentumHealthThreshold",
                table: "AccountRiskProfiles");
        }
    }
}
