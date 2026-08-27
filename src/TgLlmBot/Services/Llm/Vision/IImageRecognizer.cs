using System.Threading;
using System.Threading.Tasks;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Llm.Vision;

/// <summary>
///     Распознаёт изображения отдельной мультимодальной моделью и превращает их в текстовое описание,
///     пригодное для передачи в основную (текстовую) LLM.
/// </summary>
public interface IImageRecognizer
{
    /// <summary>
    ///     Описывает переданную картинку текстом.
    /// </summary>
    /// <param name="request">Картинка и всё, что о ней известно.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Текстовое описание изображения либо признак неудачи.</returns>
    Task<Result<string>> DescribeAsync(ImageRecognitionRequest request, CancellationToken cancellationToken);
}
