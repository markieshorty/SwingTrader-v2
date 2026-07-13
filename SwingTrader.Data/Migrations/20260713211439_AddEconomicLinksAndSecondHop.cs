using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEconomicLinksAndSecondHop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SecondHopScore",
                table: "StockSignals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondHopSummary",
                table: "StockSignals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EconomicLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    LinkedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LinkedTicker = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Relation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TransmissionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Strength = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Suppressed = table.Column<bool>(type: "bit", nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EconomicLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EconomicLinks_LinkedTicker",
                table: "EconomicLinks",
                column: "LinkedTicker");

            migrationBuilder.CreateIndex(
                name: "IX_EconomicLinks_Symbol",
                table: "EconomicLinks",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EconomicLinks");

            migrationBuilder.DropColumn(
                name: "SecondHopScore",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "SecondHopSummary",
                table: "StockSignals");
        }
    }
}
