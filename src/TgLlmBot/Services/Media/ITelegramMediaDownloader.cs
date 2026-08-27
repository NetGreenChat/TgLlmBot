using System.Threading;
using System.Threading.Tasks;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Скачивает файлы вложений из Telegram и отдаёт только то, формат чего удалось опознать.
/// </summary>
public interface ITelegramMediaDownloader
{
    /// <summary>
    ///     Скачивает файл по его идентификатору.
    /// </summary>
    /// <param name="fileId">Идентификатор файла в Telegram.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>
    ///     Содержимое файла с определённым по сигнатуре форматом либо признак неудачи -
    ///     если файл не скачался, оказался пустым, слишком большим или неизвестного формата.
    /// </returns>
    Task<Result<DownloadedMedia>> DownloadAsync(string fileId, CancellationToken cancellationToken);
}
