using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm;

/// <summary>
///     Как сообщения чата выглядят в тексте для модели: заголовок, строка реплая, строки
///     вложений, служебные пометки. Формат один на все промпты - и на запрос ответа, и на
///     описание вложений, - чтобы модель везде читала одно и то же.
/// </summary>
/// <remarks>
///     Формат рассчитан на модель с небольшим числом параметров. Никаких числовых
///     идентификаторов, которые надо сопоставлять между сообщениями: автор написан словами
///     в заголовке каждого сообщения, а реплай сразу цитирует начало того, на что отвечают.
///     Id сообщения в заголовке остаётся только как якорь для строки реплая.
/// </remarks>
public static partial class ChatTranscriptFormat
{
    /// <summary>
    ///     Разделитель перед сообщением, на которое бот отвечает: оно идёт последним в последнем
    ///     user-блоке вместе с хвостом истории, и без явной пометки модель отвечала бы на весь блок.
    /// </summary>
    public const string CurrentMessageMarker = "=== Новое сообщение, на которое ты отвечаешь ===";

    /// <summary>
    ///     Первая строка истории, когда первым в ней идёт ответ бота: реплики обязаны чередоваться,
    ///     и user-реплика нужна даже перед самым первым assistant-ответом.
    /// </summary>
    public const string HistoryStartNote = "[Более ранние сообщения чата в историю не попали]";

    private const int ExcerptMaxLength = 160;

    private const string TimestampFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    ///     Дата и время по UTC - в том виде, в каком они стоят и в заголовках сообщений,
    ///     и в системном промпте.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset date)
    {
        return date.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Дата и время сообщения: Telegram и база хранят их в UTC.
    /// </summary>
    public static string FormatTimestamp(DateTime date)
    {
        return FormatTimestamp(new DateTimeOffset(date.Ticks, TimeSpan.Zero));
    }

    /// <summary>
    ///     Подпись автора: "Имя Фамилия (@username)". Что из этого есть у отправителя, то и пишется.
    /// </summary>
    public static string FormatAuthor(long? userId, string? firstName, string? lastName, string? username)
    {
        var name = string.Join(' ', new[] { CleanInline(firstName), CleanInline(lastName) }.Where(static x => x.Length > 0));
        var user = CleanInline(username).TrimStart('@');
        if (name.Length is 0 && user.Length is 0)
        {
            return userId is null
                ? "неизвестный отправитель"
                : $"участник с id={userId.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (name.Length is 0)
        {
            return $"@{user}";
        }

        return user.Length is 0 ? name : $"{name} (@{user})";
    }

    /// <summary>
    ///     Подпись бота там, где его сообщения показываются в одном тексте с остальными
    ///     (история для описателя вложений): роли в таком тексте нет, и отличать бота
    ///     от участников приходится подписью.
    /// </summary>
    public static string FormatBotParticipant(string botName)
    {
        return $"{botName} (бот)";
    }

    /// <summary>
    ///     Заголовок сообщения: "#4521 | 2026-10-09 12:34 | Имя Фамилия (@username)".
    ///     У альбома в заголовке все id его частей и число вложений.
    /// </summary>
    public static string FormatHeader(IReadOnlyList<int> messageIds, DateTime date, string author, int attachmentsCount)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var ids = string.Join(", ", messageIds.Select(static id => $"#{id.ToString(CultureInfo.InvariantCulture)}"));
        var header = $"{ids} | {FormatTimestamp(date)} | {author}";
        return messageIds.Count > 1
            ? $"{header} | альбом из {attachmentsCount.ToString(CultureInfo.InvariantCulture)} вложений"
            : header;
    }

    /// <summary>
    ///     Строка реплая на сообщение участника: кто и что писал, видно сразу, без поиска по id.
    /// </summary>
    public static string FormatReplyLine(int messageId, string author, string excerpt)
    {
        return $"↳ в ответ на сообщение #{messageId.ToString(CultureInfo.InvariantCulture)} от участника {author}: «{excerpt}»";
    }

    /// <summary>
    ///     Строка реплая на собственное сообщение бота. Если тот ответ писался под разовой
    ///     просьбой, об этом сказано прямо здесь: цитата уезжает в промпт дословно, в обход
    ///     истории, и без пометки чужая стилистика протекла бы в текущий ответ.
    /// </summary>
    public static string FormatBotReplyLine(int messageId, string excerpt, string? customPromptNote)
    {
        var note = string.IsNullOrEmpty(customPromptNote) ? string.Empty : $" ({customPromptNote})";
        return $"↳ в ответ на твоё сообщение #{messageId.ToString(CultureInfo.InvariantCulture)}{note}: «{excerpt}»";
    }

    /// <summary>
    ///     Строка реплая на сообщение, которого в памяти бота нет: оно старше хранимой истории
    ///     или пришло до того, как бот появился в чате.
    /// </summary>
    public static string FormatUnavailableReplyLine(int messageId)
    {
        return $"↳ в ответ на сообщение #{messageId.ToString(CultureInfo.InvariantCulture)} (оно не сохранилось)";
    }

    /// <summary>
    ///     Строка вложения с его текстовым описанием: "[картинка 2 из 3: что на ней]".
    /// </summary>
    public static string FormatMediaLine(DbChatMessageMedia media, int index, int count, string description)
    {
        ArgumentNullException.ThrowIfNull(media);
        return $"[{FormatMediaLabel(media, index, count)}: {CleanInline(description)}]";
    }

    /// <summary>
    ///     Строка вложения, которое приложено к запросу само по себе: модель смотрит медиа-часть,
    ///     а строка говорит, какая из медиа-частей к какому сообщению относится.
    /// </summary>
    public static string FormatEmbeddedMediaLine(DbChatMessageMedia media, int index, int count, int partNumber)
    {
        ArgumentNullException.ThrowIfNull(media);
        return $"[{FormatMediaLabel(media, index, count)} - это медиа-часть {partNumber.ToString(CultureInfo.InvariantCulture)} этого запроса, смотри её сам]";
    }

    /// <summary>
    ///     Короткая метка вложения для цитаты: "[картинка]", "[стикер 😀]".
    /// </summary>
    public static string FormatMediaMarker(DbChatMessageMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var emoji = CleanInline(media.Emoji);
        var kind = MediaKindNames.Describe(media.Kind, media.IsAnimated);
        return emoji.Length is 0 ? $"[{kind}]" : $"[{kind} {emoji}]";
    }

    /// <summary>
    ///     Пометка в конце user-блока о том, что следующая за ним реплика бота писалась под
    ///     разовой дополнительной просьбой. Стоит в user-блоке, а не в самой реплике: всё, что
    ///     модель видит в своих репликах, она норовит воспроизвести.
    /// </summary>
    public static string FormatCustomPromptNote(DbCustomPromptScope scope, string? author)
    {
        return scope switch
        {
            DbCustomPromptScope.Personal =>
                $"[Следующий ответ ты писал под разовой просьбой пользователя {author}; её стиль действует только на тот ответ]",
            DbCustomPromptScope.Chat =>
                "[Следующий ответ ты писал под разовой просьбой, заданной на весь чат; её стиль действует только на тот ответ]",
            _ => string.Empty
        };
    }

    /// <summary>
    ///     Та же пометка, но внутри строки реплая на сообщение бота.
    /// </summary>
    public static string FormatReplyCustomPromptNote(DbCustomPromptScope scope, string? author)
    {
        return scope switch
        {
            DbCustomPromptScope.Personal =>
                $"ты писал его под разовой просьбой пользователя {author}; её стиль на текущий ответ не переносится",
            DbCustomPromptScope.Chat =>
                "ты писал его под разовой просьбой, заданной на весь чат; её стиль на текущий ответ не переносится",
            _ => string.Empty
        };
    }

    /// <summary>
    ///     Начало сообщения для цитаты в строке реплая: одна строка, не длиннее
    ///     <see cref="ExcerptMaxLength" /> символов, обрезанная по границе слова.
    /// </summary>
    public static string Excerpt(string? text)
    {
        var line = CleanInline(text);
        if (line.Length <= ExcerptMaxLength)
        {
            return line;
        }

        var cut = line.LastIndexOf(' ', ExcerptMaxLength);
        if (cut < ExcerptMaxLength / 2)
        {
            cut = ExcerptMaxLength;
        }

        // Не резать суррогатную пару пополам: половинка эмодзи - невалидный UTF-16
        if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return string.Concat(line.AsSpan(0, cut).TrimEnd(), "…");
    }

    /// <summary>
    ///     Текст, который модель видит вместо вложения: компактное описание, а если его ещё нет -
    ///     объяснение по состоянию обработки.
    /// </summary>
    public static string DescribeMedia(DbChatMessageMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (!string.IsNullOrWhiteSpace(media.ShortDescription))
        {
            return media.ShortDescription;
        }

        return media.Status switch
        {
            DbMediaRecognitionStatus.Pending => "Описание ещё готовится, разглядеть не успели.",
            DbMediaRecognitionStatus.Unsupported => "Разглядеть нечего: показать модели этот файл не получилось.",
            _ => "Разглядеть не удалось."
        };
    }

    /// <summary>
    ///     Убирает из начала ответа модели служебную разметку, если та всё же её воспроизвела:
    ///     заголовок сообщения, строку реплая или разделитель. В чат это уходить не должно.
    /// </summary>
    public static string StripLeakedMarkup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var remaining = text.AsSpan().TrimStart();
        while (!remaining.IsEmpty)
        {
            var lineEnd = remaining.IndexOf('\n');
            var line = (lineEnd < 0 ? remaining : remaining[..lineEnd]).TrimEnd();
            if (!IsLeakedMarkupLine(line))
            {
                break;
            }

            remaining = lineEnd < 0 ? ReadOnlySpan<char>.Empty : remaining[(lineEnd + 1)..].TrimStart();
        }

        return remaining.Length == text.Length ? text : remaining.ToString();
    }

