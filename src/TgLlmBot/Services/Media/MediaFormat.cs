namespace TgLlmBot.Services.Media;

/// <summary>
///     Формат скачанного из Telegram файла вложения, опознанный по сигнатуре его первых байт.
/// </summary>
public enum MediaFormat
{
    /// <summary>
    ///     JPEG.
    /// </summary>
    Jpeg = 1,

    /// <summary>
    ///     PNG.
    /// </summary>
    Png = 2,

    /// <summary>
    ///     WEBP: в него Telegram упаковывает статические стикеры и превью.
    /// </summary>
    WebP = 3,

    /// <summary>
    ///     GIF. Обычные гифки Telegram отдаёт как MP4, так что встречается редко.
    /// </summary>
    Gif = 4,

    /// <summary>
    ///     WEBM: контейнер видео-стикеров (VP9).
    /// </summary>
    WebM = 5,

    /// <summary>
    ///     MP4: контейнер гифок и видео (H.264/H.265).
    /// </summary>
    Mp4 = 6,

    /// <summary>
    ///     TGS: анимированный стикер, gzip-нутый Lottie-JSON. Единственный формат,
    ///     который приходится разворачивать в кадры своими руками.
    /// </summary>
    LottieSticker = 7
}
