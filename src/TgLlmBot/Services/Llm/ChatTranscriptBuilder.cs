using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.AI;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.Llm;

/// <summary>
///     Собирает историю чата в реплики для модели: сообщения участников - блоками с ролью
///     user, собственные ответы бота - репликами с ролью assistant.
/// </summary>
/// <remarks>
///     <para>
///         Роли - самый сильный сигнал, по которому модель обучена отличать "что говорил я" от
///         "что говорили мне". Пока вся история ехала JSON-ом в роли user, модель с небольшим
///         числом параметров путала свои ответы с чужими сообщениями, а авторов - между собой.
///         Поэтому ответы бота уходят его собственными прошлыми репликами, а сообщения участников -
///         текстом с заголовком, где автор назван словами (см. <see cref="ChatTranscriptFormat" />).
///     </para>
///     <para>
///         Реплики строго чередуются: user, assistant, user, ... - и заканчиваются user-репликой
///         с сообщением, на которое надо ответить. Подряд идущие сообщения участников склеиваются
///         в один блок, подряд идущие ответы бота - в одну реплику: так шаблон любой модели
///         получает ту структуру диалога, на которой её учили.
///     </para>
/// </remarks>
public static class ChatTranscriptBuilder
{
    private static readonly string BlockSeparator = Environment.NewLine + Environment.NewLine;

    /// <summary>
    ///     Реплики запроса на ответ, без системного промпта. Последняя - user-реплика с хвостом
    ///     истории после последнего ответа бота и самим сообщением, на которое бот отвечает.
    /// </summary>
    public static ChatMessage[] BuildConversation(ChatConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var history = request.ContextMessages
            .Where(x => !request.ExcludedMessageIds.Contains(x.MessageId))
            .ToArray();
        var entries = TranscriptEntry.Group(history);
        var quoted = TranscriptEntry.Group(request.QuotedMessages);
        var lookup = new EntryLookup(entries.Concat(quoted));
        var authors = request.Authors;

        var turns = new List<ChatMessage>();
        var userBlock = new List<string>();
        var botBlock = new List<TranscriptEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsBot)
            {
                botBlock.Add(entry);
                continue;
            }

            if (botBlock.Count > 0)
            {
                FlushTurnPair(turns, userBlock, botBlock, authors);
            }

