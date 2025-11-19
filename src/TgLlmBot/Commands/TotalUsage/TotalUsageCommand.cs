using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;

namespace TgLlmBot.Commands.TotalUsage;

public class TotalUsageCommand : AbstractCommand
{
    public TotalUsageCommand(Message message, UpdateType type) : base(message, type)
    {
    }
}
