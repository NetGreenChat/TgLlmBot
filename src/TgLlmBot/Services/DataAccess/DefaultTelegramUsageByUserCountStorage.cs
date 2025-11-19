using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TgLlmBot.DataAccess;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.DataAccess;

public record UserUsageFullSummaryForChat(long UserId, string? Username, string? FirstName, string? LastName, long UsageCountInAllChats, decimal CostInUsdInAllChats) : IUserUsageFullSummaryForChat;

public class DefaultTelegramUsageByUserCountStorage : ITelegramUsageByUserCountStorage
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public DefaultTelegramUsageByUserCountStorage(IServiceScopeFactory serviceScopeFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task IncreaseUsageCountByOne(long chatId, long fromUserId, string? fromUsername, string? fromFirstName, string? fromLastName, decimal costInUsd)
    {
        await using var asyncScope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();

        var updatedCount = await dbContext.UsageByUserCounts
            .Where(x => x.FromUserId == fromUserId && x.ChatId == chatId)
            .ExecuteUpdateAsync(spc => spc
                .SetProperty(x => x.Count, x => x.Count + 1)
                .SetProperty(x => x.FromUsername, x => fromUsername ?? x.FromUsername)
                .SetProperty(x => x.FromFirstName, x => fromFirstName ?? x.FromFirstName)
                .SetProperty(x => x.FromLastName, x => fromLastName ?? x.FromLastName)
                .SetProperty(x => x.CostInUsd, x => x.CostInUsd + costInUsd)
            );
        if (updatedCount == 0)
        {
            dbContext.UsageByUserCounts.Add(new DbUsageByUserCount()
            {
                ChatId = chatId,
                FromUserId = fromUserId,
                FromUsername = fromUsername,
                FromFirstName = fromFirstName,
                FromLastName = fromLastName,
                Count = 1,
                CostInUsd = costInUsd
            });
            await dbContext.SaveChangesAsync();
        }
    }

    public async Task<ICollection<IUserUsageFullSummaryForChat>> SelectTopByUsageCountFullSummaryForUsersInChat(long chatId, int take)
    {
        await using var asyncScope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();

        var result = await dbContext.UsageByUserCounts
            .Where(x => x.ChatId == chatId)
            .Select(x => new
                {
                    UserId = x.FromUserId,
                    Username = x.FromUsername,
                    FirstName = x.FromFirstName,
                    LastName = x.FromLastName,
                    UsageCountInAllChats = dbContext.UsageByUserCounts
                        .Where(x1 => x1.FromUserId == x.FromUserId)
                        .Select(x1 => x1.Count).Sum(),
                    CostInUsdInAllChats = dbContext.UsageByUserCounts
                        .Where(x1 => x1.FromUserId == x.FromUserId)
                        .Select(x1 => x1.CostInUsd).Sum()
                }
            )
            .OrderBy(x => x.UsageCountInAllChats)
            .Take(take)
            .Select(x => new UserUsageFullSummaryForChat(x.UserId, x.Username, x.FirstName, x.LastName, x.UsageCountInAllChats, x.CostInUsdInAllChats))
            .ToArrayAsync();

        return result;
    }
}
