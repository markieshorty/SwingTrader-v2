using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerHeartbeatsPerAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkerHeartbeats_AccountId",
                table: "WorkerHeartbeats");

            migrationBuilder.DropIndex(
                name: "IX_WorkerHeartbeats_WorkerName",
                table: "WorkerHeartbeats");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_AccountId_WorkerName",
                table: "WorkerHeartbeats",
                columns: new[] { "AccountId", "WorkerName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkerHeartbeats_AccountId_WorkerName",
                table: "WorkerHeartbeats");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_AccountId",
                table: "WorkerHeartbeats",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_WorkerName",
                table: "WorkerHeartbeats",
                column: "WorkerName",
                unique: true);
        }
    }
}
