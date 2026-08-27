using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgLlmBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaThumbnailFileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThumbnailFileId",
                table: "ChatMessageMedia",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThumbnailFileId",
                table: "ChatMessageMedia");
        }
    }
}
