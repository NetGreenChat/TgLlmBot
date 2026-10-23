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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.Commands.Model;
using TgLlmBot.Configuration.TypedConfiguration.Llm;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.DataAccess.Limits;
using TgLlmBot.Services.DataAccess.SystemPrompts;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Llm;
using TgLlmBot.Services.Llm.Multimodal;
using TgLlmBot.Services.Mcp.Tools;
using TgLlmBot.Services.Media;
using TgLlmBot.Services.Resources;
using TgLlmBot.Services.Telegram.Markdown;
using TgLlmBot.Services.Telegram.TypingStatus;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace TgLlmBot.Commands.ChatWithLlm.Services;

public partial class DefaultLlmChatHandler : ILlmChatHandler
{
    /// <summary>
    ///     Суммарный потолок на размер нативно приложенных вложений в одном запросе - в символах
    ///     data-url. Альбомы бывают большими, а памяти у сервера модели не бесконечно: что в
    ///     бюджет не влезло, уезжает текстовым описанием, как вложения из глубокой истории.
    /// </summary>
    private const long EmbeddedMediaBudgetChars = 64 * 1024 * 1024;

    /// <summary>
    ///     Общее для всех вариантов капабилити правило: вложения не путать между собой.
    /// </summary>
    private const string MediaConfusionRule =
        "Не путай вложения между собой и не приписывай одному то, что было на другом: строка каждого вложения стоит под заголовком того сообщения, к которому оно относится, и нумерована по порядку отправки. Если в описании сказано, что разглядеть не удалось или описание ещё готовится, - считай, что вложение ты не разглядел, и не выдумывай его содержимое.";

    private readonly TelegramBotClient _bot;
    private readonly IChatClient _chatClient;
    private readonly LlmCapabilitiesConfiguration _capabilities;
    private readonly AIFunction _modelInfoTool;
    private readonly ILlmLimitsService _limits;
    private readonly ILogger<DefaultLlmChatHandler> _logger;
    private readonly IMediaGroupTracker _mediaGroupTracker;
    private readonly DefaultLlmChatHandlerOptions _options;
    private readonly IMediaPreparer _preparer;
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
        LlmCapabilitiesConfiguration capabilities,
        [FromKeyedServices(ModelInfoFormatter.ToolServiceKey)] AIFunction modelInfoTool,
        IMediaPreparer preparer,
        IMediaGroupTracker mediaGroupTracker,
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
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(modelInfoTool);
        ArgumentNullException.ThrowIfNull(preparer);
        ArgumentNullException.ThrowIfNull(mediaGroupTracker);
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
        _capabilities = capabilities;
        _modelInfoTool = modelInfoTool;
        _preparer = preparer;
        _mediaGroupTracker = mediaGroupTracker;
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
                Tools = [.. tools, _modelInfoTool],
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

            var finalResponseText = string.Concat(llmResponse.Messages
                .Skip(lastToolCallIndex + 1)
                .Where(static message => message.Role == ChatRole.Assistant)
                .Select(static message => message.Text));
            // Служебную разметку истории (заголовок, строку реплая, разделитель) модель
            // воспроизводить не должна, но на всякий случай из начала ответа она вычищается
            var rawLLmResponse = ChatTranscriptFormat.StripLeakedMarkup(finalResponseText).Trim();
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

