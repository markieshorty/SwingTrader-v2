using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectionPercentile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SelectionPercentile",
                table: "WatchlistItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SelectionPercentile",
                table: "StockSignals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 1,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 2,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 3,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 4,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 5,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 6,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 7,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 8,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 9,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 10,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 11,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 12,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 13,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 14,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 15,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 16,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 17,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 18,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 19,
                column: "SelectionPercentile",
                value: null);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 20,
                column: "SelectionPercentile",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectionPercentile",
                table: "WatchlistItems");

            migrationBuilder.DropColumn(
                name: "SelectionPercentile",
                table: "StockSignals");
        }
    }
}
