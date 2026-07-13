using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFilingDeltaStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FilingDeltaScore",
                table: "StockSignals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilingDeltaSummary",
                table: "StockSignals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FilingDeltas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilingId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    FiledAt = table.Column<DateOnly>(type: "date", nullable: false),
                    Direction = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    Materiality = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    Delta = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    Categories = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ScoredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilingDeltas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Filings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Cik = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AccessionNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FilingType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    FiledAt = table.Column<DateOnly>(type: "date", nullable: false),
                    PrimaryDocument = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RiskFactorsHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RiskFactorsText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MdaHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MdaText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParseFailed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Filings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FilingDeltas_FilingId",
                table: "FilingDeltas",
                column: "FilingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilingDeltas_Symbol_FiledAt",
                table: "FilingDeltas",
                columns: new[] { "Symbol", "FiledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Filings_AccessionNumber",
                table: "Filings",
                column: "AccessionNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Filings_Symbol_FilingType_FiledAt",
                table: "Filings",
                columns: new[] { "Symbol", "FilingType", "FiledAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilingDeltas");

            migrationBuilder.DropTable(
                name: "Filings");

            migrationBuilder.DropColumn(
                name: "FilingDeltaScore",
                table: "StockSignals");

            migrationBuilder.DropColumn(
                name: "FilingDeltaSummary",
                table: "StockSignals");
        }
    }
}
