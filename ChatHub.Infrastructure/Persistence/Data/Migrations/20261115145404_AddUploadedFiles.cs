using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatHub.Infrastructure.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttachmentUrl",
                table: "Messages");

            migrationBuilder.AddColumn<Guid>(
                name: "AttachmentFileId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UploadedFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttachedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadedFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadedFiles_UserProfiles_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_IsDeleted_AttachedAtUtc",
                table: "UploadedFiles",
                columns: new[] { "IsDeleted", "AttachedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_OwnerId",
                table: "UploadedFiles",
                column: "OwnerId");

            migrationBuilder.Sql("CREATE INDEX \"IX_Messages_AttachmentFileId\" ON \"Messages\" (\"AttachmentFileId\") WHERE \"AttachmentFileId\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Messages_AttachmentFileId\";");

            migrationBuilder.DropTable(
                name: "UploadedFiles");

            migrationBuilder.DropColumn(
                name: "AttachmentFileId",
                table: "Messages");

            migrationBuilder.AddColumn<string>(
                name: "AttachmentUrl",
                table: "Messages",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }
    }
}
