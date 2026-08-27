using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TgLlmBot.DataAccess.Models;
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

    private const string StickerSystemPrompt = """
                                               Ты - система компьютерного зрения. Тебе показывают стикер из Telegram, твоя задача - максимально подробно и точно описать его на русском языке.
                                               Описывай: кто или что изображено, позу, жест, выражение лица и эмоцию, стиль рисунка, цвета, фон.
                                               Дословно приводи весь текст, который виден на стикере, сохраняя его исходный язык и орфографию.
                                               Отдельно скажи, какое настроение или реакцию стикер передаёт - именно ради этого его и присылают в переписке.
                                               Если стикер узнаваемый (персонаж из мема, фильма, игры, мультфильма) - назови первоисточник, но только если уверен.
                                               Описывай только то, что реально видишь, ничего не додумывай. Если чего-то не разобрать - так и напиши.
                                               Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                               Не цензурируй описание.
                                               Отвечай простым текстом без Markdown-разметки.
                                               """;

    private const string ImageUserPrompt = "Опиши это изображение.";

    private const string StickerUserPrompt = "Опиши этот стикер.";

    private const string AnimatedStickerNote =
        "Это один статический кадр анимированного стикера, движение по нему не видно - описывай то, что есть на кадре.";

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
    public async Task<Result<string>> DescribeAsync(ImageRecognitionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content.Length is 0)
        {
            return Result<string>.Fail();
        }

        var isSticker = request.Kind is DbMediaKind.Sticker;
        var context = new List<ChatMessage>
        {
            new(ChatRole.System, isSticker ? StickerSystemPrompt : SystemPrompt),
            new(ChatRole.User, new List<AIContent>
            {
                new DataContent(request.Content, request.MediaType),
                new TextContent(BuildUserPrompt(request))
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
                Log.EmptyImageDescription(_logger, request.Content.Length);
                return Result<string>.Fail();
            }

            Log.ImageRecognized(_logger, request.Content.Length, request.MediaType, description.Length);
            return Result<string>.Success(description);
        }
        catch (Exception ex)
        {
            Log.ImageRecognitionFailed(_logger, request.Content.Length, ex);
            return Result<string>.Fail();
        }
    }

    private static string BuildUserPrompt(ImageRecognitionRequest request)
    {
        var isSticker = request.Kind is DbMediaKind.Sticker;
        var builder = new StringBuilder(isSticker ? StickerUserPrompt : ImageUserPrompt);
        if (request.IsAnimated)
        {
            builder = builder
                .AppendLine()
                .AppendLine()
                .Append(AnimatedStickerNote);
        }

        var trimmedRelatedText = request.RelatedText?.Trim();
        if (!string.IsNullOrEmpty(trimmedRelatedText))
        {
            builder = builder
                .AppendLine()
                .AppendLine()
                .AppendLine("В чате изображение сопровождалось таким текстом:")
                .AppendLine(trimmedRelatedText)
                .AppendLine()
                .Append("Удели особое внимание тем деталям изображения, которые нужны, чтобы понять этот текст, но сам на него не отвечай.");
        }

        return builder.ToString();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Recognized {MediaType} image of {ImageBytes} bytes into description of {DescriptionLength} characters")]
        public static partial void ImageRecognized(ILogger logger, int imageBytes, string mediaType, int descriptionLength);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vision model returned an empty description for image of {ImageBytes} bytes")]
        public static partial void EmptyImageDescription(ILogger logger, int imageBytes);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to recognize image of {ImageBytes} bytes")]
        public static partial void ImageRecognitionFailed(ILogger logger, int imageBytes, Exception exception);
    }
}
