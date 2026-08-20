using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Commands.ChatWithLlm.Queues;

namespace TgLlmBot.Commands.ChatWithLlm;

public partial class ChatWithLlmCommandHandler : AbstractCommandHandler<ChatWithLlmCommand>
{
    private readonly ILogger<ChatWithLlmCommandHandler> _logger;
    private readonly ILlmRequestQueues _queues;

    public ChatWithLlmCommandHandler(
        ILlmRequestQueues queues,
        ILogger<ChatWithLlmCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(logger);
        _queues = queues;
        _logger = logger;
    }

    public override Task HandleAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var chatId = command.Message.Chat.Id;
        if (!_queues.TryEnqueue(chatId, command))
        {
            Log.RequestNotEnqueued(_logger, chatId, command.Message.MessageId);
        }

        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "There is no active LLM request queue for chat {ChatId}, message {MessageId} skipped")]
        public static partial void RequestNotEnqueued(ILogger logger, long chatId, int messageId);
    }
}
