using System;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.Llm.Vision;

/// <summary>
///     Всё, что нужно знать vision-модели о картинке, которую её просят описать.
/// </summary>
public sealed class ImageRecognitionRequest
{
    public ImageRecognitionRequest(
        byte[] content,
        string mediaType,
        DbMediaKind kind,
        bool isAnimated,
        string? relatedText)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        Content = content;
        MediaType = mediaType;
        Kind = kind;
        IsAnimated = isAnimated;
        RelatedText = relatedText;
    }

    /// <summary>
    ///     Содержимое картинки.
    /// </summary>
    public byte[] Content { get; }

    /// <summary>
    ///     Media type картинки: JPEG, PNG, WEBP или GIF.
    /// </summary>
    public string MediaType { get; }

    /// <summary>
    ///     Чем картинка была в чате: обычным изображением или стикером.
    ///     Стикер описывается иначе - у него важны эмоция и подпись, а не композиция кадра.
    /// </summary>
    public DbMediaKind Kind { get; }

    /// <summary>
    ///     Картинка - статический кадр из анимированного или видео-стикера.
    /// </summary>
    public bool IsAnimated { get; }

    /// <summary>
    ///     Текст, с которым картинка пришла в чат (подпись). Нужен, чтобы модель уделила
    ///     внимание релевантным деталям. Может отсутствовать.
    /// </summary>
    public string? RelatedText { get; }
}
