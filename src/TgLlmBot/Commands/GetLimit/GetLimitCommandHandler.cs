using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess.Limits;
using TgLlmBot.Services.Resources;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.GetLimit;

public class GetLimitCommandHandler : AbstractCommandHandler<GetLimitCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ILlmLimitsService _limitsService;
    private readonly ITelegramMarkdownConverter _markdownConverter;

    public GetLimitCommandHandler(
        TelegramBotClient bot,
        ILlmLimitsService limitsService,
        ITelegramMarkdownConverter markdownConverter)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(limitsService);
        ArgumentNullException.ThrowIfNull(markdownConverter);
        _bot = bot;
        _limitsService = limitsService;
        _markdownConverter = markdownConverter;
    }

    public override async Task HandleAsync(GetLimitCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var isAdmin = await IsAdminMessageAsync(command, cancellationToken);
        if (isAdmin)
        {
            if (command.Message.ReplyToMessage?.From is not null)
            {
                var chatUsage = await _limitsService.GetDailyLimitsAsync(
                    command.Message.Chat.Id,
                    command.Message.ReplyToMessage.From.Id,
                    cancellationToken);

                var builder = new StringBuilder();
                builder.Append("У пользователя ");
                if (!string.IsNullOrEmpty(command.Message.ReplyToMessage?.From.Username))
                {
                    builder.Append('@').Append(command.Message.ReplyToMessage?.From.Username).AppendLine(" ");
                }

                if (chatUsage.IsUnlimited)
                {
                    builder.AppendLine("нет ограничений на количество сообщений");
                }
                else
                {
                    builder.Append("установлен лимит на сообщения - ").AppendLine(chatUsage.Limit.Value.ToString(CultureInfo.InvariantCulture));
                    builder.Append("Использовано: ").AppendLine(chatUsage.Used.ToString(CultureInfo.InvariantCulture));
                    builder.Append("Осталось: ").AppendLine(chatUsage.Remaining.Value.ToString(CultureInfo.InvariantCulture));
                }

                var replyText = builder.ToString();
                await ReplyWithMarkdownAsync(command, replyText, cancellationToken);
            }
            else
            {
                await ReplyWithMarkdownAsync(command, "⚠️ Просмотр лимита сообщений доступен только через реплай на сообщение того человека, лимит которого необходимо узнать", cancellationToken);
            }
        }
        else
        {
            await HandleNonAdminAsync(command, cancellationToken);
        }
    }

    private async Task ReplyWithMarkdownAsync(GetLimitCommand command, string responseText, CancellationToken cancellationToken)
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
    }

    private async Task HandleNonAdminAsync(GetLimitCommand command, CancellationToken cancellationToken)
    {
        var telegramMarkdown = _markdownConverter.ConvertToSolidTelegramMarkdown("❌ Только администраторы могут смотреть лимиты других участников");
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
    }

    private async Task<bool> IsAdminMessageAsync(GetLimitCommand command, CancellationToken cancellationToken)
    {
        if (command.Message.Chat.Type is ChatType.Group or ChatType.Supergroup && command.Message.From is not null)
        {
            var admins = await _bot.GetChatAdministrators(command.Message.Chat, cancellationToken);
            return admins.Any(x => x.User.Id == command.Message.From.Id);
        }

        return true;
    }
}
