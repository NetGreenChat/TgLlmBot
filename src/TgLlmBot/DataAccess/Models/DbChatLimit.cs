namespace TgLlmBot.DataAccess.Models;

public class DbChatLimit
{
    public DbChatLimit()
    {
    }

    public DbChatLimit(long chatId, int limit)
    {
        ChatId = chatId;
        Limit = limit;
    }

    public long ChatId { get; set; }
    public int Limit { get; set; }
}
