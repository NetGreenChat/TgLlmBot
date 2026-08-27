using System;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Определяет формат картинки по сигнатуре её первых байт.
/// </summary>
/// <remarks>
///     Telegram про формат скачанного файла не рассказывает, а vLLM открывает картинку через PIL,
///     игнорируя media type из data-url. Тип всё равно определяем сами: так в модель заведомо
///     не уедет TGS, WEBM или прочее, что PIL открыть не сможет.
/// </remarks>
public static class ImageFormatDetector
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";
    public const string Gif = "image/gif";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    ///     Возвращает media type картинки либо <see langword="null" />,
    ///     если формат не опознан и показывать такое модели не стоит.
    /// </summary>
    public static string? DetectMediaType(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12)
        {
            return null;
        }

        if (content[0] is 0xFF && content[1] is 0xD8 && content[2] is 0xFF)
        {
            return Jpeg;
        }

        if (content[..8].SequenceEqual(PngSignature))
        {
            return Png;
        }

        // RIFF....WEBP
        if (content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        if (content[..6].SequenceEqual("GIF87a"u8) || content[..6].SequenceEqual("GIF89a"u8))
        {
            return Gif;
        }

        return null;
    }
}
