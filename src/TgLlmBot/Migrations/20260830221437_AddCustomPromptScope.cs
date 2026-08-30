using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgLlmBot.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomPromptScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CustomPromptUserId",
                table: "ChatHistory",
                type: "bigint",
                nullable: true);

            // Колонка добавляется nullable и заполняется отдельным UPDATE, а не через defaultValue:
            // AddColumn с defaultValue оставил бы на колонке постоянный DEFAULT, которого нет в модели.
            migrationBuilder.AddColumn<string>(
                name: "CustomPromptScope",
                table: "ChatHistory",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Вся накопленная история писалась до появления пометки. Часть ответов бота в ней
            // действительно сгенерирована под дополнительной просьбой, но какой именно - уже
            // не восстановить, поэтому старые строки помечаются как написанные без просьбы.
            migrationBuilder.Sql("""UPDATE "ChatHistory" SET "CustomPromptScope" = 'None';""");

            migrationBuilder.AlterColumn<string>(
                name: "CustomPromptScope",
                table: "ChatHistory",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomPromptScope",
                table: "ChatHistory");

            migrationBuilder.DropColumn(
                name: "CustomPromptUserId",
                table: "ChatHistory");
        }
    }
}
