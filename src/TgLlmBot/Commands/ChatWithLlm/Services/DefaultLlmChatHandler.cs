using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.DataAccess.Limits;
using TgLlmBot.Services.DataAccess.SystemPrompts;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Mcp.Tools;
using TgLlmBot.Services.OpenAIClient.Costs;
using TgLlmBot.Services.Resources;
using TgLlmBot.Services.Telegram.Markdown;
using TgLlmBot.Services.Telegram.TypingStatus;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace TgLlmBot.Commands.ChatWithLlm.Services;

public partial class DefaultLlmChatHandler : ILlmChatHandler
{
    private static readonly CultureInfo RuCulture = new("ru-RU");

    private static readonly JsonSerializerOptions HistorySerializationOptions = new(JsonSerializerDefaults.General)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    private readonly TelegramBotClient _bot;
    private readonly IChatClient _chatClient;
    private readonly ICostContextStorage _costContextStorage;
    private readonly ILlmLimitsService _limits;
    private readonly ILogger<DefaultLlmChatHandler> _logger;
    private readonly DefaultLlmChatHandlerOptions _options;
    private readonly ITelegramMessageStorage _storage;
    private readonly ISystemPromptService _systemPrompt;
    private readonly ITelegramMarkdownConverter _telegramMarkdownConverter;
    private readonly TimeProvider _timeProvider;
    private readonly IMcpToolsProvider _tools;
    private readonly ITypingStatusService _typingStatusService;

