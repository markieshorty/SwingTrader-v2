using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBacktestRunProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletedCandidates",
                table: "BacktestRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalCandidates",
                table: "BacktestRuns",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedCandidates",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "TotalCandidates",
                table: "BacktestRuns");
        }
    }
}