            userBlock.Add(RenderUserEntry(entry, lookup, authors, botParticipant: null));
        }

        if (botBlock.Count > 0)
        {
            FlushTurnPair(turns, userBlock, botBlock, authors);
        }

        // Последняя user-реплика: хвост истории после последнего ответа бота и само сообщение,
        // отделённое разделителем, - иначе модель отвечала бы на весь блок разом
        userBlock.Add(string.Concat(ChatTranscriptFormat.CurrentMessageMarker, Environment.NewLine, request.CurrentMessageBlock));
        var contents = new List<AIContent>(request.CurrentMessageMedia.Count + 1);
        contents.AddRange(request.CurrentMessageMedia);
        contents.Add(new TextContent(string.Join(BlockSeparator, userBlock)));
        turns.Add(new ChatMessage(ChatRole.User, contents));
        return turns.ToArray();
    }

    /// <summary>
    ///     История одним текстом - для описателя вложений, которому она нужна только как
    ///     подсказка, какие детали вложения важно сохранить. Ролей в таком тексте нет, поэтому
    ///     сообщения бота подписаны его именем наравне с участниками.
    /// </summary>
    public static string BuildTranscript(DbChatMessage[] contextMessages, string botName)
    {
        ArgumentNullException.ThrowIfNull(contextMessages);
        return BuildTranscript(contextMessages, botName, ChatAuthorDirectory.FromMessages(contextMessages));
    }

    /// <summary>
    ///     То же, но с готовым справочником подписей - когда подписи участников нужны вызывающему
    ///     коду ещё где-то в промпте и должны совпадать с заголовками буква в букву.
    /// </summary>
    public static string BuildTranscript(DbChatMessage[] contextMessages, string botName, ChatAuthorDirectory authors)
    {
        ArgumentNullException.ThrowIfNull(contextMessages);
        ArgumentException.ThrowIfNullOrWhiteSpace(botName);
        ArgumentNullException.ThrowIfNull(authors);
        var entries = TranscriptEntry.Group(contextMessages);
        if (entries.Count is 0)
        {
            return string.Empty;
        }

        var lookup = new EntryLookup(entries);
        var botParticipant = ChatTranscriptFormat.FormatBotParticipant(botName);
        var blocks = entries.Select(entry => RenderUserEntry(entry, lookup, authors, botParticipant));
        return string.Join(BlockSeparator, blocks);
    }

    /// <summary>
    ///     Закрывает пару реплик: накопленный блок сообщений участников и следующий за ним
    ///     ответ бота. Если ответ писался под разовой просьбой, пометка об этом встаёт в конец
    ///     user-блока - прямо перед репликой, к которой относится.
    /// </summary>
    private static void FlushTurnPair(
        List<ChatMessage> turns,
        List<string> userBlock,
        List<TranscriptEntry> botBlock,
        ChatAuthorDirectory authors)
    {
        var scoped = botBlock.FirstOrDefault(static x => x.CustomPromptScope is not DbCustomPromptScope.None);
        if (scoped is not null)
        {
            userBlock.Add(ChatTranscriptFormat.FormatCustomPromptNote(scoped.CustomPromptScope, authors.Resolve(scoped.CustomPromptUserId)));
        }

        if (userBlock.Count is 0)
        {
            userBlock.Add(ChatTranscriptFormat.HistoryStartNote);
        }

        turns.Add(new ChatMessage(ChatRole.User, string.Join(BlockSeparator, userBlock)));
        turns.Add(new ChatMessage(ChatRole.Assistant, string.Join(BlockSeparator, botBlock.Select(RenderBotEntry))));
        userBlock.Clear();
        botBlock.Clear();
    }

    /// <summary>
    ///     Сообщение в виде текста: заголовок, строка реплая, строки вложений, текст.
    /// </summary>
    /// <param name="botParticipant">
    ///     Подпись бота для текста без ролей. Если <see langword="null" />, сообщения бота
    ///     в такой текст не попадают (они уходят assistant-репликами), а реплай на них
    ///     пишется как "в ответ на твоё сообщение".
    /// </param>
    private static string RenderUserEntry(TranscriptEntry entry, EntryLookup lookup, ChatAuthorDirectory authors, string? botParticipant)
    {
        var author = entry.IsBot && botParticipant is not null ? botParticipant : authors.Describe(entry.Head);
        var allMedia = entry.Media;
        var builder = new StringBuilder();
        builder.Append(ChatTranscriptFormat.FormatHeader(entry.MessageIds, entry.Date, author, allMedia.Length));
        if (entry.Head.ReplyToMessageId is { } replyToMessageId)
        {
            builder.AppendLine().Append(RenderReplyLine(replyToMessageId, lookup, authors, botParticipant));
        }

        for (var i = 0; i < allMedia.Length; i++)
        {
            var media = allMedia[i];
            builder.AppendLine().Append(ChatTranscriptFormat.FormatMediaLine(media, i + 1, allMedia.Length, ChatTranscriptFormat.DescribeMedia(media)));
        }

        var text = entry.Text;
        if (text.Length > 0)
        {
            builder.AppendLine().Append(text);
        }
        else if (allMedia.Length is 0)
        {
            builder.AppendLine().Append("[пустое сообщение]");
        }

        return builder.ToString();
    }

    private static string RenderReplyLine(int targetMessageId, EntryLookup lookup, ChatAuthorDirectory authors, string? botParticipant)
    {
        var target = lookup.Find(targetMessageId);
        if (target is null)
        {
            return ChatTranscriptFormat.FormatUnavailableReplyLine(targetMessageId);
        }

        var excerpt = ChatTranscriptFormat.Excerpt(target.QuoteText);
        if (!target.IsBot)
        {
            return ChatTranscriptFormat.FormatReplyLine(targetMessageId, authors.Describe(target.Head), excerpt);
        }

        if (botParticipant is not null)
        {
            return ChatTranscriptFormat.FormatReplyLine(targetMessageId, botParticipant, excerpt);
        }

        var note = target.CustomPromptScope is DbCustomPromptScope.None
            ? null
            : ChatTranscriptFormat.FormatReplyCustomPromptNote(target.CustomPromptScope, authors.Resolve(target.CustomPromptUserId));
        return ChatTranscriptFormat.FormatBotReplyLine(targetMessageId, excerpt, note);
    }

    /// <summary>
    ///     Ответ бота - только его текст, без заголовка и прочей разметки: что модель видит
    ///     в своих репликах, то она и пишет дальше.
    /// </summary>
    private static string RenderBotEntry(TranscriptEntry entry)
    {
        var text = entry.Text;
        return text.Length > 0 ? text : "[вложение без текста]";
    }

    /// <summary>
    ///     Логическое сообщение: одно сообщение чата или альбом, собранный из частей.
    /// </summary>
    private sealed class TranscriptEntry
    {
        private readonly List<DbChatMessage> _parts = new(1);

        private TranscriptEntry(DbChatMessage head)
        {
            _parts.Add(head);
        }

        public DbChatMessage Head => _parts[0];

        public bool IsBot => Head.IsLlmReplyToMessage;

        public DateTime Date => Head.Date;

        public int[] MessageIds => _parts.Select(static x => x.MessageId).ToArray();

        public DbChatMessageMedia[] Media => _parts.SelectMany(static x => x.Media.OrderBy(static m => m.Order)).ToArray();

        /// <summary>
        ///     Текст сообщения. У альбома подпись лежит на одной из частей - берётся первая непустая.
        /// </summary>
        public string Text => _parts
            .Select(static x => (x.Text ?? x.Caption)?.Trim())
            .FirstOrDefault(static x => !string.IsNullOrEmpty(x)) ?? string.Empty;

        public DbCustomPromptScope CustomPromptScope => ScopedPart?.CustomPromptScope ?? DbCustomPromptScope.None;

        public long? CustomPromptUserId => ScopedPart?.CustomPromptUserId;

        /// <summary>
        ///     Чем сообщение цитируется в строке реплая: метки вложений и текст.
        /// </summary>
        public string QuoteText
        {
            get
            {
                var markers = Media.Select(ChatTranscriptFormat.FormatMediaMarker);
                return string.Join(' ', markers.Append(Text).Where(static x => x.Length > 0));
            }
        }

        private DbChatMessage? ScopedPart => _parts.FirstOrDefault(static x => x.CustomPromptScope is not DbCustomPromptScope.None);

        /// <summary>
        ///     Собирает сообщения в логические: части альбома - в одно, остальные - как есть.
        ///     Порядок хронологический; внутри одной секунды - по id сообщения.
        /// </summary>
        public static List<TranscriptEntry> Group(IEnumerable<DbChatMessage> messages)
        {
            var entries = new List<TranscriptEntry>();
            var albums = new Dictionary<string, TranscriptEntry>(StringComparer.Ordinal);
            foreach (var message in messages.OrderBy(static x => x.Date).ThenBy(static x => x.MessageId))
            {
                // Альбомы шлют только участники: бот отвечает текстом
                if (message.IsLlmReplyToMessage || string.IsNullOrEmpty(message.MediaGroupId))
                {
                    entries.Add(new(message));
                    continue;
                }

                if (albums.TryGetValue(message.MediaGroupId, out var album))
                {
                    album._parts.Add(message);
                    continue;
                }

                var entry = new TranscriptEntry(message);
                albums.Add(message.MediaGroupId, entry);
                entries.Add(entry);
            }

            return entries;
        }
    }

    /// <summary>
    ///     Поиск логического сообщения по id любой из его частей - для строк реплаев.
    /// </summary>
    private sealed class EntryLookup
    {
        private readonly Dictionary<int, TranscriptEntry> _byMessageId = new();

        public EntryLookup(IEnumerable<TranscriptEntry> entries)
        {
            foreach (var entry in entries)
            {
                foreach (var messageId in entry.MessageIds)
                {
                    _byMessageId.TryAdd(messageId, entry);
                }
            }
        }

        public TranscriptEntry? Find(int messageId)
        {
            return _byMessageId.GetValueOrDefault(messageId);
        }
    }
}

