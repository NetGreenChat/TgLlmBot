using System;
using System.Collections.Generic;

namespace TgLlmBot.DataAccess.Models;

public class DbChatMessage
{
    public DbChatMessage()
    {
    }

    public DbChatMessage(
        int messageId,
        long chatId,
        int? messageThreadId,
        int? replyToMessageId,
        DateTime date,
        long? fromUserId,
        string? fromUsername,
        string? fromFirstName,
        string? fromLastName,
        string? text,
        string? caption,
        bool isLlmReplyToMessage,
        string? mediaGroupId)
    {
        MessageId = messageId;
        ChatId = chatId;
        MessageThreadId = messageThreadId;
        ReplyToMessageId = replyToMessageId;
        Date = date;
        FromUserId = fromUserId;
        FromUsername = fromUsername;
        FromFirstName = fromFirstName;
        FromLastName = fromLastName;
        Text = text;
        Caption = caption;
        IsLlmReplyToMessage = isLlmReplyToMessage;
        MediaGroupId = mediaGroupId;
    }

    public Guid Id { get; set; }
    public int MessageId { get; set; }
    public long ChatId { get; set; }
    public int? MessageThreadId { get; set; }
    public int? ReplyToMessageId { get; set; }
    public DateTime Date { get; set; }
    public long? FromUserId { get; set; }
    public string? FromUsername { get; set; }
    public string? FromFirstName { get; set; }
    public string? FromLastName { get; set; }
    public string? Text { get; set; }
    public string? Caption { get; set; }
    public bool IsLlmReplyToMessage { get; set; }

    /// <summary>
    ///     Идентификатор альбома Telegram. Картинки, отправленные одной пачкой, приходят
    ///     отдельными сообщениями с общим значением этого поля - по нему они собираются обратно.
    /// </summary>
    public string? MediaGroupId { get; set; }

    /// <summary>
    ///     Вложения сообщения, упорядоченные по <see cref="DbChatMessageMedia.Order" />.
    ///     Удаляются вместе с сообщением каскадом на стороне базы.
    /// </summary>
    public ICollection<DbChatMessageMedia> Media { get; } = new List<DbChatMessageMedia>();
}
