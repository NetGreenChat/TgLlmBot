using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.Commands.ChatWithLlm;
using TgLlmBot.Commands.DisplayHelp;
using TgLlmBot.Commands.Model;
using TgLlmBot.Commands.Ping;
using TgLlmBot.Commands.Rating;
using TgLlmBot.Commands.Repo;
using TgLlmBot.Commands.ResetChatSystemPrompt;
using TgLlmBot.Commands.ResetPersonalSystemPrompt;
using TgLlmBot.Commands.SetChatSystemPrompt;
using TgLlmBot.Commands.SetLimit;
using TgLlmBot.Commands.SetPersonalSystemPrompt;
using TgLlmBot.Commands.ShowChatSystemPrompt;
using TgLlmBot.Commands.ShowPersonalSystemPrompt;
using TgLlmBot.Commands.Usage;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Media;
using TgLlmBot.Services.Telegram.SelfInformation;
using RatingCommandHandler = TgLlmBot.Commands.Rating.RatingCommandHandler;

namespace TgLlmBot.CommandDispatcher;

public partial class DefaultTelegramCommandDispatcher : ITelegramCommandDispatcher
{
    private static readonly HashSet<MessageType> AllowedMessageTypes =
    [
        MessageType.Text,
        MessageType.Photo,
        MessageType.Sticker
    ];

    private readonly ChatWithLlmCommandHandler _chatWithLlm;
    private readonly DisplayHelpCommandHandler _displayHelp;
    private readonly ILogger<DefaultTelegramCommandDispatcher> _logger;
    private readonly IMediaGroupTracker _mediaGroupTracker;
    private readonly IMediaRecognitionQueues _mediaRecognitionQueues;
    private readonly ITelegramMessageStorage _messageStorage;
    private readonly ModelCommandHandler _model;
    private readonly DefaultTelegramCommandDispatcherOptions _options;
    private readonly PingCommandHandler _ping;
    private readonly RatingCommandHandler _rating;
    private readonly RepoCommandHandler _repo;
    private readonly ResetChatSystemPromptCommandHandler _resetChatSystemPrompt;
    private readonly ResetPersonalSystemPromptCommandHandler _resetPersonalSystemPrompt;
    private readonly ITelegramSelfInformation _self;
    private readonly SetChatSystemPromptCommandHandler _setChatSystemPrompt;
    private readonly SetLimitCommandHandler _setLimit;
    private readonly SetPersonalSystemPromptCommandHandler _setPersonalSystemPrompt;
    private readonly ShowChatSystemPromptCommandHandler _showChatSystemPrompt;
    private readonly ShowPersonalSystemPromptCommandHandler _showPersonalSystemPrompt;
    private readonly UsageCommandHandler _usage;

