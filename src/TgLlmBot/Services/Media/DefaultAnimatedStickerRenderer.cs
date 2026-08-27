using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SkiaSharp.Skottie;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Рисует кадры анимированного стикера через Skottie - Lottie-движок Skia.
/// </summary>
/// <remarks>
///     Всё считается на CPU внутри процесса: ни ffmpeg, ни другого внешнего рендерера не нужно,
///     а Raspberry Pi отрисовка десятка векторных кадров стоит порядка сотни миллисекунд.
///     Дороже она и не бывает: описания стикеров кэшируются, поэтому один и тот же стикер
///     рисуется один раз за всю жизнь базы.
/// </remarks>
public partial class DefaultAnimatedStickerRenderer : IAnimatedStickerRenderer
{
    /// <summary>
    ///     Сколько кадров вырезаем из анимации.
    /// </summary>
    /// <remarks>
    ///     Стикер - это трёхсекундная петля, и шестнадцати кадров хватает, чтобы модель увидела
    ///     не только позу, но и само движение: появившуюся надпись, смену выражения лица, жест.
    ///     Потолок vLLM - 32 кадра на видео, но каждый кадр стоит около 78 токенов промпта,
    ///     а вдвое большее число кадров описание уже не улучшает.
    /// </remarks>
    private const int MaxFrames = 16;

    /// <summary>
    ///     Сторона кадра в пикселях.
    /// </summary>
    /// <remarks>
    ///     Стикеры Telegram нарисованы в 512x512, но кадры видео vision-модель всё равно сжимает
    ///     под свой потолок в 70 soft-токенов - это примерно 384 пикселя по стороне. Рисовать
    ///     крупнее незачем: и Pi, и сеть заплатят, а модель разницы не увидит.
    /// </remarks>
    private const int FrameSize = 384;

    private const int JpegQuality = 82;

    /// <summary>
    ///     Предохранитель от gzip-бомбы: у Telegram TGS не больше 64 КБ, что разворачивается
    ///     в единицы мегабайт JSON.
    /// </summary>
    private const int MaxLottieJsonBytes = 8 * 1024 * 1024;

    /// <summary>
    ///     Подстановка на случай, если в анимации не указана частота кадров.
    /// </summary>
    private const double FallbackFps = 60;

    private readonly ILogger<DefaultAnimatedStickerRenderer> _logger;

    public DefaultAnimatedStickerRenderer(ILogger<DefaultAnimatedStickerRenderer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public Result<RenderedAnimation> Render(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is 0)
        {
            return Result<RenderedAnimation>.Fail();
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var json = Decompress(content);
            if (json is null)
            {
                return Result<RenderedAnimation>.Fail();
            }

            using (var data = SKData.CreateCopy(json))
            {
                // Skottie молча возвращает false и на не-Lottie JSON, и на битой анимации
                if (!Animation.TryCreate(data, out var animation))
                {
                    Log.AnimationNotParsed(_logger, content.Length, json.Length);
                    return Result<RenderedAnimation>.Fail();
                }

                using (animation)
                {
                    var rendered = RenderFrames(animation);
                    var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                    Log.AnimationRendered(
                        _logger,
                        content.Length,
                        rendered.Frames.Length,
                        rendered.SourceFrameCount,
                        elapsedMilliseconds);
                    return Result<RenderedAnimation>.Success(rendered);
                }
            }
        }
        catch (Exception ex)
        {
            // Сюда же приезжает отсутствующая нативная библиотека Skia: вложение уйдёт
            // в модель статическим превью, а бот продолжит работать
            Log.RenderFailed(_logger, content.Length, ex);
            return Result<RenderedAnimation>.Fail();
        }
    }

    private static RenderedAnimation RenderFrames(Animation animation)
    {
        var sourceFrameCount = Math.Max(1, (int)Math.Round(animation.OutPoint - animation.InPoint));
        var frameCount = Math.Min(MaxFrames, sourceFrameCount);
        var fps = animation.Fps > 0 ? animation.Fps : FallbackFps;
        var imageInfo = new SKImageInfo(FrameSize, FrameSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var surface = SKSurface.Create(imageInfo))
        {
            var canvas = surface.Canvas;
            var destination = new SKRect(0, 0, FrameSize, FrameSize);
            var frames = new byte[frameCount][];
            var frameIndices = new int[frameCount];
            for (var i = 0; i < frameCount; i++)
            {
                // Кадры берём равномерно по всей длине петли, включая самый первый и самый последний
                var frameIndex = frameCount is 1
                    ? 0
                    : (int)Math.Round((double)i * (sourceFrameCount - 1) / (frameCount - 1));
                frameIndices[i] = frameIndex;

                // Прозрачность JPEG не переживёт, поэтому кадр кладётся на белое: на нём
                // различимы и тёмные контуры, и светлая заливка
                canvas.Clear(SKColors.White);
                // Номер кадра Skottie отсчитывает от начала анимации, а не от нуля таймлайна:
                // InPoint она прибавляет сама, и прибавлять его ещё раз - значит промотать
                // анимацию на её же начало и упереться в OutPoint на хвосте
                animation.SeekFrame(frameIndex);
                animation.Render(canvas, destination);
                using (var image = surface.Snapshot())
                {
                    using (var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality))
                    {
                        frames[i] = encoded.ToArray();
                    }
                }
            }

            return new(
                frames,
                fps,
                frameIndices,
                sourceFrameCount,
                animation.Duration);
        }
    }

    /// <summary>
    ///     Разворачивает gzip с ограничением на размер результата.
    /// </summary>
    private byte[]? Decompress(byte[] content)
    {
        using (var source = new MemoryStream(content, false))
        {
            using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            {
                // Больше потолка буфер всё равно не понадобится: на нём распаковка и обрывается
                using (var target = new MemoryStream(Math.Min(content.Length * 4, MaxLottieJsonBytes)))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (target.Length + read > MaxLottieJsonBytes)
                        {
                            Log.AnimationTooLarge(_logger, content.Length, MaxLottieJsonBytes);
                            return null;
                        }

                        target.Write(buffer, 0, read);
                    }

                    return target.ToArray();
                }
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Rendered {FrameCount} of {SourceFrameCount} frame(s) from an animated sticker of {ContentLength} bytes in {ElapsedMilliseconds} ms")]
        public static partial void AnimationRendered(ILogger logger, int contentLength, int frameCount, int sourceFrameCount, long elapsedMilliseconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Skottie could not parse an animated sticker of {ContentLength} bytes ({JsonLength} bytes of JSON)")]
        public static partial void AnimationNotParsed(ILogger logger, int contentLength, int jsonLength);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Animated sticker of {ContentLength} bytes unpacks into more than {MaxJsonLength} bytes of JSON")]
        public static partial void AnimationTooLarge(ILogger logger, int contentLength, int maxJsonLength);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to render an animated sticker of {ContentLength} bytes")]
        public static partial void RenderFailed(ILogger logger, int contentLength, Exception exception);
    }
}
