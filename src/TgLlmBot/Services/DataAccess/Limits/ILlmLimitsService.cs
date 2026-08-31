using System.Threading;
using System.Threading.Tasks;

namespace TgLlmBot.Services.DataAccess.Limits;

public interface ILlmLimitsService
{
    Task IncrementUsageAsync(long chatId, long userId, CancellationToken cancellationToken);

    Task<bool> IsLLmInteractionAllowedAsync(long chatId, long userId, CancellationToken cancellationToken);

    Task SetDailyLimitsAsync(long chatId, long userId, int limit, CancellationToken cancellationToken);

    Task SetChatDailyLimitAsync(long chatId, int limit, CancellationToken cancellationToken);
}