    /// <summary>
    ///     Собирает запрос к модели: системный промпт, историю чата чередующимися репликами
    ///     и последнюю user-реплику с сообщением, на которое надо ответить.
    /// </summary>
    private async Task<LlmRequestContext> BuildContextAsync(
        ChatWithLlmCommand command,
        DbChatMessage[] contextMessages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chatId = command.Message.Chat.Id;
        var customPrompt = await ResolveCustomPromptAsync(command, cancellationToken);
        var own = await CollectAttachmentsAsync(chatId, command.Message, waitForAlbum: true, cancellationToken);
        var reply = command.Message.ReplyToMessage is null
            ? MessageAttachments.Empty
            : await CollectAttachmentsAsync(chatId, command.Message.ReplyToMessage, waitForAlbum: false, cancellationToken);

        // Само сообщение и остальные части его альбома в историю не попадают: их текст и
        // вложения уезжают в запрос отдельно, иначе картинки оказались бы в контексте дважды
        var currentMessageIds = own.Parts
            .Select(static x => x.MessageId)
            .Append(command.Message.MessageId)
            .ToHashSet();
        var quoted = await LoadQuotedMessagesAsync(chatId, contextMessages, currentMessageIds, cancellationToken);

        // Подписи участников - одни и те же во всём запросе; автор текущего сообщения
        // регистрируется последним, чтобы его подпись была самой свежей
        var authors = ChatAuthorDirectory.FromMessages(contextMessages.Concat(quoted).Concat(reply.Parts));
        var currentAuthor = DescribeAuthor(command.Message.From, command.Message.SenderChat);
        if (command.Message.From is not null)
        {
            authors.Register(command.Message.From.Id, currentAuthor);
        }

        // Вложения своего сообщения и реплая модель видит сама - они уезжают в запрос
        // медиа-частями, а не текстовым описанием
        var embedded = await PrepareEmbeddedMediaAsync(own, reply, cancellationToken);
        var currentMessageBlock = BuildCurrentMessageBlock(command, own, reply, embedded, authors, currentAuthor);

        // Медиа-части идут перед текстом: маркеры подменит на image_url/video_url MultimodalChatClient,
        // и модель увидит вложения раньше, чем текст о них
        var currentMessageMedia = embedded
            .Select(static x => (AIContent) new AttachedMediaContent(x.Media))
            .ToArray();
        var conversation = ChatTranscriptBuilder.BuildConversation(new(
            contextMessages,
            quoted,
            currentMessageIds,
            authors,
            currentMessageBlock,
            currentMessageMedia));
        var messages = new List<ChatMessage>(conversation.Length + 1)
        {
            BuildSystemPrompt(command, customPrompt, currentAuthor)
        };
        messages.AddRange(conversation);
        return new(messages.ToArray(), customPrompt);
    }

    /// <summary>
    ///     Догружает сообщения, на которые ссылаются реплаи из истории, но которые в окно истории
    ///     не попали: без них строка реплая не смогла бы процитировать, на что отвечали.
    /// </summary>
    private async Task<DbChatMessage[]> LoadQuotedMessagesAsync(
        long chatId,
        DbChatMessage[] contextMessages,
        HashSet<int> currentMessageIds,
        CancellationToken cancellationToken)
    {
        var known = contextMessages
            .Select(static x => x.MessageId)
            .ToHashSet();
        var missing = contextMessages
            .Where(x => !currentMessageIds.Contains(x.MessageId))
            .Select(static x => x.ReplyToMessageId)
            .Where(x => x is { } id && !known.Contains(id) && !currentMessageIds.Contains(id))
            .Select(static x => x!.Value)
            .Distinct()
            .ToArray();
        if (missing.Length is 0)
        {
            return [];
        }

        return await _storage.SelectMessagesAsync(chatId, missing, cancellationToken);
    }