    public DefaultTelegramCommandDispatcher(
        DefaultTelegramCommandDispatcherOptions options,
        ITelegramSelfInformation self,
        ITelegramMessageStorage messageStorage,
        DisplayHelpCommandHandler displayHelp,
        ChatWithLlmCommandHandler chatWithLlm,
        PingCommandHandler ping,
        RepoCommandHandler repo,
        ModelCommandHandler model,
        UsageCommandHandler usage,
        RatingCommandHandler rating,
        SetChatSystemPromptCommandHandler setChatSystemPrompt,
        ResetChatSystemPromptCommandHandler resetChatSystemPrompt,
        SetPersonalSystemPromptCommandHandler setPersonalSystemPrompt,
        ResetPersonalSystemPromptCommandHandler resetPersonalSystemPrompt,
        ShowChatSystemPromptCommandHandler showChatSystemPrompt,
        ShowPersonalSystemPromptCommandHandler showPersonalSystemPrompt,
        SetLimitCommandHandler setLimit,
        IMediaRecognitionQueues mediaRecognitionQueues,
        IMediaGroupTracker mediaGroupTracker,
        ILogger<DefaultTelegramCommandDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(messageStorage);
        ArgumentNullException.ThrowIfNull(displayHelp);
        ArgumentNullException.ThrowIfNull(chatWithLlm);
        ArgumentNullException.ThrowIfNull(ping);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(rating);
        ArgumentNullException.ThrowIfNull(setChatSystemPrompt);
        ArgumentNullException.ThrowIfNull(resetChatSystemPrompt);
        ArgumentNullException.ThrowIfNull(setPersonalSystemPrompt);
        ArgumentNullException.ThrowIfNull(resetPersonalSystemPrompt);
        ArgumentNullException.ThrowIfNull(showChatSystemPrompt);
        ArgumentNullException.ThrowIfNull(showPersonalSystemPrompt);
        ArgumentNullException.ThrowIfNull(setLimit);
        ArgumentNullException.ThrowIfNull(mediaRecognitionQueues);
        ArgumentNullException.ThrowIfNull(mediaGroupTracker);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _self = self;
        _messageStorage = messageStorage;
        _displayHelp = displayHelp;
        _chatWithLlm = chatWithLlm;
        _ping = ping;
        _repo = repo;
        _model = model;
        _usage = usage;
        _rating = rating;
        _setChatSystemPrompt = setChatSystemPrompt;
        _resetChatSystemPrompt = resetChatSystemPrompt;
        _setPersonalSystemPrompt = setPersonalSystemPrompt;
        _resetPersonalSystemPrompt = resetPersonalSystemPrompt;
        _showChatSystemPrompt = showChatSystemPrompt;
        _showPersonalSystemPrompt = showPersonalSystemPrompt;
        _setLimit = setLimit;
        _mediaRecognitionQueues = mediaRecognitionQueues;
        _mediaGroupTracker = mediaGroupTracker;
        _logger = logger;
    }

    public async Task HandleMessageAsync(Message? message, UpdateType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return;
        }

        if (!AllowedMessageTypes.Contains(message.Type))
        {
            return;
        }

        var self = _self.GetSelf();
        var storedMessage = await _messageStorage.StoreMessageAsync(message, self, cancellationToken);
        // Решаем судьбу сообщения до постановки вложений в очередь: если отвечать на него будем мы,
        // описания ужмёт медиа-пайплайн перед ответом, а не фоновый воркер сразу
        var chatWithLlmCommand = TryCreateChatWithLlmCommand(message, type, self);
        var hasSupportedMedia = storedMessage.Media.Any(static m => !string.IsNullOrEmpty(m.DownloadFileId));
        var isBotOwnMessage = storedMessage.IsLlmReplyToMessage;

        // Регистрируем альбом даже без вложений: по этой отметке фронтовое задание понимает,
        // что пачка ещё едет, и не отвечает по половине картинок
        if (!isBotOwnMessage && !string.IsNullOrEmpty(storedMessage.MediaGroupId))
        {
            _mediaGroupTracker.Register(storedMessage.ChatId, storedMessage.MediaGroupId);
        }

