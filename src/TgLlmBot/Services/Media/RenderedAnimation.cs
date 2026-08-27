using System;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Кадры, вырезанные из анимации, вместе с таймингом исходника.
/// </summary>
/// <remarks>
///     Тайминг нужен не для красоты: vLLM подставляет его в промпт как метки времени вида mm:ss
///     перед каждым кадром, и без него модель не поймёт, кадры сняты за секунду или за минуту.
/// </remarks>
public sealed class RenderedAnimation
{
    public RenderedAnimation(
        byte[][] frames,
        double sourceFps,
        int[] sourceFrameIndices,
        int sourceFrameCount,
        TimeSpan sourceDuration)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(sourceFrameIndices);
        ArgumentOutOfRangeException.ThrowIfZero(frames.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceFps);
        // Требования vLLM к метаданным: индекс на каждый отправленный кадр,
        // и кадров в исходнике не меньше, чем отправлено
        if (sourceFrameIndices.Length != frames.Length)
        {
            throw new ArgumentException("Frame indices count must match frames count.", nameof(sourceFrameIndices));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sourceFrameCount, frames.Length);
        Frames = frames;
        SourceFps = sourceFps;
        SourceFrameIndices = sourceFrameIndices;
        SourceFrameCount = sourceFrameCount;
        SourceDuration = sourceDuration;
    }

    /// <summary>
    ///     Кадры в JPEG в порядке воспроизведения.
    /// </summary>
    public byte[][] Frames { get; }

    /// <summary>
    ///     Частота кадров исходной анимации.
    /// </summary>
    public double SourceFps { get; }

    /// <summary>
    ///     Номера отправляемых кадров в исходной анимации: из них модель получает метки времени.
    /// </summary>
    public int[] SourceFrameIndices { get; }

    /// <summary>
    ///     Сколько кадров было в исходной анимации целиком.
    /// </summary>
    public int SourceFrameCount { get; }

    /// <summary>
    ///     Длительность исходной анимации.
    /// </summary>
    public TimeSpan SourceDuration { get; }
}
