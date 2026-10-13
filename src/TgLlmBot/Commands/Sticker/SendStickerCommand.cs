using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;

namespace TgLlmBot.Commands.Sticker;

public class SendStickerCommand : AbstractCommand
{
    public SendStickerCommand(Message message, UpdateType type, User self) : base(message, type)
    {
        Self = self;
    }

    public User Self { get; }
}