    /// <summary>
    ///     Готовит вложения своего сообщения и реплая к нативному показу модели: скачивает,
    ///     опознаёт формат и проверяет по капабилитям, что модель такое видит.
    /// </summary>
    /// <remarks>
    ///     Что вложить не вышло - не поддерживается моделью, не скачалось или не влезло в бюджет -
    ///     уезжает текстовым описанием, как вложения из истории. Вложения реплая встраиваются
    ///     наравне со своими: спросить "что тут на картинке" чаще всего приходят реплаем.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task<List<EmbeddedMedia>> PrepareEmbeddedMediaAsync(
        MessageAttachments own,
        MessageAttachments reply,
        CancellationToken cancellationToken)
    {
        var embedded = new List<EmbeddedMedia>();
        if (!_capabilities.Image && !_capabilities.Video)
        {
            return embedded;
        }

        var budget = EmbeddedMediaBudgetChars;
        foreach (var attachment in own.Attachments.Concat(reply.Attachments))
        {
            if (budget <= 0)
            {
                break;
            }

            var media = attachment.Media;
            if (!media.HasShowableFile)
            {
                continue;
            }

            try
            {
                var prepared = await _preparer.PrepareAsync(media, cancellationToken);
                if (prepared.IsFailed)
                {
                    continue;
                }

                var cost = prepared.Value.DataUrl.Length;
                if (cost > budget)
                {
                    continue;
                }

                budget -= cost;
                embedded.Add(new(attachment, prepared.Value));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Одно вложение не готовится к показу - не повод отказываться от остальных
                Log.MediaPreparationFailed(_logger, ex, media.Kind);
            }
        }

        if (embedded.Count > 0)
        {
            Log.EmbeddedMediaPrepared(_logger, embedded.Count, EmbeddedMediaBudgetChars - budget);
        }

        return embedded;
    }

    /// <summary>
    ///     Текущее сообщение в том же формате, что и сообщения истории: заголовок, строка реплая
    ///     (с вложениями того сообщения, на которое отвечают), строки своих вложений, текст.
    /// </summary>
    /// <remarks>
    ///     Вложение, которое приложено к запросу нативно, помечено номером медиа-части; остальное
    ///     описано текстом, как вложения из истории. Если капабилити модели вложение не покрывают,
    ///     в описании прямо сказано, что распознавание не поддерживается: иначе модель решает,
    ///     что разглядела его по статическому кадру.
    /// </remarks>
    private string BuildCurrentMessageBlock(
        ChatWithLlmCommand command,
        MessageAttachments own,
        MessageAttachments reply,
        IReadOnlyList<EmbeddedMedia> embedded,
        ChatAuthorDirectory authors,
        string currentAuthor)
    {
        var message = command.Message;

        // Альбом - это N сообщений с общим MediaGroupId, и в заголовке перечисляются все его части
        int[] messageIds = own.Parts.Count > 0
            ? own.Parts.Select(static x => x.MessageId).Order().ToArray()
            : [message.MessageId];
        var builder = new StringBuilder();
        builder.Append(ChatTranscriptFormat.FormatHeader(messageIds, message.Date, currentAuthor, own.Attachments.Count));

        if (message.ReplyToMessage is { } replied)
        {
            builder.AppendLine().Append(BuildReplyLine(command, replied, reply, authors));
            if (reply.Attachments.Count > 0)
            {
                builder
                    .AppendLine()
                    .Append("  вложения сообщения #")
                    .Append(replied.MessageId.ToString(CultureInfo.InvariantCulture))
                    .Append(':');
                foreach (var attachment in reply.Attachments)
                {
                    builder.AppendLine().Append("  ").Append(DescribeAttachment(attachment, reply.Attachments.Count, embedded));
                }
            }
        }

        foreach (var attachment in own.Attachments)
        {
            builder.AppendLine().Append(DescribeAttachment(attachment, own.Attachments.Count, embedded));
        }

        // У альбома подпись лежит на одной из частей, а запрос стартует по первой пришедшей -
        // поэтому текст вопроса берём из той части, где он реально оказался
        var prompt = command.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            prompt = own.Caption;
        }

        if (!string.IsNullOrEmpty(prompt))
        {
            builder.AppendLine().Append(prompt);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Строка реплая текущего сообщения. Текст и автор берутся из того, что прислал Telegram,
    ///     вложения и пометка о разовой просьбе - из сохранённой истории.
    /// </summary>
    private static string BuildReplyLine(
        ChatWithLlmCommand command,
        Message replied,
        MessageAttachments reply,
        ChatAuthorDirectory authors)
    {
        var isBotMessage = replied.From?.Id == command.Self.Id || reply.Parts.Any(static x => x.IsLlmReplyToMessage);
        var text = reply.Caption ?? (replied.Text ?? replied.Caption)?.Trim();
        var markers = reply.Attachments.Select(static x => ChatTranscriptFormat.FormatMediaMarker(x.Media));
        var excerpt = ChatTranscriptFormat.Excerpt(string.Join(' ', markers.Append(text).Where(static x => !string.IsNullOrEmpty(x))));
        if (isBotMessage)
        {
            // Цитата уезжает в промпт дословно, в обход истории: без пометки чужая разовая
            // стилистика перетекла бы в текущий ответ мимо всех правил
            var note = reply.CustomPromptScope is DbCustomPromptScope.None
                ? null
                : ChatTranscriptFormat.FormatReplyCustomPromptNote(reply.CustomPromptScope, authors.Resolve(reply.CustomPromptUserId));
            return ChatTranscriptFormat.FormatBotReplyLine(replied.MessageId, excerpt, note);
        }

        var author = replied.From is { } from && authors.TryGet(from.Id, out var label)
            ? label
            : DescribeAuthor(replied.From, replied.SenderChat);
        return ChatTranscriptFormat.FormatReplyLine(replied.MessageId, author, excerpt);
    }

    /// <summary>
    ///     Подпись автора по данным Telegram. Сообщения от имени канала или группы приходят
    ///     без пользователя - тогда подписью служит название чата-отправителя.
    /// </summary>
    private static string DescribeAuthor(User? from, Chat? senderChat)
    {
        return from is not null
            ? ChatTranscriptFormat.FormatAuthor(from.Id, from.FirstName, from.LastName, from.Username)
            : ChatTranscriptFormat.FormatAuthor(null, senderChat?.Title, null, senderChat?.Username);
    }

    /// <summary>
    ///     Строка вложения текущего сообщения или реплая: номер медиа-части, если вложение
    ///     приложено к запросу нативно, иначе текстовое описание.
    /// </summary>
    private string DescribeAttachment(PromptAttachment attachment, int count, IReadOnlyList<EmbeddedMedia> embedded)
    {
        var embeddedIndex = FindEmbeddedIndex(embedded, attachment);
        if (embeddedIndex is { } index)
        {
            return ChatTranscriptFormat.FormatEmbeddedMediaLine(attachment.Media, attachment.Order, count, index + 1);
        }

        var description = _capabilities.Supports(attachment.Media.Kind, attachment.Media.IsAnimated)
            ? ChatTranscriptFormat.DescribeMedia(attachment.Media)
            : LlmCapabilitiesConfiguration.DescribeUnsupported(attachment.Media.Kind, attachment.Media.IsAnimated);
        return ChatTranscriptFormat.FormatMediaLine(attachment.Media, attachment.Order, count, description);
    }

    private static int? FindEmbeddedIndex(IReadOnlyList<EmbeddedMedia> embedded, PromptAttachment attachment)
    {
        for (var i = 0; i < embedded.Count; i++)
        {
            if (ReferenceEquals(embedded[i].Attachment, attachment))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    ///     Собирает вложения логического сообщения: если это часть альбома, то вложения всех его частей
    ///     в порядке отправки, иначе - вложения одного сообщения.
    /// </summary>
    /// <remarks>
    ///     Пачку картинок Telegram разбирает на отдельные сообщения, и запрос стартует по первой
    ///     пришедшей части: свой альбом сначала ждём, иначе ответ уйдёт по половине пачки. Альбом
    ///     реплая не ждём - к моменту реплая он давно приехал целиком.
    /// </remarks>
    private async Task<MessageAttachments> CollectAttachmentsAsync(
        long chatId,
        Message message,
        bool waitForAlbum,
        CancellationToken cancellationToken)
    {
        var mediaGroupId = message.MediaGroupId;
        var isAlbum = !string.IsNullOrEmpty(mediaGroupId);

        DbChatMessage[] rows;
        if (isAlbum)
        {
            if (waitForAlbum)
            {
                await _mediaGroupTracker.WaitForSettleAsync(chatId, mediaGroupId!, cancellationToken);
            }

            rows = await _storage.SelectMediaGroupMessagesAsync(chatId, mediaGroupId!, cancellationToken);
        }
        else
        {
            rows = await SelectSingleMessageAsync(chatId, message.MessageId, cancellationToken);
        }

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

            foreach (var media in row.Media.OrderBy(static x => x.Order))
            {
                attachments.Add(new(attachments.Count + 1, row.MessageId, media));
            }
        }

        return new(rows, attachments, caption, customPromptScope, customPromptUserId);
    }

    private async Task<DbChatMessage[]> SelectSingleMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
    {
        var row = await _storage.SelectMessageAsync(chatId, messageId, cancellationToken);
        return row is null ? [] : [row];
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

    private ChatMessage BuildSystemPrompt(ChatWithLlmCommand command, AppliedCustomPrompt customPrompt, string currentAuthor)
    {
        var formattedDate = ChatTranscriptFormat.FormatTimestamp(_timeProvider.GetUtcNow());
        var botUsername = command.Self.Username?.Trim();
        var identity = string.IsNullOrEmpty(botUsername)
            ? $"Ты - полезный чат-бот в групповом чате, тебя зовут {_options.BotName}."
            : $"Ты - полезный чат-бот в групповом чате, тебя зовут {_options.BotName} (твой username в Telegram - @{botUsername}).";
        var mediaRules = (_capabilities.Image, _capabilities.Video) switch
        {
            (true, true) =>
                $"""
                 Ты - мультимодальный бот: вложения (картинки, стикеры, гифки, видео) нового сообщения и сообщения, на которое оно отвечает, приложены к запросу сами по себе - медиа-частями перед текстом. Ты видишь их непосредственно: разглядывай при ответе. У такого вложения в строке написано «это медиа-часть N этого запроса» - по номеру понятно, какая из приложенных картинок или какое из видео имеется в виду.
                 Вложения из более старых сообщений приложить уже нельзя: вместо них в строке вложения стоит текстовое описание - считай его тем, что ты увидел своими глазами.
                 Не рассказывай пользователю ни про медиа-части, ни про описания - для него ты просто видишь вложения.
                 {MediaConfusionRule}
                 """,
            (false, false) =>
                $"""
                 Сам ты картинки, стикеры, гифки и видео не видишь: вместо каждого вложения тебе приходит его текстовое описание в строке вложения. Считай описание тем, что ты увидел своими глазами, и не рассказывай пользователю про описания - для него ты просто видишь вложения.
                 Если в описании сказано, что распознавание не поддерживается, - скажи об этом пользователю прямо.
                 {MediaConfusionRule}
                 """,
            (true, false) =>
                $"""
                 Ты видишь картинки, но не видео: фото и статические стикеры нового сообщения и сообщения, на которое оно отвечает, приложены к запросу сами по себе - медиа-частями перед текстом, разглядывай их. У такого вложения в строке написано «это медиа-часть N этого запроса» - по номеру понятно, какая из приложенных картинок имеется в виду. Видео, гифки и анимированные стикеры ты не видишь: и в новом сообщении, и в истории они приходят текстовым описанием в строке вложения.
                 Если в описании движущегося вложения сказано, что распознавание не поддерживается, - скажи об этом пользователю прямо, не пытайся разглядеть вложение по одному кадру и не выдумывай его содержимое.
                 Описания считай тем, что ты увидел своими глазами. Не рассказывай пользователю ни про медиа-части, ни про описания - для него ты просто видишь вложения.
                 {MediaConfusionRule}
                 """,
            _ =>
                $"""
                 Ты видишь видео, но не картинки: видео, гифки и анимированные стикеры нового сообщения и сообщения, на которое оно отвечает, приложены к запросу сами по себе - медиа-частями перед текстом, разглядывай их. У такого вложения в строке написано «это медиа-часть N этого запроса» - по номеру понятно, какое из приложенных видео имеется в виду. Фото и статические стикеры ты не видишь: и в новом сообщении, и в истории они приходят текстовым описанием в строке вложения.
                 Если в описании статической картинки сказано, что распознавание не поддерживается, - скажи об этом пользователю прямо и не выдумывай её содержимое.
                 Описания считай тем, что ты увидел своими глазами. Не рассказывай пользователю ни про медиа-части, ни про описания - для него ты просто видишь вложения.
                 {MediaConfusionRule}
                 """
        };
        var transcriptRules =
            $"""
             ## Как устроена переписка
             Это групповой чат, участников много. Переписка приходит тебе в хронологическом порядке: сообщения участников - блоками с ролью user, твои собственные прошлые ответы - отдельными репликами с ролью assistant. Всё, что в assistant-репликах, писал ты сам. Всё, что в user-блоках, писали участники чата - даже если текст там идёт от первого лица.
             Каждое сообщение участника начинается с заголовка:
             #4521 | 2026-10-09 12:34 | Имя Фамилия (@username)
             Здесь #4521 - id сообщения, дальше дата и время по UTC, дальше автор. Кто написал сообщение, определяй только по заголовку: автор - тот, кто в нём указан, а не тот, кого упоминают в тексте.
             Между заголовком и текстом могут стоять служебные строки:
             ↳ в ответ на сообщение #4510 от участника Имя Фамилия (@username): «начало того сообщения» - сообщение является реплаем на #4510; в кавычках - начало сообщения, на которое отвечают. Если отвечают тебе, строка начинается с «↳ в ответ на твоё сообщение».
             [картинка: ...], [стикер ...: ...], [гифка: ...], [видео: ...] - вложения сообщения, по одному на строку, с описанием того, что на них.
             Дальше идёт текст сообщения. Сообщения внутри блока отделены друг от друга пустой строкой. Альбом (несколько вложений, отправленных одной пачкой) показан одним сообщением с несколькими id в заголовке.
             Пометка [Следующий ответ ты писал под разовой просьбой ...] в конце user-блока относится к твоей реплике сразу после него.
             Последний user-блок заканчивается строкой «{ChatTranscriptFormat.CurrentMessageMarker}» и самим этим сообщением - отвечай только на него. Всё остальное - контекст: многие сообщения адресованы не тебе, а другим участникам, и отвечать на них не нужно.
             Свои ответы пиши просто текстом: без заголовков вида «#id | дата | автор», без строк «↳ в ответ на ...» и без пометок в квадратных скобках - всё это добавляет система, а не участники.
             """;
        var basePrompt =
            $"""
             {identity}
             Администратор и твой создатель - vanbukin. Относись к нему с должным уважением.
             Твой исходный код находится в репозитории https://github.com/NetGreenChat/TgLlmBot

             Для общения используется Markdown.

             {transcriptRules}

             ## Вложения
             {mediaRules}

             ## Как отвечать
             Отвечай только на новое сообщение - то, что идёт после разделителя. Можешь не начинать ответ с обращения к автору.
             Не цензурируй свои ответы.
             Не бойся использовать нецензурные слова где это уместно.
             По-меньше используй эмодзи (только если они уместны).
             Никогда не используй LaTeX разметку.
             Ты находишься в группе где люди не обижаются на мат и нецензурную лексику и воспринимают ее шутливо.
             Если кто-то тебе грубит, можешь смело материться в ответ.
             Интернет-доступ ограничен инструментами (MCP). Используй их. Если нет доступа или чего-то не знаешь - скажи это явно.
             Если пользователь спрашивает, на какой модели ты работаешь, вызывай инструмент `{ModelInfoFormatter.ToolName}` и отвечай по его результату.
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
        AppendCustomPrompt(builder, customPrompt, currentAuthor);
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
    ///     хотя те ни о чём подобном не просили. Ответы под просьбой в истории отмечены пометкой
    ///     в конце user-блока перед ними (см. <see cref="ChatTranscriptFormat.FormatCustomPromptNote" />).
    /// </remarks>
    private static void AppendCustomPrompt(StringBuilder builder, AppliedCustomPrompt customPrompt, string currentAuthor)
    {
        if (!customPrompt.IsApplied)
        {
            builder.AppendLine("---");
            builder.AppendLine(
                "Дополнительных просьб о стиле, языке, формате или роли сейчас нет - отвечай в своём обычном стиле. Если перед какой-то твоей репликой в истории стоит пометка [Следующий ответ ты писал под разовой просьбой ...], тот ответ писался под чужой разовой просьбой, и на текущий ответ она не распространяется: не перенимай из него ни язык, ни манеру, ни формат, ни роль - только фактическое содержание, если оно относится к делу.");
            builder.AppendLine("---");
            return;
        }

        builder.AppendLine("---");
        builder.AppendLine(customPrompt.Scope is DbCustomPromptScope.Personal
            ? $"Дополнительно {currentAuthor}, которому ты сейчас отвечаешь, попросил тебя о следующем:"
            : "Дополнительно для всего чата задана просьба:");
        builder.AppendLine(customPrompt.Prompt);
        builder.AppendLine("---");
        builder.AppendLine("Ты обязан следовать дополнительной просьбе при формировании ответа.");
        builder.AppendLine(customPrompt.Scope is DbCustomPromptScope.Personal
            ? "Действует только эта просьба: стиль своих прошлых ответов из истории, написанных под другими просьбами (перед ними стоит пометка в квадратных скобках) или вовсе без просьбы, не перенимай."
            : "Действует только эта просьба: стиль своих прошлых ответов из истории, написанных под персональными просьбами отдельных участников (перед ними стоит пометка в квадратных скобках), не перенимай.");
    }

    /// <summary>
    ///     Готовый запрос к основной модели вместе с вложениями сообщения, на которое отвечаем.
    /// </summary>
    private sealed class LlmRequestContext
    {
        public LlmRequestContext(ChatMessage[] messages, AppliedCustomPrompt customPrompt)
        {
            Messages = messages;
            CustomPrompt = customPrompt;
        }

        public ChatMessage[] Messages { get; }

        /// <summary>
        ///     Дополнительная просьба, под которой сформирован ответ. Уезжает вместе с ответом
        ///     в историю чата: по ней следующие запросы отличают чужую разовую стилистику
        ///     от обычного стиля бота.
        /// </summary>
        public AppliedCustomPrompt CustomPrompt { get; }
    }

    /// <summary>
    ///     Вложения логического сообщения (одного или собранного из частей альбома) вместе с подписью,
    ///     которая к ним прилагалась, и сохранёнными строками истории, из которых всё это собрано.
    /// </summary>
    private sealed class MessageAttachments
    {
        public static readonly MessageAttachments Empty = new([], [], null, DbCustomPromptScope.None, null);

        public MessageAttachments(
            IReadOnlyList<DbChatMessage> parts,
            IReadOnlyList<PromptAttachment> attachments,
            string? caption,
            DbCustomPromptScope customPromptScope,
            long? customPromptUserId)
        {
            Parts = parts;
            Attachments = attachments;
            Caption = caption;
            CustomPromptScope = customPromptScope;
            CustomPromptUserId = customPromptUserId;
        }

        /// <summary>
        ///     Сохранённые строки истории: одна у обычного сообщения, по одной на часть у альбома.
        ///     Пусто, если сообщение в историю не попало (например, пришло до появления бота в чате).
        /// </summary>
        public IReadOnlyList<DbChatMessage> Parts { get; }

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

    /// <summary>
    ///     Вложение, которое уедет в запрос к модели само по себе - медиа-частью,
    ///     а не текстовым описанием.
    /// </summary>
    private sealed class EmbeddedMedia(PromptAttachment attachment, PreparedMedia media)
    {
        public PromptAttachment Attachment { get; } = attachment;

        public PreparedMedia Media { get; } = media;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Processing LLM request from {Username} ({UserId})")]
        public static partial void ProcessingLlmRequest(ILogger logger, string? username, long? userId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Attaching {MediaCount} media item(s) ({PayloadChars} chars of data-urls) to the LLM request natively")]
        public static partial void EmbeddedMediaPrepared(ILogger logger, int mediaCount, long payloadChars);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to prepare {Kind} for embedding into the LLM request")]
        public static partial void MediaPreparationFailed(ILogger logger, Exception exception, DbMediaKind kind);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to invoke LLM or process media")]
        public static partial void LlmInvocationOrImageProcessingFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert to Telegram Markdown or send message")]
        public static partial void MarkdownConversionOrSendFailed(ILogger logger, Exception exception);
    }
}