    private static bool IsLeakedMarkupLine(ReadOnlySpan<char> line)
    {
        return line.SequenceEqual(CurrentMessageMarker)
               || line.StartsWith("↳ в ответ на ", StringComparison.Ordinal)
               || HeaderLineRegex().IsMatch(line);
    }

    [GeneratedRegex(@"^#\d+(, #\d+)* \| \d{4}-\d{2}-\d{2} \d{2}:\d{2} \| ")]
    private static partial Regex HeaderLineRegex();

    /// <summary>
    ///     "картинка", "картинка 2 из 3", "стикер 😀 из набора «Cats»".
    /// </summary>
    private static string FormatMediaLabel(DbChatMessageMedia media, int index, int count)
    {
        var builder = new StringBuilder(MediaKindNames.Describe(media.Kind, media.IsAnimated));
        if (count > 1)
        {
            builder
                .Append(' ')
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(" из ")
                .Append(count.ToString(CultureInfo.InvariantCulture));
        }

        var emoji = CleanInline(media.Emoji);
        if (emoji.Length > 0)
        {
            builder.Append(' ').Append(emoji);
        }

        var setName = CleanInline(media.SetName);
        if (setName.Length > 0)
        {
            builder.Append(" из набора «").Append(setName).Append('»');
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Одна строка без переводов строк, лишних пробелов и символа «|», которым разделены
    ///     поля заголовка.
    /// </summary>
    private static string CleanInline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch is '|' ? '/' : ch);
        }

        return builder.ToString();
    }
}
