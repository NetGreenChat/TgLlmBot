using System.Threading;
using System.Threading.Tasks;
using TgLlmBot.Models;

namespace TgLlmBot.Services.DataAccess.MediaDescriptions;

/// <summary>
///     Кэш описаний вложений по стабильному идентификатору файла в Telegram.
/// </summary>
/// <remarks>
///     Мемы и стикеры прилетают в чат одни и те же по многу раз. Прогонять их через vision-модель
///     каждый раз - минуты ожидания на ровном месте, поэтому описание переиспользуется.
/// </remarks>
public interface IMediaDescriptionCache
{
    /// <summary>
    ///     Возвращает ранее полученное описание файла, если оно есть.
    /// </summary>
    Task<Result<CachedMediaDescription>> TryGetAsync(string fileUniqueId, CancellationToken cancellationToken);

    /// <summary>
    ///     Запоминает описание файла.
    /// </summary>
    /// <param name="fileUniqueId">Стабильный идентификатор файла в Telegram.</param>
    /// <param name="description">Описание вложения.</param>
    /// <param name="isFallback">
    ///     Описание снято со статического превью, а не с самого файла.
    /// </param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <remarks>
    ///     Повторная запись ничего не меняет - кроме одного случая: описание, снятое с самого файла,
    ///     вытесняет то, что раньше сняли с превью. Иначе один сбой рендеринга навсегда оставил бы
    ///     анимированный стикер одним кадром.
    /// </remarks>
    Task StoreAsync(string fileUniqueId, string description, bool isFallback, CancellationToken cancellationToken);
}
