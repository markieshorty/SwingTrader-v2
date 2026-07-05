using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WatchlistItems_Symbol",
                table: "WatchlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WatchlistHistory_Symbol_WeekStarting",
                table: "WatchlistHistory");

            migrationBuilder.DropIndex(
                name: "IX_Trades_Symbol_Status",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_TradeApprovals_TradeDate",
                table: "TradeApprovals");

            migrationBuilder.DropIndex(
                name: "IX_TierEvaluationRecords_EvaluatedAt",
                table: "TierEvaluationRecords");

            migrationBuilder.DropIndex(
                name: "IX_SystemChecklists_CheckName",
                table: "SystemChecklists");

            migrationBuilder.DropIndex(
                name: "IX_StockSignals_Symbol_SignalDate",
                table: "StockSignals");

            migrationBuilder.DropIndex(
                name: "IX_RefinementSuggestions_GeneratedAt",
                table: "RefinementSuggestions");

            migrationBuilder.DropIndex(
                name: "IX_ReadinessSnapshots_SnapshotDate",
                table: "ReadinessSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_PortfolioSnapshots_SnapshotDate",
                table: "PortfolioSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_DailyReports_ReportDate",
                table: "DailyReports");

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "WorkerHeartbeats",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "WatchlistItems",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "WatchlistHistory",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Trades",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "TradeApprovals",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "TierEvaluationRecords",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "SystemChecklists",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "StrategyWeights",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "StockSignals",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "StockCandles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "RefinementSuggestions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "ReadinessSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "PortfolioSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "DailyReports",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "AccountInvites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    InvitedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InvitedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountInvites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    T212AccountId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    GlobalRefinementOptIn = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    FirstLoginAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsSuspended = table.Column<bool>(type: "bit", nullable: false),
                    SuspendedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsOnboarded = table.Column<bool>(type: "bit", nullable: false),
                    OnboardingStep = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Accounts",
                columns: new[] { "Id", "CreatedAt", "GlobalRefinementOptIn", "Name", "T212AccountId", "UpdatedAt" },
                values: new object[] { 1, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), false, "system", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.UpdateData(
                table: "StrategyWeights",
                keyColumn: "Id",
                keyValue: 1,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "SystemChecklists",
                keyColumn: "Id",
                keyValue: 1,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "SystemChecklists",
                keyColumn: "Id",
                keyValue: 2,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 1,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 2,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 3,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 4,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 5,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 6,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 7,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 8,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 9,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 10,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 11,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 12,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 13,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 14,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 15,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 16,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 17,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 18,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 19,
                column: "AccountId",
                value: 1);

            migrationBuilder.UpdateData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: 20,
                column: "AccountId",
                value: 1);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_AccountId",
                table: "WorkerHeartbeats",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_AccountId_Symbol",
                table: "WatchlistItems",
                columns: new[] { "AccountId", "Symbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistHistory_AccountId_Symbol_WeekStarting",
                table: "WatchlistHistory",
                columns: new[] { "AccountId", "Symbol", "WeekStarting" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId_Symbol_Status",
                table: "Trades",
                columns: new[] { "AccountId", "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TradeApprovals_AccountId_TradeDate",
                table: "TradeApprovals",
                columns: new[] { "AccountId", "TradeDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TierEvaluationRecords_AccountId_EvaluatedAt",
                table: "TierEvaluationRecords",
                columns: new[] { "AccountId", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemChecklists_AccountId_CheckName",
                table: "SystemChecklists",
                columns: new[] { "AccountId", "CheckName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrategyWeights_AccountId",
                table: "StrategyWeights",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_StockSignals_AccountId_Symbol_SignalDate",
                table: "StockSignals",
                columns: new[] { "AccountId", "Symbol", "SignalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCandles_AccountId",
                table: "StockCandles",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RefinementSuggestions_AccountId_GeneratedAt",
                table: "RefinementSuggestions",
                columns: new[] { "AccountId", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReadinessSnapshots_AccountId_SnapshotDate",
                table: "ReadinessSnapshots",
                columns: new[] { "AccountId", "SnapshotDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioSnapshots_AccountId_SnapshotDate",
                table: "PortfolioSnapshots",
                columns: new[] { "AccountId", "SnapshotDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyReports_AccountId_ReportDate",
                table: "DailyReports",
                columns: new[] { "AccountId", "ReportDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountInvites_AccountId",
                table: "AccountInvites",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountInvites_Token",
                table: "AccountInvites",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_AccountId",
                table: "AppUsers",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_UserId",
                table: "AppUsers",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DailyReports_Accounts_AccountId",
                table: "DailyReports",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortfolioSnapshots_Accounts_AccountId",
                table: "PortfolioSnapshots",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReadinessSnapshots_Accounts_AccountId",
                table: "ReadinessSnapshots",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RefinementSuggestions_Accounts_AccountId",
                table: "RefinementSuggestions",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockCandles_Accounts_AccountId",
                table: "StockCandles",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockSignals_Accounts_AccountId",
                table: "StockSignals",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StrategyWeights_Accounts_AccountId",
                table: "StrategyWeights",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SystemChecklists_Accounts_AccountId",
                table: "SystemChecklists",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TierEvaluationRecords_Accounts_AccountId",
                table: "TierEvaluationRecords",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TradeApprovals_Accounts_AccountId",
                table: "TradeApprovals",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Trades_Accounts_AccountId",
                table: "Trades",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WatchlistHistory_Accounts_AccountId",
                table: "WatchlistHistory",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WatchlistItems_Accounts_AccountId",
                table: "WatchlistItems",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkerHeartbeats_Accounts_AccountId",
                table: "WorkerHeartbeats",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DailyReports_Accounts_AccountId",
                table: "DailyReports");

            migrationBuilder.DropForeignKey(
                name: "FK_PortfolioSnapshots_Accounts_AccountId",
                table: "PortfolioSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_ReadinessSnapshots_Accounts_AccountId",
                table: "ReadinessSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_RefinementSuggestions_Accounts_AccountId",
                table: "RefinementSuggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_StockCandles_Accounts_AccountId",
                table: "StockCandles");

            migrationBuilder.DropForeignKey(
                name: "FK_StockSignals_Accounts_AccountId",
                table: "StockSignals");

            migrationBuilder.DropForeignKey(
                name: "FK_StrategyWeights_Accounts_AccountId",
                table: "StrategyWeights");

            migrationBuilder.DropForeignKey(
                name: "FK_SystemChecklists_Accounts_AccountId",
                table: "SystemChecklists");

            migrationBuilder.DropForeignKey(
                name: "FK_TierEvaluationRecords_Accounts_AccountId",
                table: "TierEvaluationRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_TradeApprovals_Accounts_AccountId",
                table: "TradeApprovals");

            migrationBuilder.DropForeignKey(
                name: "FK_Trades_Accounts_AccountId",
                table: "Trades");

            migrationBuilder.DropForeignKey(
                name: "FK_WatchlistHistory_Accounts_AccountId",
                table: "WatchlistHistory");

            migrationBuilder.DropForeignKey(
                name: "FK_WatchlistItems_Accounts_AccountId",
                table: "WatchlistItems");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkerHeartbeats_Accounts_AccountId",
                table: "WorkerHeartbeats");

            migrationBuilder.DropTable(
                name: "AccountInvites");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropIndex(
                name: "IX_WorkerHeartbeats_AccountId",
                table: "WorkerHeartbeats");

            migrationBuilder.DropIndex(
                name: "IX_WatchlistItems_AccountId_Symbol",
                table: "WatchlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WatchlistHistory_AccountId_Symbol_WeekStarting",
                table: "WatchlistHistory");

            migrationBuilder.DropIndex(
                name: "IX_Trades_AccountId_Symbol_Status",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_TradeApprovals_AccountId_TradeDate",
                table: "TradeApprovals");

            migrationBuilder.DropIndex(
                name: "IX_TierEvaluationRecords_AccountId_EvaluatedAt",
                table: "TierEvaluationRecords");

            migrationBuilder.DropIndex(
                name: "IX_SystemChecklists_AccountId_CheckName",
                table: "SystemChecklists");

            migrationBuilder.DropIndex(
                name: "IX_StrategyWeights_AccountId",
                table: "StrategyWeights");

            migrationBuilder.DropIndex(
                name: "IX_StockSignals_AccountId_Symbol_SignalDate",
                table: "StockSignals");

            migrationBuilder.DropIndex(
                name: "IX_StockCandles_AccountId",
                table: "StockCandles");

            migrationBuilder.DropIndex(
                name: "IX_RefinementSuggestions_AccountId_GeneratedAt",
                table: "RefinementSuggestions");

            migrationBuilder.DropIndex(
                name: "IX_ReadinessSnapshots_AccountId_SnapshotDate",
                table: "ReadinessSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_PortfolioSnapshots_AccountId_SnapshotDate",
                table: "PortfolioSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_DailyReports_AccountId_ReportDate",
                table: "DailyReports");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "WorkerHeartbeats");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "WatchlistItems");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "WatchlistHistory");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "TradeApprovals");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "TierEvaluationRecords");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "SystemChecklists");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "StrategyWeights");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "StockCandles");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "RefinementSuggestions");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "ReadinessSnapshots");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "PortfolioSnapshots");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "DailyReports");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_Symbol",
                table: "WatchlistItems",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistHistory_Symbol_WeekStarting",
                table: "WatchlistHistory",
                columns: new[] { "Symbol", "WeekStarting" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_Symbol_Status",
                table: "Trades",
                columns: new[] { "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TradeApprovals_TradeDate",
                table: "TradeApprovals",
                column: "TradeDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TierEvaluationRecords_EvaluatedAt",
                table: "TierEvaluationRecords",
                column: "EvaluatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SystemChecklists_CheckName",
                table: "SystemChecklists",
                column: "CheckName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockSignals_Symbol_SignalDate",
                table: "StockSignals",
                columns: new[] { "Symbol", "SignalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RefinementSuggestions_GeneratedAt",
                table: "RefinementSuggestions",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReadinessSnapshots_SnapshotDate",
                table: "ReadinessSnapshots",
                column: "SnapshotDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioSnapshots_SnapshotDate",
                table: "PortfolioSnapshots",
                column: "SnapshotDate");

            migrationBuilder.CreateIndex(
                name: "IX_DailyReports_ReportDate",
                table: "DailyReports",
                column: "ReportDate",
                unique: true);
        }
    }
}
