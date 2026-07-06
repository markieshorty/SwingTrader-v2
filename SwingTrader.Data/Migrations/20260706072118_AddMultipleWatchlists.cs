using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultipleWatchlists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WatchlistItems_AccountId_Symbol",
                table: "WatchlistItems");

            migrationBuilder.AddColumn<int>(
                name: "WatchlistId",
                table: "WatchlistItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Watchlists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Watchlists_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 1,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 2,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 3,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 4,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 5,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 6,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 7,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 8,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 9,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 10,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 11,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 12,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 13,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 14,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 15,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 16,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 17,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 18,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 19,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 20,
                column: "WatchlistId",
                value: 1);

            migrationBuilder.InsertData(
                table: "Watchlists",
                columns: new[] { "Id", "AccountId", "CreatedAt", "Description", "IsDefault", "IsEnabled", "Name", "Type", "UpdatedAt" },
                values: new object[] { 1, 1, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, true, "AI Picks", 0, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });

            // Backfill for every pre-existing (non-seed-data) account: one default
            // AiManaged/enabled/default "AI Picks" watchlist, with every existing
            // WatchlistItem re-pointed at it. Must run before the unique index
            // below is created - every real account's items currently share the
            // AddColumn default of WatchlistId=0, which would collide the moment
            // two accounts both have e.g. "AAPL" (every account is seeded with the
            // same starter symbols).
            migrationBuilder.Sql(@"
                INSERT INTO Watchlists (AccountId, Name, Type, IsEnabled, IsDefault, Description, CreatedAt, UpdatedAt)
                SELECT DISTINCT AccountId, 'AI Picks', 0, 1, 1, NULL, GETUTCDATE(), GETUTCDATE()
                FROM WatchlistItems
                WHERE AccountId <> 1;

                UPDATE wi
                SET wi.WatchlistId = w.Id
                FROM WatchlistItems wi
                JOIN Watchlists w ON w.AccountId = wi.AccountId AND w.IsDefault = 1
                WHERE wi.AccountId <> 1;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_AccountId",
                table: "WatchlistItems",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_WatchlistId_Symbol",
                table: "WatchlistItems",
                columns: new[] { "WatchlistId", "Symbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_AccountId",
                table: "Watchlists",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_WatchlistItems_Watchlists_WatchlistId",
                table: "WatchlistItems",
                column: "WatchlistId",
                principalTable: "Watchlists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WatchlistItems_Watchlists_WatchlistId",
                table: "WatchlistItems");

            migrationBuilder.DropTable(
                name: "Watchlists");

            migrationBuilder.DropIndex(
                name: "IX_WatchlistItems_AccountId",
                table: "WatchlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WatchlistItems_WatchlistId_Symbol",
                table: "WatchlistItems");

            migrationBuilder.DropColumn(
                name: "WatchlistId",
                table: "WatchlistItems");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_AccountId_Symbol",
                table: "WatchlistItems",
                columns: new[] { "AccountId", "Symbol" },
                unique: true);
        }
    }
}
