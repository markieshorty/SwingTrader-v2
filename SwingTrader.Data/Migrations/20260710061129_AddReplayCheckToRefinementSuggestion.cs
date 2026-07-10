using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayCheckToRefinementSuggestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReplayCheckPassed",
                table: "RefinementSuggestions",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReplayCurrentAvgReturnPct",
                table: "RefinementSuggestions",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReplaySuggestedAvgReturnPct",
                table: "RefinementSuggestions",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplayTradesKept",
                table: "RefinementSuggestions",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayCheckPassed",
                table: "RefinementSuggestions");

            migrationBuilder.DropColumn(
                name: "ReplayCurrentAvgReturnPct",
                table: "RefinementSuggestions");

            migrationBuilder.DropColumn(
                name: "ReplaySuggestedAvgReturnPct",
                table: "RefinementSuggestions");

            migrationBuilder.DropColumn(
                name: "ReplayTradesKept",
                table: "RefinementSuggestions");
        }
    }
}
