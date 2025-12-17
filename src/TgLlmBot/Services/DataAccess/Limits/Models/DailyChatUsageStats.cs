using System.Diagnostics.CodeAnalysis;

namespace TgLlmBot.Services.DataAccess.Limits.Models;

public class DailyChatUsageStats
{
    public DailyChatUsageStats(int used, int? limit, int? remaining)
    {
        Used = used;
        Limit = limit;
        Remaining = remaining;
    }

    public int Used { get; }
    public int? Remaining { get; }
    public int? Limit { get; }

    [MemberNotNullWhen(false, nameof(Limit), nameof(Remaining))]
    public bool IsUnlimited => !Limit.HasValue;
}
