using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TgLlmBot.Commands.ChatWithLlm.Queues;

public partial class DefaultLlmRequestQueues : ILlmRequestQueues
{
    private readonly FrozenDictionary<long, Channel<ChatWithLlmCommand>> _queues;

    public DefaultLlmRequestQueues(
        DefaultLlmRequestQueuesOptions options,
        ILogger<DefaultLlmRequestQueues> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var queues = new Dictionary<long, Channel<ChatWithLlmCommand>>(options.ChatIds.Count);
        var readers = new Dictionary<long, ChannelReader<ChatWithLlmCommand>>(options.ChatIds.Count);
        foreach (var chatId in options.ChatIds)
        {
            var channelOptions = new BoundedChannelOptions(options.CapacityPerChat)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };
            var currentChatId = chatId;
            var channel = Channel.CreateBounded<ChatWithLlmCommand>(
                channelOptions,
                dropped => Log.RequestDropped(logger, currentChatId, dropped.Message.MessageId, options.CapacityPerChat));
            queues.Add(chatId, channel);
            readers.Add(chatId, channel.Reader);
        }

        _queues = queues.ToFrozenDictionary();
        Readers = readers.ToFrozenDictionary();
    }

    public IReadOnlyDictionary<long, ChannelReader<ChatWithLlmCommand>> Readers { get; }

    public bool TryEnqueue(long chatId, ChatWithLlmCommand command)
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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "LLM request queue of chat {ChatId} is full ({Capacity}), message {MessageId} dropped")]
        public static partial void RequestDropped(ILogger logger, long chatId, int messageId, int capacity);
    }
}
