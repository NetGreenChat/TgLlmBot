using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TgLlmBot.DataAccess;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.DataAccess.Limits;

[SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
public class DefaultLlmLimitsService : ILlmLimitsService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public DefaultLlmLimitsService(IServiceScopeFactory serviceScopeFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task IncrementUsageAsync(long chatId, long userId, CancellationToken cancellationToken)
    {
        const string sql = $"""
                            INSERT INTO "{nameof(BotDbContext.Usage)}" AS u ("{nameof(DbChatUsage.ChatId)}", "{nameof(DbChatUsage.UserId)}", "{nameof(DbChatUsage.Date)}", "{nameof(DbChatUsage.Used)}")
                            VALUES (@{nameof(DbChatUsage.ChatId)}, @{nameof(DbChatUsage.UserId)}, @{nameof(DbChatUsage.Date)}, 1)
                            ON CONFLICT ("{nameof(DbChatUsage.ChatId)}", "{nameof(DbChatUsage.UserId)}", "{nameof(DbChatUsage.Date)}") DO UPDATE SET "{nameof(DbChatUsage.Used)}" = u."{nameof(DbChatUsage.Used)}" + 1;
                            """;
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            var date = _timeProvider.GetUtcNow().Date.ToUniversalTime();
            await dbContext.Database.ExecuteSqlRawAsync(
                sql,
                new NpgsqlParameter($"{nameof(DbChatUsage.ChatId)}", chatId),
                new NpgsqlParameter($"{nameof(DbChatUsage.UserId)}", userId),
                new NpgsqlParameter($"{nameof(DbChatUsage.Date)}", date));
        }
    }

    public async Task<bool> IsLLmInteractionAllowedAsync(long chatId, long userId, CancellationToken cancellationToken)
    {
        await using var asyncScope = _serviceScopeFactory.CreateAsyncScope();

        var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
        var date = _timeProvider.GetUtcNow().Date.ToUniversalTime();
        var dbPersonalLimit = await dbContext.Limits.AsNoTracking()
            .Where(x => x.UserId == userId && x.ChatId == chatId)
            .FirstOrDefaultAsync(cancellationToken);

        // Персональный лимит перебивает общечатовый: !set_limit ставят точечно и уже зная,
        // что в чате есть !set_chat_limit, поэтому именно он должен решать судьбу конкретного человека
        int? limit = dbPersonalLimit?.Limit;
        if (limit is null)
        {
            var dbChatLimit = await dbContext.ChatLimits.AsNoTracking()
                .Where(x => x.ChatId == chatId)
                .FirstOrDefaultAsync(cancellationToken);
            limit = dbChatLimit?.Limit;
        }

        if (limit is not { } effectiveLimit)
        {
            return true;
        }

        if (effectiveLimit == 0)
        {
            return false;
        }

        var dbDailyUsage = await dbContext.Usage.AsNoTracking()
            .Where(x => x.UserId == userId && x.Date == date && x.ChatId == chatId)
            .FirstOrDefaultAsync(cancellationToken);

        return (dbDailyUsage?.Used ?? 0) < effectiveLimit;
    }

    public async Task SetDailyLimitsAsync(long chatId, long userId, int limit, CancellationToken cancellationToken)
    {
        const string sql = $"""
                                INSERT INTO "{nameof(BotDbContext.Limits)}" ("{nameof(DbUserLimit.ChatId)}", "{nameof(DbUserLimit.UserId)}", "{nameof(DbUserLimit.Limit)}")
                                VALUES (@{nameof(DbUserLimit.ChatId)}, @{nameof(DbUserLimit.UserId)}, @{nameof(DbUserLimit.Limit)})
                                ON CONFLICT ("{nameof(DbUserLimit.ChatId)}", "{nameof(DbUserLimit.UserId)}") DO UPDATE SET "{nameof(DbUserLimit.Limit)}" = @{nameof(DbUserLimit.Limit)};
                            """;
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(
                sql,
                new NpgsqlParameter($"{nameof(DbUserLimit.ChatId)}", chatId),
                new NpgsqlParameter($"{nameof(DbUserLimit.UserId)}", userId),
                new NpgsqlParameter($"{nameof(DbUserLimit.Limit)}", limit));
        }
    }

    public async Task SetChatDailyLimitAsync(long chatId, int limit, CancellationToken cancellationToken)
    {
        const string sql = $"""
                                INSERT INTO "{nameof(BotDbContext.ChatLimits)}" ("{nameof(DbChatLimit.ChatId)}", "{nameof(DbChatLimit.Limit)}")
                                VALUES (@{nameof(DbChatLimit.ChatId)}, @{nameof(DbChatLimit.Limit)})
                                ON CONFLICT ("{nameof(DbChatLimit.ChatId)}") DO UPDATE SET "{nameof(DbChatLimit.Limit)}" = @{nameof(DbChatLimit.Limit)};
                            """;
        cancellationToken.ThrowIfCancellationRequested();
        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(
                sql,
                new NpgsqlParameter($"{nameof(DbChatLimit.ChatId)}", chatId),
                new NpgsqlParameter($"{nameof(DbChatLimit.Limit)}", limit));
        }
    }
}
