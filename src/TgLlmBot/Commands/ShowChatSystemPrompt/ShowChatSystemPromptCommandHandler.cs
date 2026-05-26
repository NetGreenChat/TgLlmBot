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

namespace TgLlmBot.Commands.ShowChatSystemPrompt;

public class ShowChatSystemPromptCommandHandler : AbstractCommandHandler<ShowChatSystemPromptCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ITelegramMarkdownConverter _markdownConverter;
    private readonly ITelegramMessageStorage _storage;
    private readonly ISystemPromptService _systemPrompt;

    public ShowChatSystemPromptCommandHandler(
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

    public override async Task HandleAsync(ShowChatSystemPromptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var chatPrompt = await _systemPrompt.GetChatPromptAsync(command.Message.Chat.Id, cancellationToken);
        if (chatPrompt.IsFailed)
        {
            var response = await _bot.SendMessage(
                command.Message.Chat,
                "🤷 Чат не использует кастомный системный промпт",
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
            //"✅ Системный промпт чата успешно изменён"
            var okStatusMessage = await _bot.SendMessage(
                command.Message.Chat,
                "✅ Чат использует кастомный системный промпт",
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
