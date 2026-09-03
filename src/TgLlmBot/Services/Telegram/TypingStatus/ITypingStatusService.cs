namespace TgLlmBot.Services.Telegram.TypingStatus;

public interface ITypingStatusService
{
    /// <summary>
    ///     Просит держать статус "печатает" в чате, пока возвращённая область не будет остановлена.
    /// </summary>
    /// <remarks>
    ///     Чат печатает, пока жива хотя бы одна область. Остановка области идемпотентна и гасит
    ///     только собственную просьбу - чужие просьбы в том же чате она не трогает.
    /// </remarks>
    TypingScope StartTyping(long chatId);
}
