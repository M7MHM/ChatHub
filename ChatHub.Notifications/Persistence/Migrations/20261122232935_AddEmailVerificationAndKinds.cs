using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatHub.Notifications.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationAndKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "SentNotifications",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SentNotifications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Digest");

            migrationBuilder.AddColumn<bool>(
                name: "EmailNotificationsEnabled",
                table: "Recipients",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailVerified",
                table: "Recipients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UnsubscribeLink",
                table: "Recipients",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnsubscribeLinkIssuedAtUtc",
                table: "Recipients",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SentNotifications");

            migrationBuilder.DropColumn(
                name: "EmailNotificationsEnabled",
                table: "Recipients");

            migrationBuilder.DropColumn(
                name: "EmailVerified",
                table: "Recipients");

            migrationBuilder.DropColumn(
                name: "UnsubscribeLink",
                table: "Recipients");

            migrationBuilder.DropColumn(
                name: "UnsubscribeLinkIssuedAtUtc",
                table: "Recipients");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "SentNotifications",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
