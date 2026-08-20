using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher;
using TgLlmBot.Services.DataAccess.KickedUsers;

namespace TgLlmBot.Services.Telegram.RequestHandler;

public sealed partial class DefaultTelegramRequestHandler : ITelegramRequestHandler
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ITelegramCommandDispatcher _commandDispatcher;
    private readonly ITelegramKickedUsersStorage _kickedUsersStorage;
    private readonly ILogger<DefaultTelegramRequestHandler> _logger;
    private readonly DefaultTelegramRequestHandlerOptions _options;

    public DefaultTelegramRequestHandler(
        DefaultTelegramRequestHandlerOptions options,
        ITelegramCommandDispatcher commandDispatcher,
        IHostApplicationLifetime applicationLifetime,
        ILogger<DefaultTelegramRequestHandler> logger,
        ITelegramKickedUsersStorage kickedUsersStorage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(kickedUsersStorage);
        _options = options;
        _commandDispatcher = commandDispatcher;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _kickedUsersStorage = kickedUsersStorage;
    }

    public async Task OnMessageAsync(Message message, UpdateType type)
    {
        ArgumentNullException.ThrowIfNull(message);
        await OnMessageInternalAsync(message, type, _applicationLifetime.ApplicationStopping);
    }

    public async Task OnErrorAsync(Exception exception, HandleErrorSource source)
    {
        await OnErrorInternalAsync(exception, source, _applicationLifetime.ApplicationStopping);
    }

    public async Task OnUpdateAsync(Update update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await OnUpdateInternalAsync(update, _applicationLifetime.ApplicationStopping);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task OnMessageInternalAsync(Message message, UpdateType type, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var isMessageTooOld = message.Date < _options.SkipMessagesOlderThan;
        var isNewMessage = type == UpdateType.Message;
        var isChatAllowed = _options.AllowedChatIds.Contains(message.Chat.Id);

        if (!isChatAllowed)
        {
            // Старые сообщения не логируем: после рестарта Telegram переотдаёт накопившийся бэклог,
            // и лог залило бы варнингами о давно случившихся попытках.
            if (!isMessageTooOld && isNewMessage)
            {
                LogUnauthorizedChatAccess(
                    _logger,
                    message.Chat.Id,
                    message.Chat.Type,
                    message.Chat.Title,
                    message.Chat.Username,
                    message.From?.Id,
                    message.From?.Username,
                    message.From?.FirstName,
                    message.From?.LastName,
                    message.MessageId);
            }

            return;
        }

        if (isMessageTooOld || !isNewMessage)
        {
            return;
        }

        try
        {
            await _commandDispatcher.HandleMessageAsync(message, type, cancellationToken);
        }
        catch (Exception ex)
        {
            LogMessageHandlingException(_logger, ex);
        }
    }

    private Task OnErrorInternalAsync(Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            LogErrorHandling(_logger, source, exception);
        }

        return Task.CompletedTask;
    }

    private async Task OnUpdateInternalAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Type != UpdateType.ChatMember)
        {
            return;
        }

        var chatMemberUpdate = update.ChatMember!;
        var chatId = chatMemberUpdate.Chat.Id;
        var userId = chatMemberUpdate.NewChatMember.User.Id;

        if (chatMemberUpdate.NewChatMember.Status == ChatMemberStatus.Kicked)
        {
            await _kickedUsersStorage.StoreKickedUserAsync(chatId, userId, cancellationToken);
        }
        else if (chatMemberUpdate.OldChatMember.Status == ChatMemberStatus.Kicked && chatMemberUpdate.NewChatMember.Status != ChatMemberStatus.Kicked)
        {
            await _kickedUsersStorage.RemoveKickedUserAsync(chatId, userId, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unauthorized access: chat is not in the allow list. "
                                                       + "ChatId: {ChatId}, ChatType: {ChatType}, ChatTitle: {ChatTitle}, ChatUsername: {ChatUsername}, "
                                                       + "FromUserId: {FromUserId}, FromUsername: {FromUsername}, FromFirstName: {FromFirstName}, FromLastName: {FromLastName}, "
                                                       + "MessageId: {MessageId}")]
    private static partial void LogUnauthorizedChatAccess(
        ILogger logger,
        long chatId,
        ChatType chatType,
        string? chatTitle,
        string? chatUsername,
        long? fromUserId,
        string? fromUsername,
        string? fromFirstName,
        string? fromLastName,
        int messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telegram error handling. Error source: {ErrorSource}")]
    private static partial void LogErrorHandling(ILogger logger, HandleErrorSource errorSource, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Got exception during message handling")]
    private static partial void LogMessageHandlingException(ILogger logger, Exception exception);
}
