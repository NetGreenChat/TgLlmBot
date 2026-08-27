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
    Task<Result<string>> TryGetAsync(string fileUniqueId, CancellationToken cancellationToken);

    /// <summary>
    ///     Запоминает описание файла. Повторная запись существующего описания ничего не меняет.
    /// </summary>
    Task StoreAsync(string fileUniqueId, string description, CancellationToken cancellationToken);
}