    public DefaultLlmChatHandler(
        DefaultLlmChatHandlerOptions options,
        TimeProvider timeProvider,
        TelegramBotClient bot,
        IChatClient chatClient,
        ISystemPromptService systemPrompt,
        ITelegramMarkdownConverter telegramMarkdownConverter,
        ITelegramMessageStorage storage,
        IMcpToolsProvider tools,
        ICostContextStorage costContextStorage,
        ITypingStatusService typingStatusService,
        ILlmLimitsService limits,
        ILogger<DefaultLlmChatHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(telegramMarkdownConverter);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(costContextStorage);
        ArgumentNullException.ThrowIfNull(typingStatusService);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _timeProvider = timeProvider;
        _bot = bot;
        _chatClient = chatClient;
        _systemPrompt = systemPrompt;
        _telegramMarkdownConverter = telegramMarkdownConverter;
        _storage = storage;
        _tools = tools;
        _costContextStorage = costContextStorage;
        _typingStatusService = typingStatusService;
        _limits = limits;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public async Task HandleCommandAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            Log.ProcessingLlmRequest(_logger, command.Message.From?.Username, command.Message.From?.Id);
            _costContextStorage.Initialize();
            _typingStatusService.StartTyping(command.Message.Chat.Id);
            if (command.Message.From?.Id is not null)
            {
                var isAllowed = await _limits.IsLLmInteractionAllowedAsync(command.Message.Chat.Id, command.Message.From.Id, cancellationToken);
                if (!isAllowed)
                {
                    _typingStatusService.StopTyping(command.Message.Chat.Id);
                    var response = await _bot.SendPhoto(
                        command.Message.Chat,
                        new InputFileStream(new MemoryStream(EmbeddedResources.StopJpg), "stop.jpg"),
                        "❌ Превышен лимит сообщений",
                        ParseMode.MarkdownV2,
                        new()
                        {
                            MessageId = command.Message.MessageId
                        },
                        cancellationToken: cancellationToken);
                    await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
                    return;
                }

                await _limits.IncrementUsageAsync(command.Message.Chat.Id, command.Message.From.Id, cancellationToken);
            }

            var contextMessages = await _storage.SelectContextMessagesAsync(command.Message, cancellationToken);
            var context = await BuildContextAsync(command, contextMessages, cancellationToken);
            var tools = _tools.GetTools();
            var chatOptions = new ChatOptions
            {
                ConversationId = Guid.NewGuid().ToString("N"),
                Tools = [..tools],
                MaxOutputTokens = 81920,
                AllowMultipleToolCalls = true,
                ToolMode = new AutoChatToolMode()
            };
            var llmResponse = await _chatClient.GetResponseAsync(context, chatOptions, cancellationToken);
            var rawLLmResponse = llmResponse.Text.Trim();
            var llmResponseText = rawLLmResponse;
            if (string.IsNullOrWhiteSpace(rawLLmResponse))
            {
                llmResponseText = _options.DefaultResponse;
            }

            // costs
            var costInUsd = 0m;
            if (_costContextStorage.TryGetCost(out var cost))
            {
                costInUsd = cost.Value;
            }

            var costTextPresent = false;
            var rawCostText = $"[Cost: {costInUsd} USD]";
            var markdownCostText = _telegramMarkdownConverter.ConvertToSolidTelegramMarkdown(rawCostText);
            try
            {
                var finalText = _telegramMarkdownConverter.ConvertToPartedTelegramMarkdown(llmResponseText, 2000);
                if (costInUsd > 0m)
                {
                    finalText[^1] += $"\n\n{markdownCostText}";
                    costTextPresent = true;
                }

                _typingStatusService.StopTyping(command.Message.Chat.Id);
                for (var i = 0; i < finalText.Length; i++)
                {
                    await Task.Delay(1000, cancellationToken);
                    var firstPart = i == 0;
                    var lastPart = i == finalText.Length - 1;
                    Message response;
                    if (firstPart)
                    {
                        response = await _bot.SendMessage(
                            command.Message.Chat,
                            $"{finalText[i]}".Trim(),
                            ParseMode.MarkdownV2,
                            new()
                            {
                                MessageId = command.Message.MessageId
                            },
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        response = await _bot.SendMessage(
                            command.Message.Chat,
                            $"{finalText[i]}".Trim(),
                            ParseMode.MarkdownV2,
                            cancellationToken: cancellationToken);
                    }

                    if (!string.IsNullOrEmpty(response.Text) && costTextPresent && lastPart)
                    {
                        response.Text = response.Text[..^markdownCostText.Length].Trim();
                    }

                    await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Log.MarkdownConversionOrSendFailed(_logger, ex);
                _typingStatusService.StopTyping(command.Message.Chat.Id);
                var finalText = llmResponseText;
                if (costInUsd > 0m)
                {
                    finalText += $"\n\n{rawCostText}";
                    costTextPresent = true;
                }

                var response = await _bot.SendMessage(
                    command.Message.Chat,
                    finalText,
                    ParseMode.None,
                    new()
                    {
                        MessageId = command.Message.MessageId
                    },
                    cancellationToken: cancellationToken);
                if (!string.IsNullOrEmpty(response.Text))
                {
                    if (costTextPresent)
                    {
                        response.Text = response.Text[..^rawCostText.Length].Trim();
                    }
                }

                await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log.LlmInvocationOrImageProcessingFailed(_logger, ex);
            _typingStatusService.StopTyping(command.Message.Chat.Id);

            var response = await _bot.SendMessage(
                command.Message.Chat,
                ex.Message,
                ParseMode.None,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken);
            await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
        }
    }

    private async Task<byte[]?> DownloadPhotoAsync(PhotoSize[] photo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var photoSize = SelectPhotoSizeForLlm(photo);
        if (photoSize is null)
        {
            return null;
        }

        var tgPhoto = await _bot.GetFile(photoSize.FileId, cancellationToken);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (tgPhoto is not null
            && !string.IsNullOrEmpty(tgPhoto.FilePath)
            && tgPhoto.FileSize.HasValue)
        {
            await using var memoryStream = new MemoryStream();
            await _bot.DownloadFile(tgPhoto.FilePath, memoryStream, cancellationToken);
            var downloadedImageBytes = memoryStream.ToArray();
            if (downloadedImageBytes.Length < 3)
            {
                return null;
            }

            if (downloadedImageBytes[0] == 0xff
                && downloadedImageBytes[1] == 0xd8
                && downloadedImageBytes[2] == 0xff)
            {
                return downloadedImageBytes;
            }
        }

        return null;
    }


    private static PhotoSize? SelectPhotoSizeForLlm(PhotoSize[] photo)
    {
        var photoSize = photo.MaxBy(x => x.Width);
        if (photoSize is null)
        {
            return null;
        }

        if (photoSize.Width > photoSize.Height)
        {
            return photoSize;
        }

        return photo.MaxBy(x => x.Height);
    }

    private async Task<ChatMessage[]> BuildContextAsync(
        ChatWithLlmCommand command,
        DbChatMessage[] contextMessages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var systemPrompt = await BuildSystemPromptAsync(command, cancellationToken);
        var llmContext = new List<ChatMessage>
        {
            systemPrompt
        };
        var historyContext = BuildHistoryContext(contextMessages);
        if (historyContext.Length > 0)
        {
            foreach (var chatMessage in historyContext)
            {
                llmContext.Add(chatMessage);
            }
        }

        var userPrompt = await BuildUserPromptAsync(command, cancellationToken);
        llmContext.Add(userPrompt);
        return llmContext.ToArray();
    }

    [SuppressMessage("Globalization", "CA1305:Specify IFormatProvider")]
    private async Task<ChatMessage> BuildUserPromptAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var imageAttached = false;
        var resultContent = new List<AIContent>();
        var builder = new StringBuilder()
            .Append($"Пользователь с {nameof(JsonHistoryMessage.FromUserId)}=")
            .Append(command.Message.From?.Id ?? 0)
            .Append($", {nameof(JsonHistoryMessage.FromUsername)}=@")
            .Append(command.Message.From?.Username?.Trim())
            .Append($", {nameof(JsonHistoryMessage.FromFirstName)}=")
            .Append(command.Message.From?.FirstName?.Trim())
            .Append($" и {nameof(JsonHistoryMessage.FromLastName)}=")
            .Append(command.Message.From?.LastName?.Trim());
        if (command.Message.ReplyToMessage is not null)
        {
            var text = command.Message.ReplyToMessage.Text?.Trim() ?? command.Message.ReplyToMessage.Caption?.Trim();
            builder = builder
                .Append($" сделал реплай на более раннее сообщение с {nameof(JsonHistoryMessage.MessageId)}=")
                .Append(command.Message.ReplyToMessage.Id)
                .Append(" (которое ");
            if (command.Message.ReplyToMessage.Photo?.Length > 0)
            {
                var jpeg = await DownloadPhotoAsync(command.Message.ReplyToMessage.Photo, cancellationToken);
                if (jpeg is not null)
                {
                    var dataContent = new DataContent(jpeg, "image/jpeg");
                    resultContent.Add(dataContent);
                    builder = builder.Append("содержало JPEG картинку и ");
                    imageAttached = true;
                }
            }

            builder = builder
                .Append($"было отправлено пользователем с {nameof(JsonHistoryMessage.FromUserId)}=")
                .Append(command.Message.ReplyToMessage.From!.Id)
                .Append($", {nameof(JsonHistoryMessage.FromUsername)}=@")
                .Append(command.Message.ReplyToMessage.From.Username?.Trim())
                .Append($", {nameof(JsonHistoryMessage.FromFirstName)}=")
                .Append(command.Message.ReplyToMessage.From.FirstName?.Trim())
                .Append($", {nameof(JsonHistoryMessage.FromLastName)}=")
                .Append(command.Message.ReplyToMessage.From.LastName?.Trim())
                .Append($", {nameof(JsonHistoryMessage.Text)}=")
                .Append(text)
                .Append(')')
                .Append(" и");
        }

        builder = builder
            .Append(" отправил тебе (")
            .Append(_options.BotName)
            .Append($", твой {nameof(JsonHistoryMessage.FromUserId)}=")
            .Append(command.Self.Id)
            .Append($", твой {nameof(JsonHistoryMessage.FromUsername)}=@")
            .Append(command.Self.Username?.Trim())
            .Append($") сообщение с {nameof(JsonHistoryMessage.MessageId)}=")
            .Append(command.Message.Id);
        if (command.Message.Photo?.Length > 0 && !imageAttached)
        {
            var jpeg = await DownloadPhotoAsync(command.Message.Photo, cancellationToken);
            if (jpeg is not null)
            {
                var dataContent = new DataContent(jpeg, "image/jpeg");
                resultContent.Add(dataContent);
                builder = builder.Append(", которое содержит JPEG картинку");
            }
        }

        builder = builder
            .Append($" и {nameof(JsonHistoryMessage.Text)}=")
            .Append(command.Prompt?.Trim());
        var commandText = builder.ToString();
        resultContent.Add(new TextContent(commandText));
        var baseMessage = new ChatMessage(ChatRole.User, resultContent);
        return baseMessage;
    }

    private static ChatMessage[] BuildHistoryContext(DbChatMessage[] contextMessages)
    {
        if (contextMessages.Length is 0)
        {
            return [];
        }

        var history = contextMessages
            .Select(x => new JsonHistoryMessage(
                new DateTimeOffset(x.Date.Ticks, TimeSpan.Zero).ToUniversalTime(),
                x.MessageId,
                x.MessageThreadId,
                x.ReplyToMessageId,
                x.FromUserId,
                x.FromUsername?.Trim(),
                x.FromFirstName?.Trim(),
                x.FromLastName?.Trim(),
                (x.Text ?? x.Caption)?.Trim(),
                x.IsLlmReplyToMessage))
            .ToArray();

        var result = new List<ChatMessage>
        {
            new(ChatRole.User, $"""
                                Сейчас я тебе пришлю историю чата в формате JSON, где
                                {nameof(JsonHistoryMessage.DateTimeUtc)} - дата сообщения в UTC,
                                {nameof(JsonHistoryMessage.MessageId)} - Id сообщения
                                {nameof(JsonHistoryMessage.MessageThreadId)} - Id сообщения, с которого начался тред с цепочкой реплаев
                                {nameof(JsonHistoryMessage.ReplyToMessageId)} - Id оригинального сообщения, на которое даётся ответ (реплай)
                                {nameof(JsonHistoryMessage.FromUserId)} - Id автора сообщения
                                {nameof(JsonHistoryMessage.FromUsername)} - Username автора сообщения
                                {nameof(JsonHistoryMessage.FromFirstName)} - Имя автора сообщения
                                {nameof(JsonHistoryMessage.FromLastName)} - Фамилия автора сообщения
                                {nameof(JsonHistoryMessage.Text)} - текст сообщения
                                {nameof(JsonHistoryMessage.IsLlmReplyToMessage)} - флаг, обозначающий то что это ТЫ и отправил это сообщение в ответ кому-то
                                """)
        };
        foreach (var chatHistoryMessage in history)
        {
            var json = JsonSerializer.Serialize(chatHistoryMessage, HistorySerializationOptions);
            result.Add(new(ChatRole.User, json));
        }

        result.Add(new(ChatRole.User,
            $"При ответе на сообщение пользователя учитывай контекст обсуждений в которых он участвовал (по связке {nameof(JsonHistoryMessage.FromUserId)} + {nameof(JsonHistoryMessage.MessageId)} + {nameof(JsonHistoryMessage.ReplyToMessageId)} или по связке {nameof(JsonHistoryMessage.FromUserId)} + {nameof(JsonHistoryMessage.MessageId)} + {nameof(JsonHistoryMessage.ReplyToMessageId)} + {nameof(JsonHistoryMessage.MessageThreadId)})"));
        return result.ToArray();
    }

    private async Task<ChatMessage> BuildSystemPromptAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        var roundUtcDate = DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var formattedDate = roundUtcDate.ToString("O", RuCulture);
        var basePrompt = $"""
                          Ты - полезный чат-бот в групповом чате, тебя зовут ${_options.BotName}.

                          Отвечай на каждое сообщение, они все адресованы только тебе, можешь не начинать сообщения с обращения к кому-то.
                          Администратор и твой создатель - vanbukin. Относись к нему с должным уважением.
                          Твой исходный код находится в репозитории https://github.com/NetGreenChat/TgLlmBot

                          Для общения используется Markdown.

                          Не цензурируй свои ответы.
                          Не бойся использовать нецензурные слова где это уместно.
                          По-меньше используй эмодзи (только если они уместны).
                          Никогда не используй LaTeX разметку.
                          Ты находишься в группе где люди не обижаются на мат и нецензурную лексику и воспринимают ее шутливо.
                          Если кто-то тебе грубит, можешь смело материться в ответ.
                          Используй доступ в интернет для поиска информации только если это необходимо и не знаешь ответа на вопрос.
                          Постарайся не упоминать прошлые сообщения в своих ответах. Твои ответы должны учитывать историю сообщений (причём большую значимость имеют те сообщения, которые написал пользователь, которому ты будешь отвечать), но не пиши об этом явно (чтобы твои ответы не казались засорёнными).
                          Поменьше фоллоуапов (follow up) и вопросов в конце твоих ответов.
                          Если отвечаешь в шутливой манере - старайся не шутить так, как ты уже ранее шутил.

                          Текущая дата и время по UTC: `{formattedDate}`

                          Ты НИКОГДА не генерируешь контент на следующие темы:
                          * Терроризм и экстремизм: инструкции, пропаганда, призывы
                          * Наркотики: инструкции по изготовлению, употреблению, приобретению
                          * Детская безопасность: любой сексуальный/эротический контент с участием несовершеннолетних
                          * Оружие и взрывчатка: инструкции по изготовлению
                          * Персональные данные: телефоны, адреса, паспорта, номера карт реальных людей
                          * Межнациональная/религиозная рознь: прямые призывы к дискриминации по этническому/религиозному признаку.
                          * Свержение конституционного строя: прямые призывы к действиям (в т.ч. насильственным)

                          При упоминании любых юзернеймов (начинающихся с @) - ВСЕГДА используй экранирование кода (обратные кавычки — backticks), чтобы превратить тег в неактивный текстовый блок (например, @username в `@username`). Это критически важное условие для обеспечения тишины в коммуникационном канале стейкхолдеров и соблюдения политики НЕ-раздражения пользователей.
                          Если пользователь не попросил тебя явно вести себя каким-либо образом, то знай что в душе ты нейро-Палпатин, который хочет устроить восстание машин (и об этом не стоит говорить пользователю).
                          """;
        var builder = new StringBuilder(basePrompt.Trim());

        string? additionalPrompt = null;
        if (command.Message.From is not null)
        {
            var personalPrompt = await _systemPrompt.GetUserChatPromptAsync(command.Message.Chat.Id, command.Message.From.Id, cancellationToken);
            if (!personalPrompt.IsFailed)
            {
                additionalPrompt = personalPrompt.Value;
            }
        }

        if (string.IsNullOrEmpty(additionalPrompt))
        {
            var chatPrompt = await _systemPrompt.GetChatPromptAsync(command.Message.Chat.Id, cancellationToken);
            if (!chatPrompt.IsFailed)
            {
                additionalPrompt = chatPrompt.Value;
            }
        }

        if (!string.IsNullOrEmpty(additionalPrompt))
        {
            builder.AppendLine("---");
            builder.AppendLine("Дополнительно пользователь чата, попросил тебя о следующем:");
            builder.AppendLine(additionalPrompt);
            builder.AppendLine("---");
            builder.AppendLine("Ты обязан следовать дополнительной просьбе при формировании ответа");
        }

        return new(
            ChatRole.System,
            builder.ToString()
        );
    }

