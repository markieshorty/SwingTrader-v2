using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeApprovalAndReportUniqueIndexesByTradingMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradeApprovals_AccountId_TradeDate",
                table: "TradeApprovals");

            migrationBuilder.DropIndex(
                name: "IX_DailyReports_AccountId_ReportDate",
                table: "DailyReports");

            migrationBuilder.CreateIndex(
                name: "IX_TradeApprovals_AccountId_TradingMode_TradeDate",
                table: "TradeApprovals",
                columns: new[] { "AccountId", "TradingMode", "TradeDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyReports_AccountId_TradingMode_ReportDate",
                table: "DailyReports",
                columns: new[] { "AccountId", "TradingMode", "ReportDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradeApprovals_AccountId_TradingMode_TradeDate",
                table: "TradeApprovals");

            migrationBuilder.DropIndex(
                name: "IX_DailyReports_AccountId_TradingMode_ReportDate",
                table: "DailyReports");

            migrationBuilder.CreateIndex(
                name: "IX_TradeApprovals_AccountId_TradeDate",
                table: "TradeApprovals",
                columns: new[] { "AccountId", "TradeDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyReports_AccountId_ReportDate",
                table: "DailyReports",
                columns: new[] { "AccountId", "ReportDate" },
                unique: true);
        }
    }
}
