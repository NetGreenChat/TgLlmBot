using System.Threading;
using System.Threading.Tasks;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Llm.Vision;

/// <summary>
///     Распознаёт вложения отдельной мультимодальной моделью и превращает их в текстовое описание,
///     пригодное для передачи в основную (текстовую) LLM.
/// </summary>
public interface IMediaRecognizer
{
    /// <summary>
    ///     Описывает переданное вложение текстом.
    /// </summary>
    /// <param name="request">Вложение и всё, что о нём известно.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Текстовое описание вложения либо признак неудачи.</returns>
    Task<Result<string>> DescribeAsync(MediaRecognitionRequest request, CancellationToken cancellationToken);
}
