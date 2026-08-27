using System;
using System.Linq;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Вложение, готовое к показу vision-модели: собранный data-url и то, чем этот data-url
///     для модели является.
/// </summary>
/// <remarks>
///     Цепочка кадров отправляется тем же способом, что и видео: data-url вида
///     <c>data:video/jpeg;base64,кадр1,кадр2,...</c> - так его понимает vLLM, а модель
///     видит обычное видео и получает метки времени по <see cref="Animation" />.
/// </remarks>
public sealed class PreparedMedia
{
    private PreparedMedia(
        PreparedMediaKind kind,
        string dataUrl,
        int payloadBytes,
        RenderedAnimation? animation,
        bool isThumbnailFallback)
    {
        Kind = kind;
        DataUrl = dataUrl;
        PayloadBytes = payloadBytes;
        Animation = animation;
        IsThumbnailFallback = isThumbnailFallback;
    }

    /// <summary>
    ///     Чем вложение является для модели.
    /// </summary>
    public PreparedMediaKind Kind { get; }

    /// <summary>
    ///     Готовый data-url, который уедет в запрос.
    /// </summary>
    public string DataUrl { get; }

    /// <summary>
    ///     Размер полезной нагрузки до base64 - для логов.
    /// </summary>
    public int PayloadBytes { get; }

    /// <summary>
    ///     Тайминг исходной анимации. Есть только у <see cref="PreparedMediaKind.RenderedFrames" />:
    ///     для видео целиком его определит сам сервер модели при декодировании.
    /// </summary>
    public RenderedAnimation? Animation { get; }

    /// <summary>
    ///     Модели показывается статическое превью от Telegram, а не само вложение: с основным
    ///     файлом не сложилось.
    /// </summary>
    /// <remarks>
    ///     Такое описание заведомо хуже того, что вышло бы из самого файла, - по нему не судят
    ///     о движении и его не стоит запоминать в кэше как окончательное.
    /// </remarks>
    public bool IsThumbnailFallback { get; }

    /// <summary>
    ///     Статическая картинка: показывается модели как есть.
    /// </summary>
    public static PreparedMedia Image(byte[] content, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        return new(
            PreparedMediaKind.Image,
            BuildDataUrl(mediaType, Convert.ToBase64String(content)),
            content.Length,
            animation: null,
            isThumbnailFallback: false);
    }

    /// <summary>
    ///     Статическое превью от Telegram вместо самого вложения.
    /// </summary>
    public static PreparedMedia ThumbnailImage(byte[] content, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        return new(
            PreparedMediaKind.Image,
            BuildDataUrl(mediaType, Convert.ToBase64String(content)),
            content.Length,
            animation: null,
            isThumbnailFallback: true);
    }

    /// <summary>
    ///     Файл видео целиком: декодировать и нарезать кадры будет сервер модели.
    /// </summary>
    public static PreparedMedia VideoFile(byte[] content, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        return new(
            PreparedMediaKind.VideoFile,
            BuildDataUrl(mediaType, Convert.ToBase64String(content)),
            content.Length,
            animation: null,
            isThumbnailFallback: false);
    }

    /// <summary>
    ///     Цепочка кадров, отрендеренных из анимации у себя.
    /// </summary>
    public static PreparedMedia RenderedFrames(RenderedAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        var frames = string.Join(',', animation.Frames.Select(Convert.ToBase64String));
        return new(
            PreparedMediaKind.RenderedFrames,
            BuildDataUrl(MediaFormatDetector.JpegFramesMediaType, frames),
            animation.Frames.Sum(static x => x.Length),
            animation,
            isThumbnailFallback: false);
    }

    private static string BuildDataUrl(string mediaType, string base64Payload)
    {
        return string.Concat("data:", mediaType, ";base64,", base64Payload);
    }
}
