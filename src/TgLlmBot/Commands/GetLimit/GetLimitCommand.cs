using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess.Limits;
using TgLlmBot.Services.DataAccess.Limits.Models;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.GetLimit;

public class GetLimitCommand : AbstractCommand
{
    public GetLimitCommand(Message message, UpdateType type, User self) : base(message, type)
    {
        Self = self;
    }
    public User Self { get; }
}

public class GetLimitCommandHandler : AbstractCommandHandler<GetLimitCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ILlmLimitsService _limitsService;
    private readonly ITelegramMarkdownConverter _markdownConverter;
    private readonly ITelegramMessageStorage _storage;

    public GetLimitCommandHandler(TelegramBotClient bot, ILlmLimitsService limitsService, ITelegramMarkdownConverter markdownConverter, ITelegramMessageStorage storage)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(limitsService);
        ArgumentNullException.ThrowIfNull(markdownConverter);
        _bot = bot;
        _limitsService = limitsService;
        _markdownConverter = markdownConverter;
        _storage = storage;
    }

    public override async Task HandleAsync(GetLimitCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Message.From is null)
        {
            return;
        }

        var chatUsage = await _limitsService.GetDailyLimitsAsync(
            command.Message.Chat.Id,
            command.Message.From.Id,
            cancellationToken);

        var response = BuildResponseTemplate(_markdownConverter, chatUsage);
        await _bot.SendMessage(
            command.Message.Chat,
            response,
            ParseMode.MarkdownV2,
            new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);
    }

    private static string BuildResponseTemplate(
        ITelegramMarkdownConverter markdownConverter,
        DailyChatUsageStats chatUsage)
    {
        if (chatUsage.IsUnlimited)
        {
            return "✅ Лимит сообщений отсутствует";
        }
        else
        {
            var builder = new StringBuilder();
            builder.Append("Дневной лимит сообщений: ").AppendLine(chatUsage.Limit.Value.ToString(CultureInfo.InvariantCulture));
            builder.Append("Использовано: ").AppendLine(chatUsage.Used.ToString(CultureInfo.InvariantCulture));
            builder.Append("Осталось: ").AppendLine(chatUsage.Remaining.Value.ToString(CultureInfo.InvariantCulture));

            var rawMarkdown = builder.ToString();
            var optimizedMarkdown = markdownConverter.ConvertToSolidTelegramMarkdown(rawMarkdown);
            return optimizedMarkdown;
        }

    }
}
