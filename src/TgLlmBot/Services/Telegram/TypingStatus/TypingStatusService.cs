using System;
using System.Threading.Channels;

namespace TgLlmBot.Services.Telegram.TypingStatus;

public class TypingStatusService : ITypingStatusService
{
    private readonly ChannelWriter<TypingCommand> _typingCommandWriter;

    public TypingStatusService(ChannelWriter<TypingCommand> typingCommandWriter)
    {
        ArgumentNullException.ThrowIfNull(typingCommandWriter);
        _typingCommandWriter = typingCommandWriter;
    }

    public void StartTyping(long chatId)
    {
        _typingCommandWriter.TryWrite(new(chatId, true));
    }

    public void StopTyping(long chatId)
    {
        // Канал без потолка, поэтому запись не отвергается: потерянная команда "перестань печатать"
        // оставила бы чат печатающим до перезапуска бота
        _typingCommandWriter.TryWrite(new(chatId, false));
    }
}
