using System.Threading;
using System.Threading.Tasks;
using TgLlmBot.Models;
using TgLlmBot.Services.DataAccess.Limits.Models;

namespace TgLlmBot.Services.DataAccess.Limits;

public interface ILlmLimitsService
{
    Task IncrementUsageAsync(long chatId, long userId, CancellationToken cancellationToken);

    Task<bool> IsLLmInteractionAllowedAsync(long chatId, long userId, CancellationToken cancellationToken);

    Task SetDailyLimitsAsync(long chatId, long userId, int limit, CancellationToken cancellationToken);

    Task<DailyChatUsageStats> GetDailyLimitsAsync(long chatId, long userId, CancellationToken cancellationToken);
}
