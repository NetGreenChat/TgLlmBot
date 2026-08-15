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
using TgLlmBot.Utils;

namespace TgLlmBot.Services.DataAccess.TelegramMessages;

public class DefaultTelegramMessageStorage : ITelegramMessageStorage
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public DefaultTelegramMessageStorage(IServiceScopeFactory serviceScopeFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        _serviceScopeFactory = serviceScopeFactory;
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    public async Task StoreMessageAsync(Message message, User self, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var dbChatMessage = CreateDbChatMessage(message, self);
                dbContext.ChatHistory.Add(dbChatMessage);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    [SuppressMessage("Usage", "CA2241:Provide correct arguments to formatting methods")]
    public async Task<DbChatMessage[]> SelectContextMessagesAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var resultAccumulator = new List<DbChatMessage>();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var messageId = new NpgsqlParameter($"{nameof(DbChatMessage.MessageId)}", message.MessageId);
                var chatId = new NpgsqlParameter($"{nameof(DbChatMessage.ChatId)}", message.Chat.Id);
                var sql = FormattableStringFactory.Create(
                    $"""
                     WITH target_message AS (
                         SELECT "{nameof(DbChatMessage.Date)}" as cutoff_date
                         FROM public."{nameof(BotDbContext.ChatHistory)}"
                         WHERE "{nameof(DbChatMessage.MessageId)}" = @{nameof(DbChatMessage.MessageId)} AND "{nameof(DbChatMessage.ChatId)}" = @{nameof(DbChatMessage.ChatId)}
                     )
                     SELECT
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
                         "{nameof(DbChatMessage.IsLlmReplyToMessage)}"
                     FROM (
                              SELECT
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
                                  SUM(COALESCE(LENGTH("{nameof(DbChatMessage.Text)}"), 0) + COALESCE(LENGTH("{nameof(DbChatMessage.Caption)}"), 0)) OVER (
                                      ORDER BY "{nameof(DbChatMessage.Date)}" DESC
                                      ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                                      ) as cumulative_length
                              FROM public."{nameof(BotDbContext.ChatHistory)}" ch
                              WHERE "{nameof(DbChatMessage.ChatId)}" = @{nameof(DbChatMessage.ChatId)}
                                AND "{nameof(DbChatMessage.Date)}" <= (SELECT cutoff_date FROM target_message)
                                AND "{nameof(DbChatMessage.MessageId)}" != @{nameof(DbChatMessage.MessageId)}
                                AND NOT EXISTS (
                                     SELECT 1
                                     FROM public."{nameof(BotDbContext.KickedUsers)}" k
                                     WHERE k."{nameof(DbKickedUser.ChatId)}" = ch."{nameof(DbChatMessage.ChatId)}"
                                       AND k."{nameof(DbKickedUser.UserId)}" = ch."{nameof(DbChatMessage.FromUserId)}"
                                )
                              ORDER BY "{nameof(DbChatMessage.Date)}" DESC
                              LIMIT 200
                          ) as subquery
                     WHERE cumulative_length <= 30000
                     ORDER BY "{nameof(DbChatMessage.Date)}" DESC;
                     """,
                    messageId,
                    chatId);
                var dbResults = await dbContext.ChatHistory.FromSql(sql).AsNoTracking().ToListAsync(cancellationToken);
                resultAccumulator.AddRange(dbResults.OrderBy(x => x.Date));
                await transaction.CommitAsync(cancellationToken);
            }
        }

        return resultAccumulator.ToArray();
    }

    private static DbChatMessage CreateDbChatMessage(Message message, User self)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(self);
        var isSelfMessage = self.Id == message.From?.Id;
        return new(
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
            isSelfMessage);
    }
}
