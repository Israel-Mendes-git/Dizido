using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dizido.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AttachmentId",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedById = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ThumbnailKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_attachments_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_attachments_user_profiles_UploadedById",
                        column: x => x.UploadedById,
                        principalTable: "user_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_AttachmentId",
                table: "messages",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_attachments_ConversationId",
                table: "attachments",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_pendentes",
                table: "attachments",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_attachments_UploadedById",
                table: "attachments",
                column: "UploadedById");

            migrationBuilder.CreateIndex(
                name: "ux_attachments_storage_key",
                table: "attachments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_messages_attachments_AttachmentId",
                table: "messages",
                column: "AttachmentId",
                principalTable: "attachments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_messages_attachments_AttachmentId",
                table: "messages");

            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropIndex(
                name: "IX_messages_AttachmentId",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "AttachmentId",
                table: "messages");
        }
    }
}
