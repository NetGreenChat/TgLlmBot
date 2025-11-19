using System.Collections.Generic;
using System.Threading.Tasks;

namespace TgLlmBot.Services.DataAccess;

public interface ITelegramUsageByUserCountStorage
{
    public Task IncreaseUsageCountByOne(long chatId, long fromUserId, string? fromUsername, string? fromFirstName, string? fromLastName, decimal costInUsd);
    public Task<ICollection<IUserUsageFullSummaryForChat>> SelectTopByUsageCountFullSummaryForUsersInChat(long chatId, int take);
}

public interface IUserUsageFullSummaryForChat
{
    long UserId { get; }
    string? Username { get; }
    string? FirstName { get; }
    string? LastName { get; }
    long UsageCountInAllChats { get; }
    decimal CostInUsdInAllChats { get; }
}
