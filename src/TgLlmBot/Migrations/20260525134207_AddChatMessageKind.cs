using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgLlmBot.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ChatHistory",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE public."ChatHistory"
                SET "Kind" = CASE
                    WHEN "IsLlmReplyToMessage" = TRUE THEN 1
                    WHEN "IsLlmReplyToMessage" = FALSE AND "Text" LIKE '!%' THEN 2
                    ELSE 0
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "idx_chathistory_chatid_kind_date_desc",
                table: "ChatHistory",
                columns: new[] { "ChatId", "Kind", "Date" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_chathistory_chatid_kind_date_desc",
                table: "ChatHistory");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ChatHistory");
        }
    }
}
