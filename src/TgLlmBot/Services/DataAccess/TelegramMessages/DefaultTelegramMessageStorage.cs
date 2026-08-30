using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Telegram.Bot.Types;
using TgLlmBot.DataAccess;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Llm;
using TgLlmBot.Services.Media;
using TgLlmBot.Utils;

namespace TgLlmBot.Services.DataAccess.TelegramMessages;

public class DefaultTelegramMessageStorage : ITelegramMessageStorage
{
    /// <summary>
    ///     Суммарный бюджет истории в символах. Описания вложений уезжают в контекст вместе
    ///     с сообщениями и точно так же его съедают, поэтому считаются наравне с текстом.
    /// </summary>
    private const int ContextLengthBudget = 30000;

    private const int ContextMessagesLimit = 200;

    /// <summary>
    ///     Сколько недообработанных сообщений забирать за один проход подчистки.
    /// </summary>
    private const int UnfinishedMediaRecoveryLimit = 500;

    /// <summary>
    ///     Список колонок истории чата - один и тот же для всех запросов, читающих сообщения целиком.
    ///     EF требует, чтобы в выборке присутствовали все замапленные колонки сущности.
    /// </summary>
    private const string ChatMessageColumns = $"""
                                               "{nameof(DbChatMessage.Id)}",
                                                   "{nameof(DbChatMessage.MessageId)}",
                                                   "{nameof(DbChatMessage.ChatId)}",
                                                   "{nameof(DbChatMessage.MessageThreadId)}",
                                                   "{nameof(DbChatMessage.ReplyToMessageId)}",
                                                   "{nameof(DbChatMessage.Date)}",
                                                   "{nameof(DbChatMessage.FromUserId)}",
                                                   "{nameof(DbChatMessage.FromUsername)}",
                                                   "{nameof(DbChatMessage.FromFirstName)}",
                                                   "{nameof(DbChatMessage.FromLastName)}",
                                                   "{nameof(DbChatMessage.Text)}",
                                                   "{nameof(DbChatMessage.Caption)}",
                                                   "{nameof(DbChatMessage.IsLlmReplyToMessage)}",
                                                   "{nameof(DbChatMessage.MediaGroupId)}",
                                                   "{nameof(DbChatMessage.CustomPromptScope)}",
                                                   "{nameof(DbChatMessage.CustomPromptUserId)}"
                                               """;

    private readonly IServiceScopeFactory _serviceScopeFactory;

    public DefaultTelegramMessageStorage(IServiceScopeFactory serviceScopeFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        _serviceScopeFactory = serviceScopeFactory;
    }

