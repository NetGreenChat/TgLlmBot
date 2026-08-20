using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgLlmBot.Commands.ChatWithLlm.Queues;
using TgLlmBot.Commands.ChatWithLlm.Services;

namespace TgLlmBot.Commands.ChatWithLlm.BackgroundServices.LlmRequests;

public partial class LlmRequestsBackgroundService : BackgroundService
{
    private readonly ILlmChatHandler _llmChatHandler;
    private readonly ILogger<LlmRequestsBackgroundService> _logger;
    private readonly ILlmRequestQueues _queues;

    public LlmRequestsBackgroundService(
        ILlmRequestQueues queues,
        ILlmChatHandler llmChatHandler,
        ILogger<LlmRequestsBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(llmChatHandler);
        ArgumentNullException.ThrowIfNull(logger);
        _queues = queues;
        _llmChatHandler = llmChatHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var readers = _queues.Readers;
        Log.BackgroundServiceStarted(_logger, readers.Count);
        try
        {
            // Каждый чат обрабатывается своим воркером, поэтому запросы из разных чатов идут параллельно,
            // а внутри одного чата - строго последовательно.
            var workers = new List<Task>(readers.Count);
            foreach (var (chatId, reader) in readers)
            {
                // stoppingToken намеренно не передаётся в Task.Run - воркер сам обрабатывает отмену внутри
                workers.Add(Task.Run(() => ProcessChatQueueAsync(chatId, reader, stoppingToken), CancellationToken.None));
            }

            await Task.WhenAll(workers);
        }
        finally
        {
            Log.BackgroundServiceCompleted(_logger);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    [SuppressMessage("ReSharper", "RedundantWithCancellation")]
    private async Task ProcessChatQueueAsync(
        long chatId,
        ChannelReader<ChatWithLlmCommand> reader,
        CancellationToken stoppingToken)
    {
        Log.ChatWorkerStarted(_logger, chatId);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await foreach (var request in reader.ReadAllAsync(stoppingToken).WithCancellation(stoppingToken))
                    {
                        Log.HandlingRequest(_logger, chatId);
                        await HandleCommandAsync(request, stoppingToken);
                        Log.HandledRequest(_logger, chatId);
                    }

                    // очередь завершена и вычитана до конца
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // graceful shutdown
                    break;
                }
                catch (Exception ex)
                {
                    Log.UnknownException(_logger, chatId, ex);
                }
            }
        }
        finally
        {
            Log.ChatWorkerCompleted(_logger, chatId);
        }
    }

    private async Task HandleCommandAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _llmChatHandler.HandleCommandAsync(command, cancellationToken);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = $"{nameof(LlmRequestsBackgroundService)} started with {{QueuesCount}} per-chat queues")]
        public static partial void BackgroundServiceStarted(ILogger logger, int queuesCount);

        [LoggerMessage(Level = LogLevel.Information, Message = $"{nameof(LlmRequestsBackgroundService)} completed")]
        public static partial void BackgroundServiceCompleted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Started LLM requests worker for chat {ChatId}")]
        public static partial void ChatWorkerStarted(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Completed LLM requests worker for chat {ChatId}")]
        public static partial void ChatWorkerCompleted(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Handling LLM request of chat {ChatId}")]
        public static partial void HandlingRequest(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Handled LLM request of chat {ChatId}")]
        public static partial void HandledRequest(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unknown exception while handling LLM requests of chat {ChatId}")]
        public static partial void UnknownException(ILogger logger, long chatId, Exception exception);
    }
}
