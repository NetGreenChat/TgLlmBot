using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Llm;

namespace TgLlmBot.Commands.Rating;

/// <summary>
///     Участник чата, которого модель должна оценить, и его номер в списке для модели.
/// </summary>
/// <remarks>
///     Модель возвращает оценки по номерам, а не по id пользователей Telegram: десятизначный id
///     модель с небольшим числом параметров переписывает с ошибками, и оценка молча уходит
///     в никуда. Номер стоит и в списке участников, и в заголовке каждого сообщения участника,
///     поэтому сопоставлять модели ничего не нужно.
/// </remarks>
public sealed class RatingParticipant
{
    /// <summary>
    ///     Сколько сообщений в истории нужно, чтобы участника было за что оценивать.
    /// </summary>
    public const int MinMessagesCount = 5;

    private RatingParticipant(int number, long userId, string label, string reportName, int messagesCount)
    {
        Number = number;
        UserId = userId;
        Label = label;
        ReportName = reportName;
        MessagesCount = messagesCount;
    }

    /// <summary>
    ///     Номер в списке для модели, начиная с 1.
    /// </summary>
    public int Number { get; }

    public long UserId { get; }

    /// <summary>
    ///     Подпись участника в промпте: номер и имя - ровно так, как в заголовках его сообщений.
    /// </summary>
    public string Label { get; }

    /// <summary>
    ///     Как участник называется в отчёте: username без @, иначе имя и фамилия.
    /// </summary>
    public string ReportName { get; }

    public int MessagesCount { get; }

    /// <summary>
    ///     Отбирает участников с достаточным числом сообщений, нумерует их в порядке первого
    ///     появления в истории и переименовывает в справочнике подписей так, чтобы номер стоял
    ///     в заголовке каждого их сообщения.
    /// </summary>
    /// <param name="contextMessages">История чата.</param>
    /// <param name="selfUserId">Id бота: его сообщения не оцениваются.</param>
    /// <param name="authors">
    ///     Справочник подписей, по которому потом строится история: подписи отобранных участников
    ///     в нём заменяются на нумерованные.
    /// </param>
    public static RatingParticipant[] SelectAndLabel(DbChatMessage[] contextMessages, long selfUserId, ChatAuthorDirectory authors)
    {
        ArgumentNullException.ThrowIfNull(contextMessages);
        ArgumentNullException.ThrowIfNull(authors);
        var messagesCount = new Dictionary<long, int>();
        var lastMessages = new Dictionary<long, DbChatMessage>();
        var firstSeen = new List<long>();
        foreach (var message in contextMessages.OrderBy(static x => x.Date).ThenBy(static x => x.MessageId))
        {
            if (message.IsLlmReplyToMessage || message.FromUserId is not { } userId || userId == selfUserId)
            {
                continue;
            }

            if (messagesCount.TryGetValue(userId, out var seen))
            {
                messagesCount[userId] = seen + 1;
            }
            else
            {
                firstSeen.Add(userId);
                messagesCount[userId] = 1;
            }

            lastMessages[userId] = message;
        }

        var participants = new List<RatingParticipant>();
        foreach (var userId in firstSeen)
        {
            var count = messagesCount[userId];
            if (count < MinMessagesCount)
            {
                continue;
            }

            var lastMessage = lastMessages[userId];
            var number = participants.Count + 1;
            var baseLabel = authors.TryGet(userId, out var known) ? known : authors.Describe(lastMessage);
            var label = $"№{number.ToString(CultureInfo.InvariantCulture)} {baseLabel}";
            authors.Register(userId, label);
            participants.Add(new(number, userId, label, BuildReportName(lastMessage), count));
        }

        return participants.ToArray();
    }

    private static string BuildReportName(DbChatMessage message)
    {
        var username = message.FromUsername?.Trim();
        if (!string.IsNullOrEmpty(username))
        {
            return username;
        }

        var fullName = $"{message.FromFirstName?.Trim()} {message.FromLastName?.Trim()}".Trim();
        return fullName.Length > 0 ? fullName : "Anonymous";
    }
}
