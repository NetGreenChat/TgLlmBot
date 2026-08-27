namespace TgLlmBot.DataAccess.Models;

/// <summary>
///     Состояние обработки вложения.
/// </summary>
public enum DbMediaRecognitionStatus
{
    /// <summary>
    ///     Вложение поставлено в очередь, но ещё не распознано.
    /// </summary>
    Pending = 0,

    /// <summary>
    ///     Сжатое описание готово и лежит в <see cref="DbChatMessageMedia.ShortDescription" /> -
    ///     именно оно уходит в историю чата.
    /// </summary>
    Ready = 1,

    /// <summary>
    ///     Распознать не удалось: не скачалось, не открылось или vision-модель вернула ошибку.
    /// </summary>
    Failed = 2,

    /// <summary>
    ///     Показать модели нечего: у вложения нет ни картинки, ни статического превью
    ///     (например, анимированный стикер без thumbnail).
    /// </summary>
    Unsupported = 3
}
