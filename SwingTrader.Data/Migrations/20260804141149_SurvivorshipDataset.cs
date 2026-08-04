using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class SurvivorshipDataset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricalDatasetInfo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatasetVersion = table.Column<int>(type: "int", nullable: false),
                    LastDelistedBackfillAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalDatasetInfo", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SymbolLifecycles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ListedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    DelistedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolLifecycles", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "HistoricalDatasetInfo",
                columns: new[] { "Id", "DatasetVersion", "LastDelistedBackfillAt" },
                values: new object[] { 1, 1, null });

            migrationBuilder.CreateIndex(
                name: "IX_SymbolLifecycles_Symbol",
                table: "SymbolLifecycles",
                column: "Symbol",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalDatasetInfo");

            migrationBuilder.DropTable(
                name: "SymbolLifecycles");
        }
    }
}
