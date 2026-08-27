using System.Threading;
using System.Threading.Tasks;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Превращает вложение сообщения в то, что vision-модель сможет открыть: качает файл,
///     опознаёт формат и, если нужно, разворачивает анимацию в кадры.
/// </summary>
public interface IMediaPreparer
{
    /// <summary>
    ///     Готовит вложение к показу модели.
    /// </summary>
    /// <param name="media">Метаданные вложения из истории чата.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>
    ///     Подготовленное вложение либо признак неудачи - если показать модели нечего:
    ///     файл не скачался, формат не опознан, а статического превью нет или оно тоже не далось.
    /// </returns>
    Task<Result<PreparedMedia>> PrepareAsync(DbChatMessageMedia media, CancellationToken cancellationToken);
}
