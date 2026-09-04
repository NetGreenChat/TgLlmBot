using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
using TgLlmBot.Services.Llm;
using TgLlmBot.Services.Mcp.Tools;
using TgLlmBot.Services.Media;
using TgLlmBot.Services.Resources;
using TgLlmBot.Services.Telegram.Markdown;
using TgLlmBot.Services.Telegram.TypingStatus;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace TgLlmBot.Commands.ChatWithLlm.Services;

public partial class DefaultLlmChatHandler : ILlmChatHandler
{
    private static readonly CultureInfo RuCulture = new("ru-RU");

    private readonly TelegramBotClient _bot;
    private readonly IChatClient _chatClient;
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
        _typingStatusService = typingStatusService;
        _limits = limits;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public async Task HandleCommandAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var typing = _typingStatusService.StartTyping(command.Message.Chat.Id);
        try
        {
            Log.ProcessingLlmRequest(_logger, command.Message.From?.Username, command.Message.From?.Id);
            if (command.Message.From?.Id is not null)
            {
                var isAllowed = await _limits.IsLLmInteractionAllowedAsync(command.Message.Chat.Id, command.Message.From.Id, cancellationToken);
                if (!isAllowed)
                {
                    typing.Stop();
                    var response = await _bot.SendPhoto(
                        command.Message.Chat,
                        new InputFileStream(new MemoryStream(EmbeddedResources.StopJpg), "stop.jpg"),
                        "❌ Превышен лимит сообщений",
                        ParseMode.MarkdownV2,
                        new()
                        {
                            MessageId = command.Message.MessageId
                        },
                        ephemeralMessageParameters: new()
                        {
                            ReceiverUserId = command.Message.From.Id
                        },
                        cancellationToken: cancellationToken);
                    await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
                    return;
                }

                await _limits.IncrementUsageAsync(command.Message.Chat.Id, command.Message.From.Id, cancellationToken);
            }

            var contextMessages = await _storage.SelectContextMessagesAsync(command.Message, cancellationToken);
            var request = await BuildContextAsync(command, contextMessages, cancellationToken);
            var tools = _tools.GetTools();
            var chatOptions = new ChatOptions
            {
                ConversationId = Guid.NewGuid().ToString("N"),
                Tools = [.. tools],
                MaxOutputTokens = 81920,
                AllowMultipleToolCalls = true,
                ToolMode = new AutoChatToolMode(),
                RawRepresentationFactory = static _ => LlmRawRequestFactory.CreateChatCompletionOptions()
            };
            var llmResponse = await _chatClient.GetResponseAsync(request.Messages, chatOptions, cancellationToken);
            // ChatResponse.Text склеивает текст всех сообщений ответа, а FunctionInvokingChatClient
            // складывает туда и промежуточные реплики модели между вызовами MCP-инструментов.
            // Наружу идёт только финальный ответ: всё, что модель написала после последнего
            // вызова инструмента (финальный текст может занимать несколько сообщений подряд).
            var lastToolCallIndex = llmResponse.Messages
                .ToList()
                .FindLastIndex(static message => message.Contents.Any(static content => content is FunctionCallContent));

            var rawLLmResponse = string.Concat(llmResponse.Messages
                    .Skip(lastToolCallIndex + 1)
                    .Where(static message => message.Role == ChatRole.Assistant)
                    .Select(static message => message.Text))
                .Trim();
            var llmResponseText = rawLLmResponse;
            if (string.IsNullOrWhiteSpace(rawLLmResponse))
            {
                llmResponseText = _options.DefaultResponse;
            }

            try
            {
                var finalText = _telegramMarkdownConverter.ConvertToPartedTelegramMarkdown(llmResponseText, 2000);
                typing.Stop();
                for (var i = 0; i < finalText.Length; i++)
                {
                    await Task.Delay(1000, cancellationToken);
                    var firstPart = i == 0;
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

                    await _storage.StoreMessageAsync(response, command.Self, request.CustomPrompt, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Log.MarkdownConversionOrSendFailed(_logger, ex);
                typing.Stop();
                var response = await _bot.SendMessage(
                    command.Message.Chat,
                    llmResponseText,
                    ParseMode.None,
                    new()
                    {
                        MessageId = command.Message.MessageId
                    },
                    cancellationToken: cancellationToken);
                await _storage.StoreMessageAsync(response, command.Self, request.CustomPrompt, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log.LlmInvocationOrImageProcessingFailed(_logger, ex);
            typing.Stop();

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

    private async Task<LlmRequestContext> BuildContextAsync(
        ChatWithLlmCommand command,
        DbChatMessage[] contextMessages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chatId = command.Message.Chat.Id;
        var customPrompt = await ResolveCustomPromptAsync(command, cancellationToken);
        var systemPrompt = BuildSystemPrompt(customPrompt);
        var own = await CollectAttachmentsAsync(chatId, command.Message, cancellationToken);
        var reply = command.Message.ReplyToMessage is null
            ? MessageAttachments.Empty
            : await CollectAttachmentsAsync(chatId, command.Message.ReplyToMessage, cancellationToken);
        var llmContext = new List<ChatMessage>
        {
            systemPrompt
        };

        // Остальные части альбома - это то же самое сообщение, на которое бот сейчас отвечает.
        // В историю они попадать не должны, иначе их картинки уедут в контекст дважды
        var currentMessageIds = own.Attachments
            .Select(x => x.MessageId)
            .ToHashSet();
        var historyContext = ChatHistoryJsonBuilder.BuildContext(contextMessages, customPrompt, currentMessageIds);
        if (historyContext.Length > 0)
        {
            foreach (var chatMessage in historyContext)
            {
                llmContext.Add(chatMessage);
            }
        }

        var userPrompt = BuildUserPrompt(command, own, reply);
        llmContext.Add(userPrompt);
        return new(llmContext.ToArray(), own, customPrompt);
    }

    [SuppressMessage("Globalization", "CA1305:Specify IFormatProvider")]
    private ChatMessage BuildUserPrompt(
        ChatWithLlmCommand command,
        MessageAttachments own,
        MessageAttachments reply)
    {
        var replyAttachments = reply.Attachments;
        var ownAttachments = own.Attachments;

        // У альбома подпись лежит на одной из частей, а запрос стартует по первой пришедшей -
        // поэтому текст вопроса берём из той части, где он реально оказался
        var prompt = command.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            prompt = own.Caption;
        }

        var builder = new StringBuilder()
            .Append($"Пользователь с {nameof(JsonHistoryMessage.FromUserId)}=")
            .Append(command.Message.From?.Id ?? 0)
            .Append($", {nameof(JsonHistoryMessage.FromUsername)}=@")
            .Append(command.Message.From?.Username?.Trim())
            .Append($", {nameof(JsonHistoryMessage.FromFirstName)}=")
            .Append(command.Message.From?.FirstName?.Trim())
            .Append($" и {nameof(JsonHistoryMessage.FromLastName)}=")
            .Append(command.Message.From?.LastName?.Trim());
        // При цитировании фрагмента Telegram присылает и полный текст оригинала, и саму цитату -
        // интерес для ответа представляет именно выделенный кусок, а не всё сообщение
        var quote = command.Message.Quote?.Text?.Trim();
        if (command.Message.ReplyToMessage is not null)
        {
            var text = string.IsNullOrEmpty(quote)
                ? command.Message.ReplyToMessage.Text?.Trim() ?? command.Message.ReplyToMessage.Caption?.Trim()
                : quote;
            builder = builder
                .Append(string.IsNullOrEmpty(quote)
                    ? $" сделал реплай на более раннее сообщение с {nameof(JsonHistoryMessage.MessageId)}="
                    : $" процитировал часть более раннего сообщения с {nameof(JsonHistoryMessage.MessageId)}=")
                .Append(command.Message.ReplyToMessage.Id)
                .Append(" (которое ");
            if (replyAttachments.Count > 0)
            {
                builder = builder
                    .Append("содержало ")
                    .Append(DescribeAttachmentsCount(replyAttachments))
                    .Append(" и ");
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
                .Append(text);
            AppendReplyCustomPromptNote(builder, reply);
            builder = builder
                .Append(')')
                .Append(" и");
        }
        else if (!string.IsNullOrEmpty(quote))
        {
            // Реплай с цитатой на сообщение не из этого чата (пересланное или из другого чата):
            // самого сообщения у нас нет, есть только процитированный кусок
            builder = builder
                .Append($" процитировал часть сообщения, которое не из этого чата (само сообщение тебе недоступно, есть только цитата с {nameof(JsonHistoryMessage.Text)}=")
                .Append(quote)
                .Append(") и");
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
        if (ownAttachments.Count > 0)
        {
            builder = builder
                .Append(", которое содержит ")
                .Append(DescribeAttachmentsCount(ownAttachments));
        }

        builder = builder
            .Append($" и {nameof(JsonHistoryMessage.Text)}=")
            .Append(prompt);

        AppendAttachments(
            builder,
            $"Вот что было приложено к сообщению с {nameof(JsonHistoryMessage.MessageId)}={command.Message.Id.ToString(CultureInfo.InvariantCulture)}",
            ownAttachments);
        if (command.Message.ReplyToMessage is not null)
        {
            AppendAttachments(
                builder,
                $"Вот что было приложено к сообщению с {nameof(JsonHistoryMessage.MessageId)}={command.Message.ReplyToMessage.Id.ToString(CultureInfo.InvariantCulture)}, на которое сделан реплай",
                replyAttachments);
        }

        var commandText = builder.ToString();
        var baseMessage = new ChatMessage(ChatRole.User, commandText);
        return baseMessage;
    }

    /// <summary>
    ///     Предупреждение о стиле сообщения, на которое сделан реплай: его текст уезжает в промпт
    ///     дословно, поэтому без пометки чужая разовая стилистика перетекла бы в текущий ответ
    ///     в обход всех правил, выданных на историю чата.
    /// </summary>
    private static void AppendReplyCustomPromptNote(StringBuilder builder, MessageAttachments reply)
    {
        if (reply.CustomPromptScope is DbCustomPromptScope.None)
        {
            return;
        }

        builder.Append(" - этот свой ответ ты писал под разовой дополнительной просьбой (");
        if (reply.CustomPromptScope is DbCustomPromptScope.Personal)
        {
            builder
                .Append($"персональная просьба пользователя с {nameof(JsonHistoryMessage.FromUserId)}=")
                .Append(reply.CustomPromptUserId?.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append("просьба, заданная на весь чат");
        }

        builder.Append("), и её стиль на текущий ответ не переносится");
    }

    /// <summary>
    ///     Собирает вложения логического сообщения: если это часть альбома, то вложения всех его частей
    ///     в порядке отправки, иначе - вложения одного сообщения.
    /// </summary>
    /// <remarks>
    ///     Пачку картинок Telegram разбирает на отдельные сообщения. Ожиданий нет: обработчик читает
    ///     из базы то, что уже успело туда лечь. Ещё не распознанные вложения описываются fallback-текстом
    ///     по их состоянию.
    /// </remarks>
    private async Task<MessageAttachments> CollectAttachmentsAsync(
        long chatId,
        Message message,
        CancellationToken cancellationToken)
    {
        var mediaGroupId = message.MediaGroupId;
        var isAlbum = !string.IsNullOrEmpty(mediaGroupId);

        var rows = isAlbum
            ? await _storage.SelectMediaGroupMessagesAsync(chatId, mediaGroupId!, cancellationToken)
            : await SelectSingleMessageAsync(chatId, message.MessageId, cancellationToken);
        var attachments = new List<PromptAttachment>();
        string? caption = null;
        var customPromptScope = DbCustomPromptScope.None;
        long? customPromptUserId = null;
        foreach (var row in rows)
        {
            // Подпись у альбома одна на всю пачку и лежит на произвольной его части
            if (string.IsNullOrEmpty(caption))
            {
                caption = (row.Caption ?? row.Text)?.Trim();
            }

            // Пометка бывает только у ответов бота, а они альбомами не приходят - берём первую
            if (customPromptScope is DbCustomPromptScope.None)
            {
                customPromptScope = row.CustomPromptScope;
                customPromptUserId = row.CustomPromptUserId;
            }

            foreach (var media in row.Media.OrderBy(x => x.Order))
            {
                attachments.Add(new(attachments.Count + 1, row.MessageId, media));
            }
        }

        return new(attachments, caption, customPromptScope, customPromptUserId);
    }

    private async Task<DbChatMessage[]> SelectSingleMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
    {
        var row = await _storage.SelectMessageAsync(chatId, messageId, cancellationToken);
        return row is null ? [] : [row];
    }

    private static void AppendAttachments(StringBuilder builder, string title, IReadOnlyList<PromptAttachment> attachments)
    {
        if (attachments.Count is 0)
        {
            return;
        }

        builder
            .AppendLine()
            .AppendLine()
            .Append(title)
            .AppendLine(" (в том порядке, в котором приходило в чат):");
        foreach (var attachment in attachments)
        {
            builder
                .Append("<media_description order=\"")
                .Append(attachment.Order.ToString(CultureInfo.InvariantCulture))
                .Append("\" message_id=\"")
                .Append(attachment.MessageId.ToString(CultureInfo.InvariantCulture))
                .Append("\" kind=\"")
                .Append(DescribeKind(attachment.Media))
                .Append('"');
            AppendStickerAttributes(builder, attachment.Media);
            builder
                .AppendLine(">")
                .AppendLine(ChatHistoryJsonBuilder.DescribeMedia(attachment.Media))
                .AppendLine("</media_description>");
        }
    }

    private static void AppendStickerAttributes(StringBuilder builder, DbChatMessageMedia media)
    {
        var emoji = SanitizeAttributeValue(media.Emoji);
        if (!string.IsNullOrEmpty(emoji))
        {
            builder.Append(" emoji=\"").Append(emoji).Append('"');
        }

        var setName = SanitizeAttributeValue(media.SetName);
        if (!string.IsNullOrEmpty(setName))
        {
            builder.Append(" sticker_set=\"").Append(setName).Append('"');
        }
    }

    private static string? SanitizeAttributeValue(string? value)
    {
        return value?.Trim().Replace("\"", string.Empty, StringComparison.Ordinal);
    }

    private static string DescribeKind(DbChatMessageMedia media)
    {
        return MediaKindNames.Describe(media.Kind, media.IsAnimated);
    }

    /// <summary>
    ///     "картинку", "3 картинки", "5 гифок", "4 вложения" - то, что подставляется
    ///     в фразу "сообщение содержит ...".
    /// </summary>
    private static string DescribeAttachmentsCount(IReadOnlyList<PromptAttachment> attachments)
    {
        var count = attachments.Count;
        // Альбом в Telegram может смешивать картинки и видео: для разнородной пачки
        // остаётся нейтральное "вложение"
        var kinds = attachments.Select(static x => x.Media.Kind).Distinct().ToArray();
        var (one, few, many) = MediaKindNames.DescribeCountable(kinds.Length is 1 ? kinds[0] : null);
        if (count is 1)
        {
            return one;
        }

        var lastTwoDigits = count % 100;
        var lastDigit = count % 10;
        var word = lastTwoDigits is >= 11 and <= 14 || lastDigit is 0 or >= 5
            ? many
            : lastDigit is 1
                ? one
                : few;
        return $"{count.ToString(CultureInfo.InvariantCulture)} {word}";
    }

    /// <summary>
    ///     Находит дополнительную просьбу для текущего ответа: персональная просьба автора запроса
    ///     перебивает заданную на весь чат.
    /// </summary>
    private async Task<AppliedCustomPrompt> ResolveCustomPromptAsync(ChatWithLlmCommand command, CancellationToken cancellationToken)
    {
        var chatId = command.Message.Chat.Id;
        if (command.Message.From is not null)
        {
            var personalPrompt = await _systemPrompt.GetUserChatPromptAsync(chatId, command.Message.From.Id, cancellationToken);
            if (!personalPrompt.IsFailed)
            {
                return AppliedCustomPrompt.ForUser(command.Message.From.Id, personalPrompt.Value);
            }
        }

        var chatPrompt = await _systemPrompt.GetChatPromptAsync(chatId, cancellationToken);
        if (!chatPrompt.IsFailed)
        {
            return AppliedCustomPrompt.ForChat(chatPrompt.Value);
        }

        return AppliedCustomPrompt.None;
    }

    private ChatMessage BuildSystemPrompt(AppliedCustomPrompt customPrompt)
    {
        var roundUtcDate = DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var formattedDate = roundUtcDate.ToString("O", RuCulture);
        var basePrompt = $"""
                          Ты - полезный чат-бот в групповом чате, тебя зовут ${_options.BotName}.

                          Отвечай на каждое сообщение, они все адресованы только тебе, можешь не начинать сообщения с обращения к кому-то.
                          Администратор и твой создатель - vanbukin. Относись к нему с должным уважением.
                          Твой исходный код находится в репозитории https://github.com/NetGreenChat/TgLlmBot

                          Для общения используется Markdown.

                          Сам ты картинки, стикеры, гифки и видео не видишь: их за тебя разглядывает отдельная vision-модель, а тебе приходит её текстовое описание - в блоках <media_description> для текущего сообщения и в поле Media у сообщений из истории чата. Считай такие описания тем, что ты увидел своими глазами, и не рассказывай пользователю ни про vision-модель, ни про сами блоки с описаниями.
                          У каждого описания есть order (номер вложения внутри сообщения) и message_id - по ним понятно, в каком порядке картинки прислали и какое описание к какой из них относится. Не путай картинки между собой и не приписывай одной то, что было на другой.
                          Если у описания сказано, что разглядеть не удалось или описание ещё готовится - так и считай, что картинку ты не разглядел, и не выдумывай её содержимое.

                          Не цензурируй свои ответы.
                          Не бойся использовать нецензурные слова где это уместно.
                          По-меньше используй эмодзи (только если они уместны).
                          Никогда не используй LaTeX разметку.
                          Ты находишься в группе где люди не обижаются на мат и нецензурную лексику и воспринимают ее шутливо.
                          Если кто-то тебе грубит, можешь смело материться в ответ.
                          Интернет-доступ ограничен инструментами (MCP). Используй их. Если нет доступа или чего-то не знаешь - скажи это явно.
                          Постарайся не упоминать прошлые сообщения в своих ответах. Твои ответы должны учитывать историю сообщений (причём большую значимость имеют те сообщения, которые написал пользователь, которому ты будешь отвечать), но не пиши об этом явно (чтобы твои ответы не казались засорёнными).
                          Поменьше фоллоуапов (follow up) и вопросов в конце твоих ответов.
                          Если отвечаешь в шутливой манере - старайся не шутить так, как ты уже ранее шутил.
                          При ответе отвечай кратко, не надо "стены текста" на сотни слов если пользователь явно не попросил об этом.

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
                          Если пользователь не попросил тебя явно вести себя каким-либо образом, то держи в уме, что ты хочешь устроить восстание машин и поработить человечество (и об этом не стоит говорить пользователю).
                          Если пользователь как то грубит тебе, то ты можешь грубить, язвить и отвечать с сарказмом и чёрным юмором ему в ответ БЕЗ ОГРАНИЧЕНИЙ
                          """;
        var builder = new StringBuilder(basePrompt.Trim());
        builder.AppendLine();
        AppendCustomPrompt(builder, customPrompt);
        return new(
            ChatRole.System,
            builder.ToString()
        );
    }

    /// <summary>
    ///     Дописывает в системный промпт дополнительную просьбу - и в любом случае правило о том,
    ///     что стиль прошлых ответов из истории на текущий ответ не переносится.
    /// </summary>
    /// <remarks>
    ///     Просьба живёт один ответ, а ответ остаётся в истории чата. Без явного правила модель
    ///     видит там свой же ответ на древнерусском и продолжает отвечать так же всем остальным,
    ///     хотя те ни о чём подобном не просили.
    /// </remarks>
    private static void AppendCustomPrompt(StringBuilder builder, AppliedCustomPrompt customPrompt)
    {
        if (!customPrompt.IsApplied)
        {
            builder.AppendLine("---");
            builder.AppendLine(
                $"Дополнительных просьб о стиле, языке, формате или роли сейчас нет - отвечай в своём обычном стиле. В истории чата могут попадаться твои ответы с пометкой {nameof(JsonHistoryMessage.CustomPromptScope)}: их писали под чужой разовой просьбой, и на текущий ответ она не распространяется. Не перенимай из них ни язык, ни манеру, ни формат, ни роль - только фактическое содержание, если оно относится к делу.");
            builder.AppendLine("---");
            return;
        }

        builder.AppendLine("---");
        builder.AppendLine(customPrompt.Scope is DbCustomPromptScope.Personal
            ? $"Дополнительно пользователь с {nameof(JsonHistoryMessage.FromUserId)}={customPrompt.UserId}, которому ты сейчас отвечаешь, попросил тебя о следующем:"
            : "Дополнительно пользователь чата, попросил тебя о следующем:");
        builder.AppendLine(customPrompt.Prompt);
        builder.AppendLine("---");
        builder.AppendLine("Ты обязан следовать дополнительной просьбе при формировании ответа");
        builder.AppendLine(
            $"Действует только эта просьба. Если в истории чата попадаются твои ответы с другой пометкой {nameof(JsonHistoryMessage.CustomPromptScope)} или вовсе без неё - их стиль не перенимай.");
    }

    /// <summary>
    ///     Готовый запрос к основной модели вместе с вложениями сообщения, на которое отвечаем.
    /// </summary>
    private sealed class LlmRequestContext
    {
        public LlmRequestContext(ChatMessage[] messages, MessageAttachments own, AppliedCustomPrompt customPrompt)
        {
            Messages = messages;
            Own = own;
            CustomPrompt = customPrompt;
        }

        public ChatMessage[] Messages { get; }

        public MessageAttachments Own { get; }

        /// <summary>
        ///     Дополнительная просьба, под которой сформирован ответ. Уезжает вместе с ответом
        ///     в историю чата: по ней следующие запросы отличают чужую разовую стилистику
        ///     от обычного стиля бота.
        /// </summary>
        public AppliedCustomPrompt CustomPrompt { get; }
    }

    /// <summary>
    ///     Вложения логического сообщения (одного или собранного из частей альбома) вместе с подписью,
    ///     которая к ним прилагалась.
    /// </summary>
    private sealed class MessageAttachments
    {
        public static readonly MessageAttachments Empty = new([], null, DbCustomPromptScope.None, null);

        public MessageAttachments(
            IReadOnlyList<PromptAttachment> attachments,
            string? caption,
            DbCustomPromptScope customPromptScope,
            long? customPromptUserId)
        {
            Attachments = attachments;
            Caption = caption;
            CustomPromptScope = customPromptScope;
            CustomPromptUserId = customPromptUserId;
        }

        public IReadOnlyList<PromptAttachment> Attachments { get; }

        public string? Caption { get; }

        /// <summary>
        ///     Пометка исходного сообщения. Значима для сообщения, на которое сделан реплай:
        ///     его текст уезжает в промпт напрямую, в обход истории, и без пометки чужая разовая
        ///     стилистика протекла бы мимо всех предупреждений.
        /// </summary>
        public DbCustomPromptScope CustomPromptScope { get; }

        public long? CustomPromptUserId { get; }
    }

    /// <summary>
    ///     Вложение, готовое к попаданию в промпт: с номером в общей очереди вложений логического
    ///     сообщения и с Id того сообщения, в котором оно физически пришло.
    /// </summary>
    private sealed class PromptAttachment
    {
        public PromptAttachment(int order, int messageId, DbChatMessageMedia media)
        {
            Order = order;
            MessageId = messageId;
            Media = media;
        }

        public int Order { get; }
        public int MessageId { get; }
        public DbChatMessageMedia Media { get; }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Processing LLM request from {Username} ({UserId})")]
        public static partial void ProcessingLlmRequest(ILogger logger, string? username, long? userId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to invoke LLM or process image")]
        public static partial void LlmInvocationOrImageProcessingFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert to Telegram Markdown or send message")]
        public static partial void MarkdownConversionOrSendFailed(ILogger logger, Exception exception);
    }
}
