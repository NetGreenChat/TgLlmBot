namespace TgLlmBot.DataAccess.Models;

/// <summary>
///     Вид вложения, приложенного к сообщению чата.
/// </summary>
public enum DbMediaKind
{
    /// <summary>
    ///     Вид вложения определить не удалось.
    /// </summary>
    Unknown = 0,

    /// <summary>
    ///     Обычная картинка (Telegram photo).
    /// </summary>
    Photo = 1,

    /// <summary>
    ///     Стикер: статический (WEBP), анимированный (TGS) или видео (WEBM).
    /// </summary>
    Sticker = 2,

    /// <summary>
    ///     Гифка: Telegram хранит её как беззвучное видео (MP4), реже - как настоящий GIF.
    /// </summary>
    Animation = 3,

    /// <summary>
    ///     Видео: обычное или круглое видеосообщение.
    /// </summary>
    Video = 4
}
