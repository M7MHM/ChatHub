using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatHub.Infrastructure.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GuestExpiresAtUtc",
                table: "UserProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemoPersona",
                table: "UserProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsGuest",
                table: "UserProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_GuestExpiresAtUtc",
                table: "UserProfiles",
                column: "GuestExpiresAtUtc",
                filter: "\"IsGuest\" AND NOT \"IsDeleted\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_GuestExpiresAtUtc",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "GuestExpiresAtUtc",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "IsDemoPersona",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "IsGuest",
                table: "UserProfiles");
        }
    }
}
