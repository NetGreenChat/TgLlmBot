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
        string? mediaGroupId,
        DbCustomPromptScope customPromptScope,
        long? customPromptUserId)
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
        CustomPromptScope = customPromptScope;
        CustomPromptUserId = customPromptUserId;
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
    ///     Под какой дополнительной просьбой к системному промпту был сгенерирован этот ответ бота.
    ///     У сообщений пользователей и у служебных ответов команд всегда
    ///     <see cref="DbCustomPromptScope.None" />.
    /// </summary>
    /// <remarks>
    ///     Пометка уезжает в историю чата вместе с сообщением: по ней модель отличает свои ответы,
    ///     написанные под чужой разовой просьбой о стиле, от ответов в собственном обычном стиле -
    ///     и не тащит чужую стилистику в ответы остальным участникам чата.
    /// </remarks>
    public DbCustomPromptScope CustomPromptScope { get; set; }

    /// <summary>
    ///     Автор персональной просьбы, под которой сгенерирован ответ. Заполнен только при
    ///     <see cref="DbCustomPromptScope.Personal" />.
    /// </summary>
    public long? CustomPromptUserId { get; set; }

    /// <summary>
    ///     Вложения сообщения, упорядоченные по <see cref="DbChatMessageMedia.Order" />.
    ///     Удаляются вместе с сообщением каскадом на стороне базы.
    /// </summary>
    public ICollection<DbChatMessageMedia> Media { get; } = new List<DbChatMessageMedia>();
}