/// <summary>
///     Всё, из чего собираются реплики запроса на ответ.
/// </summary>
public sealed class ChatConversationRequest
{
    public ChatConversationRequest(
        DbChatMessage[] contextMessages,
        IReadOnlyCollection<DbChatMessage> quotedMessages,
        IReadOnlySet<int> excludedMessageIds,
        ChatAuthorDirectory authors,
        string currentMessageBlock,
        IReadOnlyList<AIContent> currentMessageMedia)
    {
        ArgumentNullException.ThrowIfNull(contextMessages);
        ArgumentNullException.ThrowIfNull(quotedMessages);
        ArgumentNullException.ThrowIfNull(excludedMessageIds);
        ArgumentNullException.ThrowIfNull(authors);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentMessageBlock);
        ArgumentNullException.ThrowIfNull(currentMessageMedia);
        ContextMessages = contextMessages;
        QuotedMessages = quotedMessages;
        ExcludedMessageIds = excludedMessageIds;
        Authors = authors;
        CurrentMessageBlock = currentMessageBlock;
        CurrentMessageMedia = currentMessageMedia;
    }

    /// <summary>
    ///     История чата до текущего сообщения.
    /// </summary>
    public DbChatMessage[] ContextMessages { get; }

    /// <summary>
    ///     Сообщения, на которые ссылаются реплаи из истории, но которые в саму историю не
    ///     попали: они нужны только ради цитаты в строке реплая.
    /// </summary>
    public IReadOnlyCollection<DbChatMessage> QuotedMessages { get; }

    /// <summary>
    ///     Сообщения истории, которые в неё попадать не должны: само текущее сообщение и
    ///     остальные части его альбома - их содержимое и так уезжает в запрос отдельно.
    /// </summary>
    public IReadOnlySet<int> ExcludedMessageIds { get; }

    /// <summary>
    ///     Подписи участников - одни и те же во всём запросе.
    /// </summary>
    public ChatAuthorDirectory Authors { get; }

    /// <summary>
    ///     Текущее сообщение, уже отформатированное как блок текста в том же формате, что и история.
    /// </summary>
    public string CurrentMessageBlock { get; }

    /// <summary>
    ///     Маркеры вложений, приложенных к запросу нативно: они встают в последнюю user-реплику
    ///     перед текстом.
    /// </summary>
    public IReadOnlyList<AIContent> CurrentMessageMedia { get; }
}

