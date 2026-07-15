using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRegimeRiskBooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountRiskProfiles_AccountId",
                table: "AccountRiskProfiles");

            migrationBuilder.RenameColumn(
                name: "AutopauseDuringBear",
                table: "AccountRiskProfiles",
                newName: "AutopauseTrading");

            migrationBuilder.AddColumn<int>(
                name: "CurrentMarketRegime",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RegimeUpdatedAt",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Regime",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CurrentMarketRegime", "RegimeUpdatedAt" },
                values: new object[] { 1, null });

            // Backfill: the pre-regime single profile becomes each account's
            // Neutral baseline book (enum Neutral = 1, not the column default 0 =
            // Bull). The remaining Bull/Bear/Crisis books are seeded lazily on
            // first access (AccountRiskProfileRepository.SeedDefaultAsync). Every
            // existing account starts in the Neutral regime until Monitor detects
            // otherwise.
            migrationBuilder.Sql("UPDATE AccountRiskProfiles SET Regime = 1;");
            migrationBuilder.Sql("UPDATE Accounts SET CurrentMarketRegime = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRiskProfiles_AccountId_Regime",
                table: "AccountRiskProfiles",
                columns: new[] { "AccountId", "Regime" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountRiskProfiles_AccountId_Regime",
                table: "AccountRiskProfiles");

            migrationBuilder.DropColumn(
                name: "CurrentMarketRegime",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "RegimeUpdatedAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Regime",
                table: "AccountRiskProfiles");

            migrationBuilder.RenameColumn(
                name: "AutopauseTrading",
                table: "AccountRiskProfiles",
                newName: "AutopauseDuringBear");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRiskProfiles_AccountId",
                table: "AccountRiskProfiles",
                column: "AccountId");
        }
    }
}
