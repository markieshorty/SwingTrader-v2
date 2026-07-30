using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountAutoPauseVetoDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AutoPauseVetoDateDemo",
                table: "Accounts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AutoPauseVetoDateLive",
                table: "Accounts",
                type: "date",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AutoPauseVetoDateDemo", "AutoPauseVetoDateLive" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPauseVetoDateDemo",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AutoPauseVetoDateLive",
                table: "Accounts");
        }
    }
}
