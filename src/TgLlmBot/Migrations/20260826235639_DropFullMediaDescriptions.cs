using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgLlmBot.Migrations
{
    /// <inheritdoc />
    public partial class DropFullMediaDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Промежуточное состояние "распознан, но не сжат" больше не существует: недожатые
            // вложения возвращаются в очередь и обрабатываются заново по новому пайплайну
            migrationBuilder.Sql(
                """
                UPDATE "ChatMessageMedia"
                SET "Status" = 'Pending'
                WHERE "Status" = 'Recognized';
                """);

            migrationBuilder.DropIndex(
                name: "idx_chatmessagemedia_status_recognizedat",
                table: "ChatMessageMedia");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "ChatMessageMedia");

            migrationBuilder.DropColumn(
                name: "RecognizedAt",
                table: "ChatMessageMedia");

            migrationBuilder.CreateIndex(
                name: "idx_chatmessagemedia_status",
                table: "ChatMessageMedia",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_chatmessagemedia_status",
                table: "ChatMessageMedia");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "ChatMessageMedia",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecognizedAt",
                table: "ChatMessageMedia",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_chatmessagemedia_status_recognizedat",
                table: "ChatMessageMedia",
                columns: new[] { "Status", "RecognizedAt" });
        }
    }
}
