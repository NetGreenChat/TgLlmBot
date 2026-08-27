using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgLlmBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageMediaMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaGroupId",
                table: "ChatHistory",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatMessageMedia",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    ChatMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileUniqueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DownloadFileId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Emoji = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SetName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsAnimated = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ShortDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RecognizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessageMedia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessageMedia_ChatHistory_ChatMessageId",
                        column: x => x.ChatMessageId,
                        principalTable: "ChatHistory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaDescriptions",
                columns: table => new
                {
                    FileUniqueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaDescriptions", x => x.FileUniqueId);
                });

            migrationBuilder.CreateIndex(
                name: "idx_chathistory_chatid_mediagroupid",
                table: "ChatHistory",
                columns: new[] { "ChatId", "MediaGroupId" },
                filter: "\"MediaGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_chatmessagemedia_chatmessageid_order",
                table: "ChatMessageMedia",
                columns: new[] { "ChatMessageId", "Order" });

            migrationBuilder.CreateIndex(
                name: "idx_chatmessagemedia_fileuniqueid",
                table: "ChatMessageMedia",
                column: "FileUniqueId");

            migrationBuilder.CreateIndex(
                name: "idx_chatmessagemedia_status_recognizedat",
                table: "ChatMessageMedia",
                columns: new[] { "Status", "RecognizedAt" });

            migrationBuilder.CreateIndex(
                name: "idx_mediadescriptions_createdat",
                table: "MediaDescriptions",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessageMedia");

            migrationBuilder.DropTable(
                name: "MediaDescriptions");

            migrationBuilder.DropIndex(
                name: "idx_chathistory_chatid_mediagroupid",
                table: "ChatHistory");

            migrationBuilder.DropColumn(
                name: "MediaGroupId",
                table: "ChatHistory");
        }
    }
}
