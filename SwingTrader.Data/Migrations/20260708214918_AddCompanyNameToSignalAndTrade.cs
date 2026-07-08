using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyNameToSignalAndTrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Trades",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "StockSignals",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // One-time backfill from WatchlistItems (which already carries the
            // Finnhub-sourced company name for every symbol an account has
            // researched) so existing trades/signals get a hover tooltip
            // immediately rather than waiting for the symbol to be rescored.
            migrationBuilder.Sql(@"
                UPDATE t
                SET t.CompanyName = w.CompanyName
                FROM Trades t
                CROSS APPLY (
                    SELECT TOP 1 CompanyName FROM WatchlistItems
                    WHERE AccountId = t.AccountId AND Symbol = t.Symbol
                ) w
                WHERE t.CompanyName IS NULL;

                UPDATE s
                SET s.CompanyName = w.CompanyName
                FROM StockSignals s
                CROSS APPLY (
                    SELECT TOP 1 CompanyName FROM WatchlistItems
                    WHERE AccountId = s.AccountId AND Symbol = s.Symbol
                ) w
                WHERE s.CompanyName IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "StockSignals");
        }
    }
}
