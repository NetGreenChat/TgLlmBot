using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess.Limits;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Resources;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.SetChatLimit;

public class SetChatLimitCommandHandler : AbstractCommandHandler<SetChatLimitCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ILlmLimitsService _limitsService;
    private readonly ITelegramMarkdownConverter _markdownConverter;
    private readonly ITelegramMessageStorage _storage;

    public SetChatLimitCommandHandler(
        TelegramBotClient bot,
        ITelegramMessageStorage storage,
        ILlmLimitsService limitsService,
        ITelegramMarkdownConverter markdownConverter)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(limitsService);
        ArgumentNullException.ThrowIfNull(markdownConverter);
        _bot = bot;
        _storage = storage;
        _limitsService = limitsService;
        _markdownConverter = markdownConverter;
    }

    public override async Task HandleAsync(SetChatLimitCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var isAdmin = await IsAdminMessageAsync(command, cancellationToken);
        if (isAdmin)
        {
            var commandText = $"{command.Message.Text?.Trim()}"
                .Replace("!set_chat_limit", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (int.TryParse(commandText, out var limit) && limit >= 0)
            {
                await _limitsService.SetChatDailyLimitAsync(command.Message.Chat.Id, limit, cancellationToken);
                await ReplyWithMarkdownAsync(
                    command,
                    $"✅ Для всех участников чата установлен лимит - {limit:D}",
                    cancellationToken);
            }
            else
            {
                await ReplyWithMarkdownAsync(command, $"⚠️ Не удалось распарсить лимиты.\nНужно указать целое число от 0 до {int.MaxValue}", cancellationToken);
            }
        }
        else
        {
            await HandleNonAdminAsync(command, cancellationToken);
        }
    }

    private async Task ReplyWithMarkdownAsync(SetChatLimitCommand command, string responseText, CancellationToken cancellationToken)
    {
        var telegramMarkdown = _markdownConverter.ConvertToSolidTelegramMarkdown(responseText);
        var response = await _bot.SendMessage(
            command.Message.Chat,
            telegramMarkdown,
            ParseMode.MarkdownV2,
            new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);
        await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
    }

    private async Task HandleNonAdminAsync(SetChatLimitCommand command, CancellationToken cancellationToken)
    {
        var telegramMarkdown = _markdownConverter.ConvertToSolidTelegramMarkdown("❌ Только администраторы могут менять лимиты");
        var response = await _bot.SendPhoto(
            command.Message.Chat,
            new InputFileStream(new MemoryStream(EmbeddedResources.NoJpg), "no.jpg"),
            telegramMarkdown,
            ParseMode.MarkdownV2,
            new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);
        await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
    }

    private async Task<bool> IsAdminMessageAsync(SetChatLimitCommand command, CancellationToken cancellationToken)
    {
        if (command.Message.Chat.Type is ChatType.Group or ChatType.Supergroup && command.Message.From is not null)
        {
            var admins = await _bot.GetChatAdministrators(command.Message.Chat, cancellationToken: cancellationToken);
            return admins.Any(x => x.User.Id == command.Message.From.Id);
        }

        return true;
    }
}
