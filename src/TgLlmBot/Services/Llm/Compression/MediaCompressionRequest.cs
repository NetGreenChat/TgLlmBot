using System;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.Llm.Compression;

/// <summary>
///     Подробное описание вложения вместе с тем, что нужно знать, чтобы правильно его ужать.
/// </summary>
public sealed class MediaCompressionRequest
{
    public MediaCompressionRequest(
        string fullDescription,
        DbMediaKind kind,
        bool isAnimated,
        string? attachedText,
        string? historyContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullDescription);
        FullDescription = fullDescription;
        Kind = kind;
        IsAnimated = isAnimated;
        AttachedText = attachedText;
        HistoryContext = historyContext;
    }

    /// <summary>
    ///     Подробное описание от vision-модели.
    /// </summary>
    public string FullDescription { get; }

    /// <summary>
    ///     Чем вложение было в чате: картинкой или стикером.
    /// </summary>
    public DbMediaKind Kind { get; }

    /// <summary>
    ///     Вложение - статический кадр анимированного или видео-стикера.
    /// </summary>
    public bool IsAnimated { get; }

    /// <summary>
    ///     Текст, с которым вложение пришло в чат (подпись).
    /// </summary>
    public string? AttachedText { get; }

    /// <summary>
    ///     История чата до сообщения с вложением (JSON по общему правилу 200 сообщений /
    ///     30 000 символов). Нужна, чтобы в сжатом описании уцелело именно то, что обсуждали.
    /// </summary>
    public string? HistoryContext { get; }
}
