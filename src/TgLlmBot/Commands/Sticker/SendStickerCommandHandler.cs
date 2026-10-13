using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Resources;

namespace TgLlmBot.Commands.Sticker;

public class SendStickerCommandHandler : AbstractCommandHandler<SendStickerCommand>
{
    private readonly TelegramBotClient _bot;

    private readonly ITelegramMessageStorage _storage;

    private const string AvailableStickers = "alz; hands";

    public SendStickerCommandHandler(ITelegramMessageStorage storage, TelegramBotClient bot)
    {
        _storage = storage;
        _bot = bot;
    }

    public override async Task HandleAsync(SendStickerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var userPrompt = $"{command.Message.Text?.Trim()?.ToLowerInvariant()}";
        if (string.IsNullOrWhiteSpace(userPrompt))
            return;

        var sticker = FromUserPrompt(userPrompt);
        if (sticker == null)
        {
            await _bot.SendMessage(
            command.Message.Chat,
            $"Стикер отсутствует. Доступные стикеры: {AvailableStickers}",
            replyParameters: new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);

            return;
        }

        var response = await _bot.SendPhoto(
                    command.Message.Chat,
                    sticker,
                    "",
                    ParseMode.MarkdownV2,
                    new()
                    {
                        MessageId = command.Message.MessageId
                    },
                    cancellationToken: cancellationToken);

        await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
    }

    private static InputFileStream? FromUserPrompt(string prompt)
    {
        if (prompt.Contains("alz", StringComparison.Ordinal))
        {
            return new InputFileStream(new MemoryStream(EmbeddedResources.AlzJpg), "alz.jpg");
        }

        if (prompt.Contains("hands", StringComparison.Ordinal))
        {
            return new InputFileStream(new MemoryStream(EmbeddedResources.HandsJpg), "hands.jpg");
        }

        return null;
    }
}
