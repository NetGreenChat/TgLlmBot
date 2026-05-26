using System;

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
        DbChatMessageKind kind)
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
        Kind = kind;
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
    public DbChatMessageKind Kind { get; set; }
}
