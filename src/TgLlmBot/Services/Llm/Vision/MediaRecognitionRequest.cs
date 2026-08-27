using System;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm.Vision;

/// <summary>
///     Всё, что нужно знать vision-модели о вложении, которое её просят описать.
/// </summary>
public sealed class MediaRecognitionRequest
{
    public MediaRecognitionRequest(
        PreparedMedia media,
        DbMediaKind kind,
        bool isAnimated,
        string? relatedText)
    {
        ArgumentNullException.ThrowIfNull(media);
        Media = media;
        Kind = kind;
        IsAnimated = isAnimated;
        RelatedText = relatedText;
    }

    /// <summary>
    ///     Подготовленное вложение: картинка, файл видео или цепочка кадров анимации.
    /// </summary>
    public PreparedMedia Media { get; }

    /// <summary>
    ///     Чем вложение было в чате: картинкой, стикером, гифкой или видео.
    ///     От этого зависит промпт: у стикера важны эмоция и подпись, а не композиция кадра.
    /// </summary>
    public DbMediaKind Kind { get; }

    /// <summary>
    ///     Вложение движется. Вместе с видом подготовленного вложения показывает, увидит модель
    ///     движение или только один кадр: у анимации, для которой не нашлось ничего, кроме
    ///     статического превью, <see cref="Media" /> будет картинкой.
    /// </summary>
    public bool IsAnimated { get; }

    /// <summary>
    ///     Текст, с которым вложение пришло в чат (подпись). Нужен, чтобы модель уделила
    ///     внимание релевантным деталям. Может отсутствовать.
    /// </summary>
    public string? RelatedText { get; }
}
