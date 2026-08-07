using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class FilingEventPriceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LastPrice",
                table: "FilingEvents",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPriceAt",
                table: "FilingEvents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAtCapture",
                table: "FilingEvents",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPrice",
                table: "FilingEvents");

            migrationBuilder.DropColumn(
                name: "LastPriceAt",
                table: "FilingEvents");

            migrationBuilder.DropColumn(
                name: "PriceAtCapture",
                table: "FilingEvents");
        }
    }
}
