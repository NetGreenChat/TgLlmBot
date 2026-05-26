using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.DataAccess.TelegramMessages;

public interface ITelegramMessageStorage
{
    Task StoreMessageAsync(
        Message message,
        User self,
        CancellationToken cancellationToken,
        DbChatMessageKind? kind = null);

    Task<DbChatMessage[]> SelectContextMessagesAsync(
        Message message,
        CancellationToken cancellationToken);
}
