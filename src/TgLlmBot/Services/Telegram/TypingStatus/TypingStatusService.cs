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

    public void StartTyping(long chatId)
    {
        Enqueue(chatId, true);
    }

    public void StopTyping(long chatId)
    {
        Enqueue(chatId, false);
    }

    private void Enqueue(long chatId, bool isTyping)
    {
        if (!_queues.TryEnqueue(chatId, new(chatId, isTyping)))
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
