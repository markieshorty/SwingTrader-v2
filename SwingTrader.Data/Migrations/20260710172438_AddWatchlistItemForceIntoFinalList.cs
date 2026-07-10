using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchlistItemForceIntoFinalList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForceIntoFinalList",
                table: "WatchlistItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 1,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 2,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 3,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 4,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 5,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 6,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 7,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 8,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 9,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 10,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 11,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 12,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 13,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 14,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 15,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 16,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 17,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 18,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 19,
                column: "ForceIntoFinalList",
                value: false);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 20,
                column: "ForceIntoFinalList",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForceIntoFinalList",
                table: "WatchlistItems");
        }
    }
}
