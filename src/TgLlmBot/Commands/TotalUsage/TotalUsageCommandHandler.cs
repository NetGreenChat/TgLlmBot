using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess;

namespace TgLlmBot.Commands.TotalUsage;

public class TotalUsageCommandHandler : AbstractCommandHandler<TotalUsageCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly TimeProvider _timeProvider;
    private readonly ITelegramUsageByUserCountStorage _usageByUserCountStorage;

    private ICollection<IUserUsageFullSummaryForChat> _keyStats;
    private DateTimeOffset _lastUpdateAt;

    public TotalUsageCommandHandler(
        TelegramBotClient bot,
        TimeProvider timeProvider,
        ITelegramUsageByUserCountStorage usageByUserCountStorage)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _bot = bot;
        _timeProvider = timeProvider;
        _usageByUserCountStorage = usageByUserCountStorage;

        _lastUpdateAt = DateTimeOffset.MinValue;
        _keyStats = [];
    }

    public override async Task HandleAsync(TotalUsageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var currentDate = _timeProvider.GetUtcNow();
        var threshold = _lastUpdateAt.AddSeconds(10);
        if (currentDate > threshold)
        {
            _keyStats = await _usageByUserCountStorage.SelectTopByUsageCountFullSummaryForUsersInChat(command.Message.Chat.Id, 5);
            _lastUpdateAt = _timeProvider.GetUtcNow();
        }

        var response = BuildTotalUsageReport(_keyStats);
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

    private static string BuildTotalUsageReport(ICollection<IUserUsageFullSummaryForChat> usersUsageFullSummaryForChat)
    {
        if (usersUsageFullSummaryForChat.Count == 0)
        {
            return "Нет использований бота для анализа 🤷";
        }

        var builder = new StringBuilder();
        builder.AppendLine("🎭 **Рейтинг использования**");
        builder.AppendLine();

        var top5 = usersUsageFullSummaryForChat.Take(5).ToList(); // там конечно уже оно из бд только топ 5 получено
        for (var i = 0; i < top5.Count; i++)
        {
            var user = top5[i];
            var rank = i + 1;
            var medal = rank switch
            {
                1 => "🥇",
                2 => "🥈",
                3 => "🥉",
                _ => "  "
            };

            var name = user.Username;
            if (string.IsNullOrWhiteSpace(name))
            {
                var combinedName = $"{user.FirstName?.Trim()} {user.LastName?.Trim()}".Trim();
                name = !string.IsNullOrWhiteSpace(combinedName)
                    ? combinedName
                    : "Anonymous";
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"{medal} #{rank}: `{name}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Сообщений: {user.UsageCountInAllChats}, Долларов потрачено: {user.CostInUsdInAllChats:F3}");
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
