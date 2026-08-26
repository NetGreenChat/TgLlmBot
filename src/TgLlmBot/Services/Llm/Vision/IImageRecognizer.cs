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
    ///     Описывает переданное изображение текстом.
    /// </summary>
    /// <param name="jpegImage">Содержимое изображения в формате JPEG.</param>
    /// <param name="relatedText">
    ///     Текст, с которым изображение пришло в чат (подпись к картинке или вопрос пользователя).
    ///     Нужен, чтобы модель уделила внимание релевантным деталям. Может отсутствовать.
    /// </param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Текстовое описание изображения либо признак неудачи.</returns>
    Task<Result<string>> DescribeAsync(byte[] jpegImage, string? relatedText, CancellationToken cancellationToken);
}
