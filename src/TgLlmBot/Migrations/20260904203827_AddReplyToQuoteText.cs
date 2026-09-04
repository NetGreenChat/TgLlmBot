using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgLlmBot.Migrations
{
    /// <inheritdoc />
    public partial class AddReplyToQuoteText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplyToQuoteText",
                table: "ChatHistory",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplyToQuoteText",
                table: "ChatHistory");
        }
    }
}
