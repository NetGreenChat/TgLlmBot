using System;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Скачанный из Telegram файл вложения вместе с распознанным форматом.
/// </summary>
public sealed class DownloadedMedia
{
    public DownloadedMedia(byte[] content, MediaFormat format)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        Format = format;
        MediaType = MediaFormatDetector.ToMediaType(format);
    }

    /// <summary>
    ///     Содержимое файла.
    /// </summary>
    public byte[] Content { get; }

    /// <summary>
    ///     Формат, определённый по сигнатуре содержимого.
    /// </summary>
    public MediaFormat Format { get; }

    /// <summary>
    ///     Media type, соответствующий <see cref="Format" />.
    /// </summary>
    public string MediaType { get; }
}
