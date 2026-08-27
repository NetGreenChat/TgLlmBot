using System;

namespace TgLlmBot.BackgroundServices;

public class MediaRecognitionBackgroundServiceOptions
{
    public MediaRecognitionBackgroundServiceOptions(TimeSpan sweepInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sweepInterval, TimeSpan.Zero);
        SweepInterval = sweepInterval;
    }

    /// <summary>
    ///     Как часто искать вложения, застрявшие на полпути.
    /// </summary>
    public TimeSpan SweepInterval { get; }
}
