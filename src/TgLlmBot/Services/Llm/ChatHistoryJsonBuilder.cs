using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm;

/// <summary>
///     Сериализует историю чата в формат JSON, который читает основная модель: и при формировании
///     ответа, и при компактинге описаний вложений.
/// </summary>
public static class ChatHistoryJsonBuilder
{
    private static readonly JsonSerializerOptions HistorySerializationOptions = new(JsonSerializerDefaults.General)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    /// <summary>
    ///     Готовый контекст для LLM: вводное описание формата, сообщения истории в JSON и
    ///     завершающие инструкции.
    /// </summary>
    /// <param name="contextMessages">Сообщения истории в хронологическом порядке.</param>
    /// <param name="excludedMessageIds">
    ///     Идентификаторы сообщений, которые в историю попадать не должны (например, части альбома,
    ///     на который бот отвечает прямо сейчас: их вложения и так уедут в запрос отдельно).
    /// </param>
    public static ChatMessage[] BuildContext(DbChatMessage[] contextMessages, HashSet<int>? excludedMessageIds = null)
    {
        ArgumentNullException.ThrowIfNull(contextMessages);
        if (contextMessages.Length is 0)
        {
            return [];
        }

        var history = BuildHistory(contextMessages, excludedMessageIds);
        if (history.Length is 0)
        {
            return [];
        }

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
                                {nameof(JsonHistoryMessage.HasMedia)} - были ли к сообщению приложены вложения: картинки, стикеры, гифки, видео; есть у каждого сообщения
                                {nameof(JsonHistoryMessage.MediaGroupId)} - Id альбома: сообщения с одинаковым значением пользователь отправил одной пачкой картинок
                                {nameof(JsonHistoryMessage.Media)} - что именно было приложено, по одному элементу на вложение (есть, когда {nameof(JsonHistoryMessage.HasMedia)} = true):
                                  {nameof(JsonHistoryMedia.Order)} - порядковый номер вложения внутри сообщения,
                                  {nameof(JsonHistoryMedia.Kind)} - что это (картинка, стикер, анимированный стикер, гифка, видео),
                                  {nameof(JsonHistoryMedia.Emoji)} - эмодзи стикера,
                                  {nameof(JsonHistoryMedia.StickerSet)} - название стикерпака,
                                  {nameof(JsonHistoryMedia.Description)} - что на этом вложении изображено, включая дословно весь текст, который на нём виден
                                """)
        };
        foreach (var chatHistoryMessage in history)
        {
            var json = JsonSerializer.Serialize(chatHistoryMessage, HistorySerializationOptions);
            result.Add(new(ChatRole.User, json));
        }

        result.Add(new(ChatRole.User,
            $"При ответе на сообщение пользователя учитывай контекст обсуждений в которых он участвовал (по связке {nameof(JsonHistoryMessage.FromUserId)} + {nameof(JsonHistoryMessage.MessageId)} + {nameof(JsonHistoryMessage.ReplyToMessageId)} или по связке {nameof(JsonHistoryMessage.FromUserId)} + {nameof(JsonHistoryMessage.MessageId)} + {nameof(JsonHistoryMessage.ReplyToMessageId)} + {nameof(JsonHistoryMessage.MessageThreadId)})"));
        result.Add(new(ChatRole.User,
            $"Вложения из истории ты видел сам - их содержимое лежит в {nameof(JsonHistoryMessage.Media)}. Порядок сообщений в истории и {nameof(JsonHistoryMedia.Order)} внутри сообщения задают порядок, в котором их отправляли, а связка {nameof(JsonHistoryMessage.MessageId)} + {nameof(JsonHistoryMedia.Order)} однозначно говорит, какое описание к какому вложению относится. Помни, что было на присланных ранее вложениях, и не путай их между собой."));
        return result.ToArray();
    }

    /// <summary>
    ///     История в виде строки JSON - по одному сообщению на строку. Используется компактингом
    ///     описаний вложений.
    /// </summary>
    public static string BuildJsonHistory(DbChatMessage[] contextMessages, HashSet<int>? excludedMessageIds = null)
    {
        ArgumentNullException.ThrowIfNull(contextMessages);
        var history = BuildHistory(contextMessages, excludedMessageIds);
        if (history.Length is 0)
        {
            return string.Empty;
        }

        var jsons = history.Select(x => JsonSerializer.Serialize(x, HistorySerializationOptions));
        return string.Join(Environment.NewLine, jsons);
    }

    /// <summary>
    ///     Текст, который модель увидит вместо картинки: сжатое описание, а если его ещё нет -
    ///     объяснение по состоянию распознавания.
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

    private static JsonHistoryMessage[] BuildHistory(DbChatMessage[] contextMessages, HashSet<int>? excludedMessageIds)
    {
        if (contextMessages.Length is 0)
        {
            return [];
        }

        return contextMessages
            .Where(x => excludedMessageIds is null || !excludedMessageIds.Contains(x.MessageId))
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
                x.IsLlmReplyToMessage,
                x.Media.Count > 0,
                x.MediaGroupId,
                BuildHistoryMedia(x)))
            .ToArray();
    }

    private static JsonHistoryMedia[]? BuildHistoryMedia(DbChatMessage message)
    {
        if (message.Media.Count is 0)
        {
            return null;
        }

        return message.Media
            .OrderBy(x => x.Order)
            .Select(x => new JsonHistoryMedia(
                x.Order,
                DescribeKind(x),
                x.Emoji?.Trim(),
                x.SetName?.Trim(),
                DescribeMedia(x)))
            .ToArray();
    }

    private static string DescribeKind(DbChatMessageMedia media)
    {
        return MediaKindNames.Describe(media.Kind, media.IsAnimated);
    }
}

public sealed class JsonHistoryMedia
{
    public JsonHistoryMedia(int order, string kind, string? emoji, string? stickerSet, string description)
    {
        Order = order;
        Kind = kind;
        Emoji = emoji;
        StickerSet = stickerSet;
        Description = description;
    }

    public int Order { get; }
    public string Kind { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Emoji { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StickerSet { get; }

    public string Description { get; }
}

public sealed class JsonHistoryMessage
{
    public JsonHistoryMessage(
        DateTimeOffset dateTimeUtc,
        int messageId,
        int? messageThreadId,
        int? replyToMessageId,
        long? fromUserId,
        string? fromUsername,
        string? fromFirstName,
        string? fromLastName,
        string? text,
        bool isLlmReplyToMessage,
        bool hasMedia,
        string? mediaGroupId,
        JsonHistoryMedia[]? media)
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
        HasMedia = hasMedia;
        MediaGroupId = mediaGroupId;
        Media = media;
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

    /// <summary>
    ///     Пишется у каждого сообщения, в том числе <see langword="false" />: модель должна
    ///     видеть разницу между "картинки не было" и "картинка была, но её не описали".
    /// </summary>
    public bool HasMedia { get; }

    // Остальные поля вложений пишем только у тех сообщений, где они есть: пустые значения
    // у каждого сообщения истории заняли бы заметный кусок контекста ничем
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaGroupId { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonHistoryMedia[]? Media { get; }
}
