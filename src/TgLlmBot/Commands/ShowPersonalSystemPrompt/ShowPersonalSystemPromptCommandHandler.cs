using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.DataAccess.SystemPrompts;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.ShowPersonalSystemPrompt;

public class ShowPersonalSystemPromptCommandHandler : AbstractCommandHandler<ShowPersonalSystemPromptCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ITelegramMarkdownConverter _markdownConverter;
    private readonly ITelegramMessageStorage _storage;
    private readonly ISystemPromptService _systemPrompt;

    public ShowPersonalSystemPromptCommandHandler(
        TelegramBotClient bot,
        ITelegramMessageStorage storage,
        ISystemPromptService systemPrompt,
        ITelegramMarkdownConverter markdownConverter)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(markdownConverter);
        _bot = bot;
        _storage = storage;
        _systemPrompt = systemPrompt;
        _markdownConverter = markdownConverter;
    }

    public override async Task HandleAsync(ShowPersonalSystemPromptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Message.From is null)
        {
            return;
        }

        var chatPrompt = await _systemPrompt.GetUserChatPromptAsync(
            command.Message.Chat.Id,
            command.Message.From.Id,
            cancellationToken);
        if (chatPrompt.IsFailed)
        {
            var response = await _bot.SendMessage(
                command.Message.Chat,
                "🤷 Персональный системный промпт не задан",
                ParseMode.MarkdownV2,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken);
            await _storage.StoreMessageAsync(response, command.Self, cancellationToken, DbChatMessageKind.ServiceResponse);
        }
        else
        {
            var okStatusMessage = await _bot.SendMessage(
                command.Message.Chat,
                "✅ Используется персональный системный промпт",
                ParseMode.MarkdownV2,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken);
            var customPrompt = _markdownConverter.ConvertToSolidTelegramMarkdown(chatPrompt.Value);
            var promptMessage = await _bot.SendMessage(
                command.Message.Chat,
                customPrompt,
                ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
            await _storage.StoreMessageAsync(okStatusMessage, command.Self, cancellationToken, DbChatMessageKind.ServiceResponse);
            await _storage.StoreMessageAsync(promptMessage, command.Self, cancellationToken, DbChatMessageKind.ServiceResponse);
        }
    }
}
