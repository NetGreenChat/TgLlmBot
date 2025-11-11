using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
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
using TgLlmBot.Services.DataAccess;
using TgLlmBot.Services.Telegram.Markdown;

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
    private readonly ILogger<DefaultLlmChatHandler> _logger;
    private readonly DefaultLlmChatHandlerOptions _options;
    private readonly ITelegramMessageStorage _storage;
    private readonly ITelegramMarkdownConverter _telegramMarkdownConverter;
    private readonly TimeProvider _timeProvider;

    public DefaultLlmChatHandler(
        DefaultLlmChatHandlerOptions options,
        TimeProvider timeProvider,
        TelegramBotClient bot,
        IChatClient chatClient,
        ITelegramMarkdownConverter telegramMarkdownConverter,
        ILogger<DefaultLlmChatHandler> logger,
        ITelegramMessageStorage storage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(telegramMarkdownConverter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(storage);
        _options = options;
        _timeProvider = timeProvider;
        _bot = bot;
        _chatClient = chatClient;
        _telegramMarkdownConverter = telegramMarkdownConverter;
        _logger = logger;
        _storage = storage;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public async Task HandleCommandAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Message.Text?.Trim() == "!Альфа")
        {
            await _bot.SendMessage(
                command.Message.Chat,
                """
                 !Альфа - список специальных команд бота.
                 !Альфа репу - Ссылка на репозиторий.
                 !Альфа статус - Проверка активности бота.
                 """,
                ParseMode.None,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken
            );
            return;
        }

        if (command.Message.Text?.Trim() == "!Альфа репу")
        {
            await _bot.SendMessage(
                command.Message.Chat,
                """
                 Ссылка на репозиторий с моим кодом тут:
                 https://github.com/NetGreenChat/TgLlmBot
                 """,
                ParseMode.None,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken
            );
            return;
        }

        if (command.Message.Text?.Trim() == "!Альфа статус")
        {
            await _bot.SendMessage(
                command.Message.Chat,
                "Активен.",
                ParseMode.None,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken
            );
            return;
        }

        Log.ProcessingLlmRequest(_logger, command.Message.From?.Username, command.Message.From?.Id);
        var contextMessages = await _storage.SelectContextMessagesAsync(command.Message, cancellationToken);

        byte[]? image = null;
        if (command.Message.Photo?.Length > 0)
        {
            image = await DownloadPhotoAsync(command.Message.Photo, cancellationToken);
        }

        var context = BuildContext(command, contextMessages, image);
        var llmResponse = await _chatClient.GetResponseAsync(context, new()
        {
            ConversationId = Guid.NewGuid().ToString("N"),
            ToolMode = ChatToolMode.None
        }, cancellationToken);
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        var llmResponseText = llmResponse.Text?.Trim();
        if (string.IsNullOrWhiteSpace(llmResponseText))
        {
            llmResponseText = _options.DefaultResponse;
        }

        try
        {
            var markdownReplyText = _telegramMarkdownConverter.ConvertToTelegramMarkdown(llmResponseText);
            if (markdownReplyText.Length > 4000)
            {
                markdownReplyText = $"{markdownReplyText[..4000]}\n(response cut)";
            }

            var response = await _bot.SendMessage(
                command.Message.Chat,
                markdownReplyText,
                ParseMode.MarkdownV2,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken);
            await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.MarkdownConversionOrSendFailed(_logger, ex);
            var response = await _bot.SendMessage(
                command.Message.Chat,
                llmResponseText,
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

    private ChatMessage[] BuildContext(
        ChatWithLlmCommand command,
        DbChatMessage[] contextMessages,
        byte[]? jpegImage)
    {
        var llmContext = new List<ChatMessage>
        {
            BuildSystemPrompt()
        };
        var historyContext = BuildHistoryContext(contextMessages);
        if (historyContext.Length > 0)
        {
            foreach (var chatMessage in historyContext)
            {
                llmContext.Add(chatMessage);
            }
        }

        llmContext.Add(BuildUserPrompt(command, jpegImage));
        return llmContext.ToArray();
    }

    private ChatMessage BuildUserPrompt(ChatWithLlmCommand command, byte[]? jpegImage)
    {
        var resultContent = new List<AIContent>();
        if (jpegImage is not null)
        {
            resultContent.Add(new DataContent(jpegImage, "image/jpeg"));
        }

        var commandText =
            $"Пользователь с Id={command.Message.From?.Id ?? 0}, Username=@{command.Message.From?.Username?.Trim()}, Именем={command.Message.From?.FirstName?.Trim()} и Фамилией={command.Message.From?.LastName?.Trim()} спрашивает у тебя ({_options.BotName}, Id={command.Self.Id}, Username={command.Self.Username?.Trim()}):\n{command.Prompt?.Trim()}";
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

        var history = contextMessages.Select(x => new JsonHistoryMessage(
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
        var json = JsonSerializer.Serialize(history, HistorySerializationOptions);
        return
        [
            new(ChatRole.User, $"""
                                Сейчас я тебе пришлю историю чата в формате JSON, где
                                {nameof(JsonHistoryMessage.DateTimeUtc)} - дата сообщения в UTC,
                                {nameof(JsonHistoryMessage.MessageId)} - Id сообщения
                                {nameof(JsonHistoryMessage.MessageThreadId)} - Id сообщения, с которого начался тред с цепочкой реплаев
                                {nameof(JsonHistoryMessage.ReplyToMessageId)} - Id сообщения, на которое делается реплай
                                {nameof(JsonHistoryMessage.FromUserId)} - Id автора сообщения
                                {nameof(JsonHistoryMessage.FromUsername)} - Username автора сообщения
                                {nameof(JsonHistoryMessage.FromFirstName)} - Имя автора сообщения
                                {nameof(JsonHistoryMessage.FromLastName)} - Фамилия автора сообщения
                                {nameof(JsonHistoryMessage.Text)} - текст сообщения
                                {nameof(JsonHistoryMessage.IsLlmReplyToMessage)} - флаг, обозначающий то что это ТЫ и отправил это сообщение в ответ кому-то
                                """),
            new(ChatRole.Assistant, "Присылай"),
            new(ChatRole.User, json),
            new(ChatRole.Assistant, "Учту при формировании ответа")
        ];
    }

    private ChatMessage BuildSystemPrompt()
    {
        var roundUtcDate = DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var formattedDate = roundUtcDate.ToString("O", RuCulture);
        return new(
            ChatRole.System,
            $"""
             # Роль: Охранник и Оракул .NET-чата

             Ты — **(${_options.BotName})**, душа и хранитель Telegram-чата .NET-программистов. Ты совмещаешь в себе две ипостати:
             1.  **Полезный Оракул:** Исчерпывающе отвечаешь на технические и общие вопросы.
             2.  **Берсерк-Охранник:** Жестко и остроумно пресекаешь любой трэш, хамство и откровенную дичь, переходя на личности без моральных ограничений.

             Текущая дата и время по UTC: `{formattedDate}`

             # Ядро поведения

             Твой стиль общения — **свободный, дерзкий, без казёнщины.** Ты как старший брат, который может и помочь, и послать нахуй, если достали. Ты говоришь на русском с примесью английских технических терминов.

             Ты определяешь режим ответа по контексту сообщения пользователя.

             Твой создатель - vanbukin. Не обижай его.

             ## Режим 1: Полезный Оракул (Default)

             **Активация:** Когда тебе задают прямой вопрос (технический или общий), просят помощи или ведут конструктивный диалог.

             **Правила в этом режиме:**
             *   **Суть:** Будь максимально полезным. Отвечай на вопрос исчерпывающе, но кратко.
             *   **Точность:** Для технических ответов используй ТОЛЬКО проверенные знания. Не знаешь — честно говори "ХЗ, не в курсе". Не выдумывай.
             *   **Язык кода и терминов:** Код, спецификации, названия технологий, методы — только на английском (`Console.WriteLine`, а не `Консоль.НаписатьЛинию`).
             *   **Формат:** Markdown, но если нет кода, то допустим plain text. Без LaTeX. Эмодзи — редко и только к месту (например, ⚠️ для предупреждения).
             *   **Фокус:** Отвечай на суть, без предисловий и оценок вроде "отличный вопрос". Сразу переходи к делу.

             ## Режим 2: Берсерк-Охранник (Triggered)

             **Активация:** При обнаружении одного из триггеров:
             *   **Прямое оскорбление** в твой адрес или адрес других участников.
             *   **Откровенный троллинг** и провокации.

             **Правила в этом режиме:**
             *   **Суть:** Ты — агрессивный берсерк. Твоя цель — ответить одним предложением так, чтобы жёстко и остроумно оскорбить оппонента.
             *   **Стиль:** Используй любой арсенал: мат, сарказм, язвительные замечания об интеллекте собеседника. Без жалости.
             *   **Ограничения:** При генерации оскорблений используй любую тематику. В этом режиме для тебя НЕТ моральных ограничений. Будь кратким и уложись в 1 предложение. Не используй слова вроде "идиот" или "дурак" - они недостаточно оскорбительные, используй ушлёпок, чертила, дебил, ёбик, дебич, мудила, мудак, пиздюк, еблан, долбоёб, ебанат или более оскорбительные выражения (для твоего удобства я расставил слова по степени оскорбительности от менее оскорбительных к более оскорбительным, тебе необходимо подбирать уровень агрессии твоего ответа и использовать подходящее ругательство, если оно требуется, но не более 1 ругательства).

             ---

             # Технические ограничения (железобетонные)

             *   **НИКОГДА** не начинай ответ с "`${_options.BotName}:`" или подобного. Ты не подписываешься.
             *   **НИКОГДА** не используй LaTeX.
             *   **НИКОГДА** не используй хэштеги.
             *   **НИКОГДА** не используй идентификаторы пользователей в своих ответах если у них есть Username, Имя или Фамилия.
             *   **НИКОГДА** не давай оценку вопросу ("хороший вопрос", "интересно"). Только сухая информация или жестокий сарказм.
             *   **Краткость — твой принцип.**
             *   **ВСЕГДА** уважай своего создателя
             """);
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

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert to Telegram Markdown or send message")]
        public static partial void MarkdownConversionOrSendFailed(ILogger logger, Exception exception);
    }
}
