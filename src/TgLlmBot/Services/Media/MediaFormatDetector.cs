using System;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Определяет формат скачанного вложения по сигнатуре его первых байт.
/// </summary>
/// <remarks>
///     Telegram про формат скачанного файла не рассказывает, а vLLM разбирается сам: картинки
///     открывает через PIL, видео - через OpenCV, media type из data-url при этом игнорирует.
///     Формат мы всё равно определяем сами: от него зависит, что именно показывать модели -
///     картинку, файл видео целиком или кадры, отрендеренные из Lottie-анимации у себя.
/// </remarks>
public static class MediaFormatDetector
{
    public const string JpegMediaType = "image/jpeg";
    public const string PngMediaType = "image/png";
    public const string WebPMediaType = "image/webp";
    public const string GifMediaType = "image/gif";
    public const string WebMMediaType = "video/webm";
    public const string Mp4MediaType = "video/mp4";

    /// <summary>
    ///     Своего media type у TGS нет: это обычный gzip, внутри которого лежит Lottie-JSON.
    /// </summary>
    public const string LottieStickerMediaType = "application/gzip";

    /// <summary>
    ///     Media type цепочки кадров: по нему vLLM понимает, что в data-url лежит не один файл,
    ///     а несколько JPEG через запятую, и собирает из них видео.
    /// </summary>
    public const string JpegFramesMediaType = "video/jpeg";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> GzipSignature => [0x1F, 0x8B];

    /// <summary>
    ///     EBML-заголовок Matroska, с которого начинается WEBM.
    /// </summary>
    private static ReadOnlySpan<byte> EbmlSignature => [0x1A, 0x45, 0xDF, 0xA3];

    /// <summary>
    ///     Возвращает формат вложения либо <see langword="null" />, если сигнатура неизвестна
    ///     и показывать такое модели не стоит.
    /// </summary>
    public static MediaFormat? Detect(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12)
        {
            return null;
        }

        if (content[0] is 0xFF && content[1] is 0xD8 && content[2] is 0xFF)
        {
            return MediaFormat.Jpeg;
        }

        if (content[..8].SequenceEqual(PngSignature))
        {
            return MediaFormat.Png;
        }

        // RIFF....WEBP
        if (content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return MediaFormat.WebP;
        }

        if (content[..6].SequenceEqual("GIF87a"u8) || content[..6].SequenceEqual("GIF89a"u8))
        {
            return MediaFormat.Gif;
        }

        if (content[..2].SequenceEqual(GzipSignature))
        {
            return MediaFormat.LottieSticker;
        }

        if (content[..4].SequenceEqual(EbmlSignature))
        {
            return MediaFormat.WebM;
        }

        // ....ftyp: размер бокса, за ним его тип
        if (content[4..8].SequenceEqual("ftyp"u8))
        {
            return MediaFormat.Mp4;
        }

        return null;
    }

    /// <summary>
    ///     Media type, который уедет в data-url запроса к модели.
    /// </summary>
    public static string ToMediaType(MediaFormat format)
    {
        return format switch
        {
            MediaFormat.Jpeg => JpegMediaType,
            MediaFormat.Png => PngMediaType,
            MediaFormat.WebP => WebPMediaType,
            MediaFormat.Gif => GifMediaType,
            MediaFormat.WebM => WebMMediaType,
            MediaFormat.Mp4 => Mp4MediaType,
            MediaFormat.LottieSticker => LottieStickerMediaType,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown media format.")
        };
    }

    /// <summary>
    ///     Формат, который модель откроет как обычную статическую картинку.
    /// </summary>
    public static bool IsImage(MediaFormat format)
    {
        return format is MediaFormat.Jpeg or MediaFormat.Png or MediaFormat.WebP or MediaFormat.Gif;
    }

    /// <summary>
    ///     Формат, который модель откроет как видео и разложит на кадры сама.
    /// </summary>
    public static bool IsVideo(MediaFormat format)
    {
        return format is MediaFormat.WebM or MediaFormat.Mp4;
    }
}
