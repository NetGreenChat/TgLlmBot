using System;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Скачанный из Telegram файл вложения вместе с распознанным форматом.
/// </summary>
public sealed class DownloadedMedia
{
    public DownloadedMedia(byte[] content, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        Content = content;
        MediaType = mediaType;
    }

    /// <summary>
    ///     Содержимое файла.
    /// </summary>
    public byte[] Content { get; }

    /// <summary>
    ///     Media type, определённый по сигнатуре содержимого.
    /// </summary>
    public string MediaType { get; }
}
