using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgLlmBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaDescriptionFallbackFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFallback",
                table: "MediaDescriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Значение по умолчанию верно и для уже накопленного кэша: до этой версии в него
            // попадали только статические стикеры, снятые с собственного файла, а анимированные
            // и видео-стикеры не заводили строки вовсе. Сбрасывать нечего.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFallback",
                table: "MediaDescriptions");
        }
    }
}