/// <summary>
///     Подписи участников чата по id пользователя. Одного и того же участника модель должна
///     видеть под одной и той же подписью во всём запросе, даже если он менял имя: иначе
///     модель с небольшим числом параметров считает его двумя разными людьми.
/// </summary>
public sealed class ChatAuthorDirectory
{
    private readonly Dictionary<long, string> _labels = new();

    private ChatAuthorDirectory()
    {
    }

    /// <summary>
    ///     Собирает подписи по сообщениям участников. Сообщения идут в хронологическом порядке,
    ///     поэтому у каждого участника остаётся самая свежая подпись.
    /// </summary>
    public static ChatAuthorDirectory FromMessages(IEnumerable<DbChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var directory = new ChatAuthorDirectory();
        foreach (var message in messages.OrderBy(static x => x.Date).ThenBy(static x => x.MessageId))
        {
            if (message.IsLlmReplyToMessage || message.FromUserId is null)
            {
                continue;
            }

            directory._labels[message.FromUserId.Value] = ChatTranscriptFormat.FormatAuthor(
                message.FromUserId,
                message.FromFirstName,
                message.FromLastName,
                message.FromUsername);
        }

        return directory;
    }

    /// <summary>
    ///     Запоминает подпись участника поверх собранной из истории - например, автора
    ///     текущего сообщения, чьё имя Telegram только что прислал.
    /// </summary>
    public void Register(long userId, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _labels[userId] = label;
    }

    public bool TryGet(long userId, out string label)
    {
        return _labels.TryGetValue(userId, out label!);
    }

    /// <summary>
    ///     Подпись автора сообщения из истории.
    /// </summary>
    public string Describe(DbChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.FromUserId is { } userId && _labels.TryGetValue(userId, out var label))
        {
            return label;
        }

        return ChatTranscriptFormat.FormatAuthor(message.FromUserId, message.FromFirstName, message.FromLastName, message.FromUsername);
    }

    /// <summary>
    ///     Подпись участника по id - для пометок о разовых просьбах. Если участник в истории
    ///     не встречался, остаётся только id.
    /// </summary>
    public string? Resolve(long? userId)
    {
        if (userId is null)
        {
            return null;
        }

        return _labels.TryGetValue(userId.Value, out var label)
            ? label
            : ChatTranscriptFormat.FormatAuthor(userId, null, null, null);
    }
}
