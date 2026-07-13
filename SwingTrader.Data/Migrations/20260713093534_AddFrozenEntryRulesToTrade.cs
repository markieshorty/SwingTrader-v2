using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFrozenEntryRulesToTrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxHoldDaysAtEntry",
                table: "Trades",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinHoldDaysAtEntry",
                table: "Trades",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MomentumHealthThresholdAtEntry",
                table: "Trades",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TrailingActivationPctAtEntry",
                table: "Trades",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TrailingDistancePctAtEntry",
                table: "Trades",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxHoldDaysAtEntry",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "MinHoldDaysAtEntry",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "MomentumHealthThresholdAtEntry",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "TrailingActivationPctAtEntry",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "TrailingDistancePctAtEntry",
                table: "Trades");
        }
    }
}
