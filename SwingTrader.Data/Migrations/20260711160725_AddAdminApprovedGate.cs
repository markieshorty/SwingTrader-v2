using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminApprovedGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true backfills every EXISTING row (nobody already
            // using the app should get locked out). It's harmless that the
            // column keeps this default afterward - application code
            // (UserRegistrationMiddleware) always sets AdminApproved
            // explicitly on every new AppUser it creates, so EF Core never
            // relies on the DB-level default for a fresh insert; it only
            // ever mattered for this one-time backfill.
            migrationBuilder.AddColumn<bool>(
                name: "AdminApproved",
                table: "AppUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminApproved",
                table: "AppUsers");
        }
    }
}
