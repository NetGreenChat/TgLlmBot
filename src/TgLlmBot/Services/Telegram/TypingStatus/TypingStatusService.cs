using System;
using Microsoft.Extensions.Logging;

namespace TgLlmBot.Services.Telegram.TypingStatus;

public partial class TypingStatusService : ITypingStatusService
{
    private readonly ILogger<TypingStatusService> _logger;
    private readonly ITypingStatusQueues _queues;

    public TypingStatusService(ITypingStatusQueues queues, ILogger<TypingStatusService> logger)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(logger);
        _queues = queues;
        _logger = logger;
    }

    public TypingScope StartTyping(long chatId)
    {
        var requestId = Guid.NewGuid();
        Enqueue(chatId, requestId, true);
        return new(() => Enqueue(chatId, requestId, false));
    }

    private void Enqueue(long chatId, Guid requestId, bool isTyping)
    {
        if (!_queues.TryEnqueue(chatId, new(chatId, requestId, isTyping)))
        {
            // Очереди без потолка, так что сюда попадает только чат без очереди или остановка
            // приложения. Молча терять именно выключение нельзя: чат останется печатать
            Log.CommandNotEnqueued(_logger, chatId, isTyping);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "There is no active typing status queue for chat {ChatId}, typing={IsTyping} skipped")]
        public static partial void CommandNotEnqueued(ILogger logger, long chatId, bool isTyping);
    }
}
