using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionPauseReasonAndTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExecutionPauseReasonDemo",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionPauseReasonLive",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionPausedAtDemo",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExecutionPausedAtLive",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ExecutionPauseReasonDemo", "ExecutionPauseReasonLive", "ExecutionPausedAtDemo", "ExecutionPausedAtLive" },
                values: new object[] { 0, 0, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionPauseReasonDemo",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ExecutionPauseReasonLive",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ExecutionPausedAtDemo",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ExecutionPausedAtLive",
                table: "Accounts");
        }
    }
}
