using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading.Channels;

namespace TgLlmBot.Services.Telegram.TypingStatus;

public class DefaultTypingStatusQueues : ITypingStatusQueues
{
    private readonly FrozenDictionary<long, Channel<TypingCommand>> _queues;

    public DefaultTypingStatusQueues(DefaultTypingStatusQueuesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var queues = new Dictionary<long, Channel<TypingCommand>>(options.ChatIds.Count);
        var readers = new Dictionary<long, ChannelReader<TypingCommand>>(options.ChatIds.Count);
        foreach (var chatId in options.ChatIds)
        {
            // Без потолка намеренно: команды крошечные, а отброшенное "перестань печатать"
            // оставляет чат печатающим до перезапуска бота. Копиться тут нечему - разбор
            // команды не ходит в сеть, он только заводит или гасит таймер.
            var channel = Channel.CreateUnbounded<TypingCommand>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            queues.Add(chatId, channel);
            readers.Add(chatId, channel.Reader);
        }

        _queues = queues.ToFrozenDictionary();
        Readers = readers.ToFrozenDictionary();
    }

    public IReadOnlyDictionary<long, ChannelReader<TypingCommand>> Readers { get; }

    public bool TryEnqueue(long chatId, TypingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _queues.TryGetValue(chatId, out var channel) && channel.Writer.TryWrite(command);
    }

    public void Complete()
    {
        foreach (var channel in _queues.Values)
        {
            channel.Writer.TryComplete();
        }
    }
}
