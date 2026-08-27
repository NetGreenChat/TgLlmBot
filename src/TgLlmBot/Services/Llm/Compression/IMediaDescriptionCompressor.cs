using System.Threading;
using System.Threading.Tasks;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Llm.Compression;

/// <summary>
///     Ужимает подробное описание вложения до размера, с которым его не жалко таскать
///     в контексте вместе со всей историей чата.
/// </summary>
/// <remarks>
///     Жмёт основная модель, а не vision: решать, что из описания важно сохранить, надо
///     с оглядкой на переписку, а переписку видит только она.
/// </remarks>
public interface IMediaDescriptionCompressor
{
    /// <summary>
    ///     Сжимает описание вложения.
    /// </summary>
    /// <returns>
    ///     Сжатое описание либо признак неудачи - тогда в истории останется обрезанное подробное.
    /// </returns>
    Task<Result<string>> CompressAsync(MediaCompressionRequest request, CancellationToken cancellationToken);
}