        if (!isBotOwnMessage)
        {
            if (chatWithLlmCommand is not null && hasSupportedMedia)
            {
                // Сообщение адресовано боту и несёт картинки: ответ пойдёт только после распознавания
                // и компактинга, поэтому дальше обрабатываем в медиа-пайплайне, а команду в LLM-очередь
                // поставит воркер. Постановка во фронт блокирующая: дропать ответ недопустимо.
                var job = new MediaRecognitionJob(
                    message,
                    storedMessage,
                    message.Caption ?? message.Text,
                    requiresResponse: true,
                    command: chatWithLlmCommand);
                if (!await _mediaRecognitionQueues.EnqueueAsync(storedMessage.ChatId, job, cancellationToken))
                {
                    Log.MediaNotEnqueued(_logger, storedMessage.ChatId, storedMessage.MessageId);
                }

                return;
            }

            if (hasSupportedMedia)
            {
                // Сообщение просто ложится в историю: вложения распознаются в фоне, в конец очереди.
                // Не блокирует разгребание входящих сообщений.
                var job = new MediaRecognitionJob(
                    message,
                    storedMessage,
                    message.Caption ?? message.Text,
                    requiresResponse: false,
                    command: null);
                if (!await _mediaRecognitionQueues.EnqueueAsync(storedMessage.ChatId, job, cancellationToken))
                {
                    Log.MediaNotEnqueued(_logger, storedMessage.ChatId, storedMessage.MessageId);
                }
            }
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        var rawPrompt = $"{message.Text?.Trim()?.ToLowerInvariant()}";
        switch (rawPrompt)
        {
            case "!help":
                {
                    var command = new DisplayHelpCommand(message, type);
                    await _displayHelp.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!ping":
                {
                    var command = new PingCommand(message, type);
                    await _ping.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!repo":
                {
                    var command = new RepoCommand(message, type);
                    await _repo.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!model":
                {
                    var command = new ModelCommand(message, type);
                    await _model.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!usage":
                {
                    var command = new UsageCommand(message, type);
                    await _usage.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!rating":
                {
                    var command = new RatingCommand(message, type, self);
                    await _rating.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!chat_role_reset":
                {
                    var command = new ResetChatSystemPromptCommand(message, type, self);
                    await _resetChatSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!personal_role_reset":
                {
                    var command = new ResetPersonalSystemPromptCommand(message, type, self);
                    await _resetPersonalSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!personal_role_show":
                {
                    var command = new ShowPersonalSystemPromptCommand(message, type, self);
                    await _showPersonalSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!chat_role_show":
                {
                    var command = new ShowChatSystemPromptCommand(message, type, self);
                    await _showChatSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
        }

        if (rawPrompt.StartsWith("!chat_role", StringComparison.Ordinal))
        {
            var command = new SetChatSystemPromptCommand(message, type, self);
            await _setChatSystemPrompt.HandleAsync(command, cancellationToken);
            return;
        }

        if (rawPrompt.StartsWith("!personal_role", StringComparison.Ordinal))
        {
            var command = new SetPersonalSystemPromptCommand(message, type, self);
            await _setPersonalSystemPrompt.HandleAsync(command, cancellationToken);
            return;
        }

        if (rawPrompt.StartsWith("!set_limit", StringComparison.Ordinal))
        {
            var command = new SetLimitCommand(message, type, self);
            await _setLimit.HandleAsync(command, cancellationToken);
            return;
        }

        if (chatWithLlmCommand is not null)
        {
            await _chatWithLlm.HandleAsync(chatWithLlmCommand, cancellationToken);
        }
    }

    /// <summary>
    ///     Собирает запрос к LLM, если сообщение адресовано боту и отвечать на него ещё не начали.
    /// </summary>
    private ChatWithLlmCommand? TryCreateChatWithLlmCommand(Message message, UpdateType type, User self)
    {
        var prompt = message.Text ?? message.Caption;
        var isAddressedToBot = message.Chat.Type switch
        {
            ChatType.Private => true,
            ChatType.Group or ChatType.Supergroup =>
                prompt?.StartsWith(_options.BotName, StringComparison.OrdinalIgnoreCase) is true
                || message.ReplyToMessage?.From?.Id == self.Id,
            _ => false
        };
        if (!isAddressedToBot)
        {
            return null;
        }

        // Альбом - это N отдельных сообщений, и условию "адресовано боту" удовлетворяет каждое из них.
        // Отвечаем один раз на всю пачку: подписи и вложения остальных частей обработчик соберёт сам
        if (!string.IsNullOrEmpty(message.MediaGroupId)
            && !_mediaGroupTracker.TryBeginLlmRequest(message.Chat.Id, message.MediaGroupId))
        {
            return null;
        }

        return new(message, type, self, prompt);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Media recognition queue rejected attachments of message {MessageId} in chat {ChatId}")]
        public static partial void MediaNotEnqueued(ILogger logger, long chatId, int messageId);
    }
}
