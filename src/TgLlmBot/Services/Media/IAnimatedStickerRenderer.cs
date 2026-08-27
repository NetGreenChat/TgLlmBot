using TgLlmBot.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Разворачивает анимированный стикер Telegram (TGS) в цепочку кадров.
/// </summary>
/// <remarks>
///     TGS - это gzip-нутый Lottie-JSON, то есть векторная анимация: ни один декодер видео её
///     не откроет, поэтому кадры приходится рисовать у себя. Видео-стикеры (WEBM) через этот
///     рендерер не идут - их файл целиком понимает сервер модели.
/// </remarks>
public interface IAnimatedStickerRenderer
{
    /// <summary>
    ///     Рисует из анимации несколько равномерно распределённых по времени кадров.
    /// </summary>
    /// <param name="content">Содержимое файла TGS.</param>
    /// <returns>
    ///     Кадры в JPEG вместе с таймингом исходника либо признак неудачи - если это не Lottie,
    ///     анимация не разобралась или отрисовать её не удалось.
    /// </returns>
    Result<RenderedAnimation> Render(byte[] content);
}
