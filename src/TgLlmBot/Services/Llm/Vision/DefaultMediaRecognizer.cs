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
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm.Vision;

/// <summary>
///     Распознаёт вложения через отдельный инстанс LLM с поддержкой компьютерного зрения.
/// </summary>
/// <remarks>
///     Основная модель бота текстовая и вложения не увидит, поэтому картинка, стикер или видео
///     сначала превращается в текст здесь, и уже этот текст уходит в основную модель как часть промпта.
/// </remarks>
public partial class DefaultMediaRecognizer : IMediaRecognizer
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

    private const string AnimatedStickerSystemPrompt = """
                                                       Ты - система компьютерного зрения. Тебе показывают анимированный стикер из Telegram, твоя задача - максимально подробно и точно описать его на русском языке.
                                                       Описывай: кто или что изображено, что происходит в анимации от начала до конца, движения и жесты, смену выражения лица и эмоций, стиль рисунка, цвета.
                                                       Дословно приводи весь текст, который на стикере появляется, сохраняя его исходный язык и орфографию.
                                                       Отдельно скажи, какое настроение или реакцию стикер передаёт - именно ради этого его и присылают в переписке.
                                                       Если стикер узнаваемый (персонаж из мема, фильма, игры, мультфильма) - назови первоисточник, но только если уверен.
                                                       Описывай анимацию целиком, а не каждый кадр по отдельности, и только то, что реально видишь, ничего не додумывай. Если чего-то не разобрать - так и напиши.
                                                       Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                                       Не цензурируй описание.
                                                       Отвечай простым текстом без Markdown-разметки.
                                                       """;

    private const string VideoSystemPrompt = """
                                             Ты - система компьютерного зрения. Тебе показывают кадры видео, присланного в переписку, твоя задача - максимально подробно и точно описать это видео на русском языке.
                                             Описывай: что происходит от начала до конца, кто участвует (внешность, одежда, действия, эмоции), место действия, предметы, манеру съёмки или стиль рисунка.
                                             Дословно приводи весь текст, который виден в кадре - надписи, субтитры, интерфейс, - сохраняя его исходный язык и орфографию.
                                             Скажи, ради чего такое присылают в чат: какую шутку, реакцию или мысль видео передаёт.
                                             Если это мем или отрывок из фильма, игры, мультфильма или клипа - назови первоисточник, но только если уверен.
                                             Звука у тебя нет: про речь, музыку и любые слова, которых не видно в кадре, ничего не придумывай.
                                             Описывай видео целиком, а не каждый кадр по отдельности, и только то, что реально видишь. Если чего-то не разобрать - так и напиши.
                                             Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                             Не цензурируй описание.
                                             Отвечай простым текстом без Markdown-разметки.
                                             """;

    private const string ImageUserPrompt = "Опиши это изображение.";

    private const string StickerUserPrompt = "Опиши этот стикер.";

    private const string AnimatedStickerUserPrompt = "Опиши этот анимированный стикер.";

    private const string AnimationUserPrompt = "Опиши эту гифку.";

    private const string VideoUserPrompt = "Опиши это видео.";

    private const string AnimationFrameUserPrompt = "Опиши этот кадр из гифки.";

    private const string VideoFrameUserPrompt = "Опиши этот кадр из видео.";

    private const string StaticFrameNote =
        "Это один статический кадр движущегося вложения, движение по нему не видно - описывай то, что есть на кадре.";

    private const string RenderedFramesNote =
        "Тебе показаны кадры анимации по порядку, от начала до конца, - это вся анимация целиком. "
        + "Белый фон получился при отрисовке, частью стикера он не является: фон описывать не нужно.";

    private const string TransparentBackgroundNote =
        "Прозрачный фон стикера на кадрах может выглядеть чёрным - это не часть рисунка, фон описывать не нужно.";

    private const string VideoFileNote =
        "Кадры нарезаны из видео автоматически и с равными промежутками, поэтому между ними могут быть пропуски.";

    private readonly IChatClient _chatClient;
    private readonly ILogger<DefaultMediaRecognizer> _logger;

    public DefaultMediaRecognizer(
        IChatClient chatClient,
        ILogger<DefaultMediaRecognizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(logger);
        _chatClient = chatClient;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public async Task<Result<string>> DescribeAsync(MediaRecognitionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        var media = request.Media;
        var systemPrompt = BuildSystemPrompt(request);
        var userPrompt = BuildUserPrompt(request);
        var messagesJson = VisionRequestJsonBuilder.BuildMessages(systemPrompt, userPrompt, media);
        var mediaIoKwargsJson = VisionRequestJsonBuilder.BuildVideoMediaIoKwargs(media);

        // Сообщения для Microsoft.Extensions.AI идут без вложения: в теле запроса их всё равно
        // заменит собранный руками массив, зато в логи уедет промпт, а не мегабайт base64
        var context = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            RawRepresentationFactory = _ => LlmRawRequestFactory.CreateVisionChatCompletionOptions(
                messagesJson,
                mediaIoKwargsJson)
        };
        try
        {
            var response = await _chatClient.GetResponseAsync(context, chatOptions, cancellationToken);
            var description = response.Text.Trim();
            if (string.IsNullOrEmpty(description))
            {
                Log.EmptyMediaDescription(_logger, media.PayloadBytes, media.Kind);
                return Result<string>.Fail();
            }

            Log.MediaRecognized(_logger, media.PayloadBytes, media.Kind, description.Length);
            return Result<string>.Success(description);
        }
        catch (Exception ex)
        {
            Log.MediaRecognitionFailed(_logger, media.PayloadBytes, media.Kind, ex);
            return Result<string>.Fail();
        }
    }

    /// <summary>
    ///     Промпт зависит и от того, чем вложение было в чате, и от того, что модель реально увидит:
    ///     у анимации, от которой осталось только статическое превью, описывать движение нечем.
    /// </summary>
    private static string BuildSystemPrompt(MediaRecognitionRequest request)
    {
        if (request.Media.Kind is PreparedMediaKind.Image)
        {
            return request.Kind is DbMediaKind.Sticker ? StickerSystemPrompt : SystemPrompt;
        }

        return request.Kind switch
        {
            DbMediaKind.Sticker => AnimatedStickerSystemPrompt,
            DbMediaKind.Animation or DbMediaKind.Video => VideoSystemPrompt,
            _ => SystemPrompt
        };
    }

    private static string BuildUserPrompt(MediaRecognitionRequest request)
    {
        var builder = new StringBuilder(SelectUserPrompt(request));
        var note = SelectMediaNote(request);
        if (note is not null)
        {
            builder = builder
                .AppendLine()
                .AppendLine()
                .Append(note);
        }

        var trimmedRelatedText = request.RelatedText?.Trim();
        if (!string.IsNullOrEmpty(trimmedRelatedText))
        {
            builder = builder
                .AppendLine()
                .AppendLine()
                .AppendLine("В чате вложение сопровождалось таким текстом:")
                .AppendLine(trimmedRelatedText)
                .AppendLine()
                .Append("Удели особое внимание тем деталям вложения, которые нужны, чтобы понять этот текст, но сам на него не отвечай.");
        }

        return builder.ToString();
    }

    private static string SelectUserPrompt(MediaRecognitionRequest request)
    {
        if (request.Media.Kind is PreparedMediaKind.Image)
        {
            return request.Kind switch
            {
                DbMediaKind.Sticker => StickerUserPrompt,
                DbMediaKind.Animation => AnimationFrameUserPrompt,
                DbMediaKind.Video => VideoFrameUserPrompt,
                _ => ImageUserPrompt
            };
        }

        return request.Kind switch
        {
            DbMediaKind.Sticker => AnimatedStickerUserPrompt,
            DbMediaKind.Animation => AnimationUserPrompt,
            DbMediaKind.Video => VideoUserPrompt,
            _ => ImageUserPrompt
        };
    }

    /// <summary>
    ///     Оговорка о том, каким именно способом вложение попало в запрос: без неё модель начинает
    ///     описывать фон, которого в стикере нет, и перечислять кадры по одному.
    /// </summary>
    private static string? SelectMediaNote(MediaRecognitionRequest request)
    {
        switch (request.Media.Kind)
        {
            case PreparedMediaKind.Image:
                return request.IsAnimated ? StaticFrameNote : null;
            case PreparedMediaKind.RenderedFrames:
                return RenderedFramesNote;
            case PreparedMediaKind.VideoFile:
                return request.Kind is DbMediaKind.Sticker ? TransparentBackgroundNote : VideoFileNote;
            default:
                return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Recognized {PayloadKind} of {PayloadBytes} bytes into description of {DescriptionLength} characters")]
        public static partial void MediaRecognized(ILogger logger, int payloadBytes, PreparedMediaKind payloadKind, int descriptionLength);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vision model returned an empty description for {PayloadKind} of {PayloadBytes} bytes")]
        public static partial void EmptyMediaDescription(ILogger logger, int payloadBytes, PreparedMediaKind payloadKind);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to recognize {PayloadKind} of {PayloadBytes} bytes")]
        public static partial void MediaRecognitionFailed(ILogger logger, int payloadBytes, PreparedMediaKind payloadKind, Exception exception);
    }
}