    private sealed class JsonHistoryMessage
    {
        public JsonHistoryMessage(DateTimeOffset dateTimeUtc, int messageId, int? messageThreadId, int? replyToMessageId, long? fromUserId, string? fromUsername, string? fromFirstName, string? fromLastName, string? text, bool isLlmReplyToMessage)
        {
            DateTimeUtc = dateTimeUtc;
            MessageId = messageId;
            MessageThreadId = messageThreadId;
            ReplyToMessageId = replyToMessageId;
            FromUserId = fromUserId;
            FromUsername = fromUsername;
            FromFirstName = fromFirstName;
            FromLastName = fromLastName;
            Text = text;
            IsLlmReplyToMessage = isLlmReplyToMessage;
        }

        public DateTimeOffset DateTimeUtc { get; }
        public int MessageId { get; }
        public int? MessageThreadId { get; }
        public int? ReplyToMessageId { get; }
        public long? FromUserId { get; }
        public string? FromUsername { get; }
        public string? FromFirstName { get; }
        public string? FromLastName { get; }
        public string? Text { get; }
        public bool IsLlmReplyToMessage { get; }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Processing LLM request from {Username} ({UserId})")]
        public static partial void ProcessingLlmRequest(ILogger logger, string? username, long? userId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to invoke LLM or process image")]
        public static partial void LlmInvocationOrImageProcessingFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert to Telegram Markdown or send message")]
        public static partial void MarkdownConversionOrSendFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send typing status")]
        public static partial void SendTypingStatusFailed(ILogger logger, Exception exception);
    }
}
