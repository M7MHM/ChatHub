using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatHub.Infrastructure.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsernames : Migration
    {
        private const string Backfill = """
            DO $$
            DECLARE
                reserved text[] := ARRAY['admin', 'administrator', 'support', 'chathub', 'system', 'api', 'root', 'help',
                    'guest', 'bot', 'null', 'undefined', 'moderator', 'staff', 'official', 'security', 'everyone',
                    'anonymous', 'deleted', 'me', 'settings', 'www', 'mail', 'noreply', 'postmaster', 'abuse', 'webmaster'];
                user_row record;
                base text;
                candidate text;
                suffix int;
            BEGIN
                UPDATE "UserProfiles" SET "Username" = 'deleted_' || left(replace("Id"::text, '-', ''), 22)
                WHERE "IsDeleted" AND "Username" IS NULL;

                FOR user_row IN
                    SELECT "Id", "Email", "DisplayName" FROM "UserProfiles"
                    WHERE "IsDeleted" = false AND "Username" IS NULL
                    ORDER BY "CreatedAtUtc", "Id"
                LOOP
                    base := lower(coalesce(nullif(split_part(user_row."Email", '@', 1), ''), user_row."DisplayName"));
                    base := translate(base,
                        'àáâãäåāăąçćčďđèéêëēėęěğìíîïīįıłñńňòóôõöøōőŕřśşšșťțùúûüūůűųýÿźżžæœßþð',
                        'aaaaaaaaacccddeeeeeeeegiiiiiiilnnnoooooooorrssssttuuuuuuuuyyzzzaostd');
                    base := regexp_replace(base, '[\s-]+', '_', 'g');
                    base := regexp_replace(base, '[^a-z0-9._]', '', 'g');
                    base := regexp_replace(base, '([._])[._]+', '\1', 'g');
                    base := regexp_replace(base, '^[^a-z]+', '');
                    base := regexp_replace(left(base, 24), '[._]+$', '');
                    IF length(base) = 0 THEN
                        base := 'user';
                    ELSIF length(base) < 3 THEN
                        base := base || '_user';
                    END IF;

                    candidate := base;
                    suffix := 1;
                    WHILE candidate = ANY(reserved) OR EXISTS (
                        SELECT 1 FROM "UserProfiles" WHERE "Username" = candidate AND "IsDeleted" = false)
                    LOOP
                        suffix := suffix + 1;
                        candidate := base || suffix::text;
                    END LOOP;

                    UPDATE "UserProfiles" SET "Username" = candidate WHERE "Id" = user_row."Id";
                END LOOP;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "UserProfiles",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UsernameChangedAtUtc",
                table: "UserProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_Username",
                table: "UserProfiles",
                column: "Username",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.Sql(Backfill);

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "UserProfiles",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_Username",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "UsernameChangedAtUtc",
                table: "UserProfiles");
        }
    }
}
