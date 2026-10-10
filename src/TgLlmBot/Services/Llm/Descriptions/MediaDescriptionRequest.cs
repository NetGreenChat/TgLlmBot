using System;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm.Descriptions;

/// <summary>
///     Всё, что нужно знать о вложении, чтобы описать его для истории чата.
/// </summary>
public sealed class MediaDescriptionRequest
{
    public MediaDescriptionRequest(
        PreparedMedia media,
        DbMediaKind kind,
        bool isAnimated,
        string? relatedText,
        string? historyContext)
    {
        ArgumentNullException.ThrowIfNull(media);
        Media = media;
        Kind = kind;
        IsAnimated = isAnimated;
        RelatedText = relatedText;
        HistoryContext = historyContext;
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
    ///     Текст, с которым вложение пришло в чат (подпись). Нужен, чтобы в описании уцелели
    ///     детали, без которых текст не понять. Может отсутствовать.
    /// </summary>
    public string? RelatedText { get; }

    /// <summary>
    ///     История чата до сообщения с вложением - текстом в формате
    ///     <see cref="ChatTranscriptFormat" /> (по общему правилу 200 сообщений / 30 000 символов).
    ///     Нужна, чтобы понять, какие детали вложения важно сохранить.
    /// </summary>
    public string? HistoryContext { get; }
}
