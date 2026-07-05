using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountRiskProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountRiskProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LockedCapitalPct = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MaxPositionPctOfActive = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MaxOpenPositions = table.Column<int>(type: "int", nullable: false),
                    DailyLossCircuitBreakerPct = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Tier1UnlockMinTrades = table.Column<int>(type: "int", nullable: false),
                    Tier1UnlockMinWinRate = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Tier2UnlockMinTrades = table.Column<int>(type: "int", nullable: false),
                    Tier2UnlockMinWinRate = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRiskProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountRiskProfiles_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountRiskProfiles_AccountId",
                table: "AccountRiskProfiles",
                column: "AccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountRiskProfiles");
        }
    }
}
