using System;

namespace TgLlmBot.BackgroundServices;

public class MediaRecognitionBackgroundServiceOptions
{
    public MediaRecognitionBackgroundServiceOptions(TimeSpan sweepInterval, string botName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sweepInterval, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(botName);
        SweepInterval = sweepInterval;
        BotName = botName;
    }

    /// <summary>
    ///     Как часто искать вложения, застрявшие на полпути.
    /// </summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>
    ///     Имя бота - им подписаны его сообщения в истории, которую видит описатель вложений.
    /// </summary>
    public string BotName { get; }
}