    public Task<DbChatMessage> StoreMessageAsync(Message message, User self, CancellationToken cancellationToken)
    {
        return StoreMessageAsync(message, self, AppliedCustomPrompt.None, cancellationToken);
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    public async Task<DbChatMessage> StoreMessageAsync(
        Message message,
        User self,
        AppliedCustomPrompt customPrompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var dbChatMessage = CreateDbChatMessage(message, self, customPrompt);
                // Вложения уезжают в базу вместе с сообщением: внешний ключ EF проставит сам
                dbContext.ChatHistory.Add(dbChatMessage);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return dbChatMessage;
            }
        }
    }

    public Task<DbChatMessage[]> SelectContextMessagesAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        return SelectContextMessagesCoreAsync(message.Chat.Id, message.MessageId, message.Date, cancellationToken);
    }

    public Task<DbChatMessage[]> SelectContextMessagesBeforeAsync(
        long chatId,
        int messageId,
        DateTime date,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SelectContextMessagesCoreAsync(chatId, messageId, date, cancellationToken);
    }

    /// <summary>
    ///     Единая выборка истории по общему правилу (200 сообщений или 30 000 символов бюджета):
    ///     все сообщения чата с датой не позже указанной, кроме самого целевого, в порядке убывания даты.
    /// </summary>
    /// <remarks>
    ///     Бюджет длины считается по тексту, подписи и сжатым описаниям вложений. Описание вложения
    ///     уезжает в контекст вместе с сообщением, поэтому считается наравне с текстом. Длину
    ///     описания приводим к нулю до LEAST, а не после: LEAST в Postgres игнорирует NULL, и ещё
    ///     не описанное вложение съедало бы полный лимит вместо ничего.
    /// </remarks>
    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    [SuppressMessage("Usage", "CA2241:Provide correct arguments to formatting methods")]
    private async Task<DbChatMessage[]> SelectContextMessagesCoreAsync(
        long chatId,
        int messageId,
        DateTime cutoffDate,
        CancellationToken cancellationToken)
    {
        var resultAccumulator = new List<DbChatMessage>();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var messageIdParam = new NpgsqlParameter($"{nameof(DbChatMessage.MessageId)}", messageId);
                var chatIdParam = new NpgsqlParameter($"{nameof(DbChatMessage.ChatId)}", chatId);
                var cutoffDateParam = new NpgsqlParameter("cutoff_date", cutoffDate);
                var sql = FormattableStringFactory.Create(
                    $"""
                     SELECT
                         {ChatMessageColumns}
                     FROM (
                              SELECT
                                  {ChatMessageColumns},
                                  SUM(COALESCE(LENGTH("{nameof(DbChatMessage.Text)}"), 0)
                                      + COALESCE(LENGTH("{nameof(DbChatMessage.Caption)}"), 0)
                                      + COALESCE((
                                          SELECT SUM(LEAST(
                                              COALESCE(LENGTH(m."{nameof(DbChatMessageMedia.ShortDescription)}"), 0),
                                              {MediaDescriptionLimits.ShortMaxLength}))
                                          FROM public."{nameof(BotDbContext.ChatMessageMedia)}" m
                                          WHERE m."{nameof(DbChatMessageMedia.ChatMessageId)}" = ch."{nameof(DbChatMessage.Id)}"
                                      ), 0)) OVER (
                                      ORDER BY "{nameof(DbChatMessage.Date)}" DESC
                                      ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                                      ) as cumulative_length,
                                  ROW_NUMBER() OVER (ORDER BY "{nameof(DbChatMessage.Date)}" DESC) as message_number
                              FROM public."{nameof(BotDbContext.ChatHistory)}" ch
                              WHERE "{nameof(DbChatMessage.ChatId)}" = @{nameof(DbChatMessage.ChatId)}
                                AND "{nameof(DbChatMessage.Date)}" <= @cutoff_date
                                AND "{nameof(DbChatMessage.MessageId)}" != @{nameof(DbChatMessage.MessageId)}
                                AND NOT EXISTS (
                                     SELECT 1
                                     FROM public."{nameof(BotDbContext.KickedUsers)}" k
                                     WHERE k."{nameof(DbKickedUser.ChatId)}" = ch."{nameof(DbChatMessage.ChatId)}"
                                       AND k."{nameof(DbKickedUser.UserId)}" = ch."{nameof(DbChatMessage.FromUserId)}"
                                )
                              ORDER BY "{nameof(DbChatMessage.Date)}" DESC
                              LIMIT {ContextMessagesLimit}
                          ) as subquery
                     WHERE cumulative_length <= {ContextLengthBudget} OR message_number = 1
                     ORDER BY "{nameof(DbChatMessage.Date)}" DESC;
                     """,
                    chatIdParam,
                    cutoffDateParam,
                    messageIdParam);
                var dbResults = await dbContext.ChatHistory.FromSql(sql).AsNoTracking().ToListAsync(cancellationToken);
                await LoadMediaAsync(dbContext, dbResults, cancellationToken);
                resultAccumulator.AddRange(dbResults.OrderBy(x => x.Date));
                await transaction.CommitAsync(cancellationToken);
            }
        }

        return resultAccumulator.ToArray();
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    public async Task<DbChatMessage?> SelectMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            return await dbContext.ChatHistory
                .AsNoTracking()
                .Include(x => x.Media.OrderBy(m => m.Order))
                .Where(x => x.ChatId == chatId && x.MessageId == messageId)
                .OrderByDescending(x => x.Date)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    public async Task<DbChatMessage[]> SelectMediaGroupMessagesAsync(long chatId, string mediaGroupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(mediaGroupId))
        {
            return [];
        }

        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            var parts = await dbContext.ChatHistory
                .AsNoTracking()
                .Include(x => x.Media.OrderBy(m => m.Order))
                .Where(x => x.ChatId == chatId && x.MediaGroupId == mediaGroupId)
                .OrderBy(x => x.MessageId)
                .ToListAsync(cancellationToken);
            return parts.ToArray();
        }
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    public async Task<DbChatMessage[]> SelectMessagesWithUnfinishedMediaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            var unfinished = await dbContext.ChatHistory
                .AsNoTracking()
                .Include(x => x.Media.OrderBy(m => m.Order))
                .Where(x => x.Media.Any(m => m.Status == DbMediaRecognitionStatus.Pending))
                .OrderByDescending(x => x.Date)
                .Take(UnfinishedMediaRecoveryLimit)
                .ToListAsync(cancellationToken);
            return unfinished.OrderBy(x => x.Date).ToArray();
        }
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    public async Task UpdateMediaAsync(DbChatMessageMedia[] media, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(media);
        if (media.Length is 0)
        {
            return;
        }

        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            foreach (var item in media)
            {
                var mediaId = item.Id;
                var status = item.Status;
                var shortDescription = item.ShortDescription;
                // Точечное обновление вместо загрузки сущности: остальные поля вложения
                // с момента вставки не менялись и перезаписывать их незачем
                await dbContext.ChatMessageMedia
                    .Where(x => x.Id == mediaId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(x => x.ShortDescription, shortDescription)
                            .SetProperty(x => x.Status, status),
                        cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Догружает вложения к сообщениям, выбранным сырым SQL.
    /// </summary>
    /// <remarks>
    ///     Отдельным запросом, а не через Include: сырой запрос истории с оконной функцией
    ///     и бюджетом по длине EF в подзапрос не завернёт.
    /// </remarks>
    private static async Task LoadMediaAsync(
        BotDbContext dbContext,
        List<DbChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count is 0)
        {
            return;
        }

        var messageIds = messages.Select(x => x.Id).ToArray();
        var media = await dbContext.ChatMessageMedia
            .AsNoTracking()
            .Where(x => messageIds.Contains(x.ChatMessageId))
            .OrderBy(x => x.Order)
            .ToListAsync(cancellationToken);
        if (media.Count is 0)
        {
            return;
        }

        var mediaByMessage = media.ToLookup(x => x.ChatMessageId);
        foreach (var message in messages)
        {
            foreach (var item in mediaByMessage[message.Id])
            {
                message.Media.Add(item);
            }
        }
    }

    private static DbChatMessage CreateDbChatMessage(Message message, User self, AppliedCustomPrompt customPrompt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(customPrompt);
        var isSelfMessage = self.Id == message.From?.Id;
        var dbChatMessage = new DbChatMessage(
            message.Id,
            message.Chat.Id,
            message.MessageThreadId,
            message.ReplyToMessage?.Id,
            message.Date,
            message.From?.Id,
            SurrogatePairSanitizer.SanitizeInvalidUtf16(message.From?.Username),
            SurrogatePairSanitizer.SanitizeInvalidUtf16(message.From?.FirstName),
            SurrogatePairSanitizer.SanitizeInvalidUtf16(message.From?.LastName),
            SurrogatePairSanitizer.SanitizeInvalidUtf16(message.Text),
            SurrogatePairSanitizer.SanitizeInvalidUtf16(message.Caption),
            isSelfMessage,
            message.MediaGroupId,
            customPrompt.Scope,
            customPrompt.UserId);
        foreach (var media in TelegramMessageMediaExtractor.Extract(message))
        {
            dbChatMessage.Media.Add(media);
        }

        return dbChatMessage;
    }
}
