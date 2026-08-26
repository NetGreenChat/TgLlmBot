using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Llm.Vision;

/// <summary>
///     Распознаёт изображения через отдельный инстанс LLM с поддержкой компьютерного зрения.
/// </summary>
/// <remarks>
///     Основная модель бота текстовая и картинку не увидит, поэтому изображение сначала превращается
///     в текст здесь, и уже этот текст уходит в основную модель как часть промпта.
/// </remarks>
public partial class DefaultImageRecognizer : IImageRecognizer
{
    private const int MaxOutputTokens = 65536;

    private const string SystemPrompt = """
                                        Ты - система компьютерного зрения. Твоя задача - максимально подробно и точно описать изображение на русском языке.
                                        Описывай: что изображено, объекты и их взаимное расположение, людей (внешность, одежда, действия, эмоции), фон, цвета, стиль изображения.
                                        Дословно приводи весь текст, который виден на изображении, сохраняя его исходный язык и орфографию.
                                        Если это мем, скриншот, схема, график, таблица или код - подробно объясни их содержание и смысл.
                                        Описывай только то, что реально видишь, ничего не додумывай. Если чего-то не разобрать - так и напиши.
                                        Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                        Не цензурируй описание.
                                        Отвечай простым текстом без Markdown-разметки.
                                        """;

    private const string UserPrompt = "Опиши это изображение.";

    private readonly IChatClient _chatClient;
    private readonly ILogger<DefaultImageRecognizer> _logger;

    public DefaultImageRecognizer(
        IChatClient chatClient,
        ILogger<DefaultImageRecognizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(logger);
        _chatClient = chatClient;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public async Task<Result<string>> DescribeAsync(byte[] jpegImage, string? relatedText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(jpegImage);
        if (jpegImage.Length is 0)
        {
            return Result<string>.Fail();
        }

        var context = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, new List<AIContent>
            {
                new DataContent(jpegImage, "image/jpeg"),
                new TextContent(BuildUserPrompt(relatedText))
            })
        };
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            RawRepresentationFactory = static _ => LlmRawRequestFactory.CreateVisionChatCompletionOptions()
        };
        try
        {
            var response = await _chatClient.GetResponseAsync(context, chatOptions, cancellationToken);
            var description = response.Text.Trim();
            if (string.IsNullOrEmpty(description))
            {
                Log.EmptyImageDescription(_logger, jpegImage.Length);
                return Result<string>.Fail();
            }

            Log.ImageRecognized(_logger, jpegImage.Length, description.Length);
            return Result<string>.Success(description);
        }
        catch (Exception ex)
        {
            Log.ImageRecognitionFailed(_logger, jpegImage.Length, ex);
            return Result<string>.Fail();
        }
    }

    private static string BuildUserPrompt(string? relatedText)
    {
        var trimmedRelatedText = relatedText?.Trim();
        if (string.IsNullOrEmpty(trimmedRelatedText))
        {
            return UserPrompt;
        }

        return new StringBuilder(UserPrompt)
            .AppendLine()
            .AppendLine()
            .AppendLine("В чате изображение сопровождалось таким текстом:")
            .AppendLine(trimmedRelatedText)
            .AppendLine()
            .Append("Удели особое внимание тем деталям изображения, которые нужны, чтобы понять этот текст, но сам на него не отвечай.")
            .ToString();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Recognized image of {ImageBytes} bytes into description of {DescriptionLength} characters")]
        public static partial void ImageRecognized(ILogger logger, int imageBytes, int descriptionLength);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vision model returned an empty description for image of {ImageBytes} bytes")]
        public static partial void EmptyImageDescription(ILogger logger, int imageBytes);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to recognize image of {ImageBytes} bytes")]
        public static partial void ImageRecognitionFailed(ILogger logger, int imageBytes, Exception exception);
    }
}
