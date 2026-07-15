using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveTargetWatchlistSizeToAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. New account-level column, defaulted to the standard 25.
            migrationBuilder.AddColumn<int>(
                name: "TargetWatchlistSize",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 25);

            // 2. Backfill each account from its Neutral risk book (the value was
            //    duplicated across all four regime books, so Neutral (=1) is a
            //    faithful source) BEFORE the old column is dropped.
            migrationBuilder.Sql(@"
                UPDATE a
                SET a.TargetWatchlistSize = arp.TargetWatchlistSize
                FROM Accounts a
                INNER JOIN AccountRiskProfiles arp
                    ON arp.AccountId = a.Id AND arp.Regime = 1;");

            // 3. Now retire the per-regime column.
            migrationBuilder.DropColumn(
                name: "TargetWatchlistSize",
                table: "AccountRiskProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetWatchlistSize",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 25);

            // Copy the account-level value back onto every regime book.
            migrationBuilder.Sql(@"
                UPDATE arp
                SET arp.TargetWatchlistSize = a.TargetWatchlistSize
                FROM AccountRiskProfiles arp
                INNER JOIN Accounts a ON a.Id = arp.AccountId;");

            migrationBuilder.DropColumn(
                name: "TargetWatchlistSize",
                table: "Accounts");
        }
    }
}
