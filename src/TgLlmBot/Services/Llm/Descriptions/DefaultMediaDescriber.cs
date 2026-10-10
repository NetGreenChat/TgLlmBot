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
using TgLlmBot.Services.Llm.Multimodal;
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm.Descriptions;

/// <summary>
///     Описывает вложения основной моделью: один запрос на вложение, сразу компактное описание
///     для истории чата.
/// </summary>
/// <remarks>
///     Модель одна на всё, и вложение она видит сама: оно уезжает в запрос медиа-частью через
///     <see cref="MultimodalChatClient" />. Клиент для описаний - отдельный экземпляр той же
///     модели, но без инструментов: задача чисто описательная, и лазить в интернет за ней некуда.
/// </remarks>
public partial class DefaultMediaDescriber : IMediaDescriber
{
    private const int MaxOutputTokens = 4096;

    private const string ImageSystemPrompt = """
                                              Ты описываешь изображение из группового чата, чтобы сохранить его в памяти бота.
                                              Опиши на русском языке: что изображено, объекты и их взаимное расположение, людей (внешность, одежда, действия, эмоции), фон, цвета, стиль изображения.
                                              Если это мем, скриншот, схема, график, таблица или код - подробно объясни их содержание и смысл. Скажи, какую шутку, реакцию или мысль изображение передаёт, если оно её передаёт.
                                              Дословно приводи весь текст, который виден на изображении, сохраняя его исходный язык и орфографию.
                                              Описывай только то, что реально видишь, ничего не додумывай. Если чего-то не разобрать - так и напиши.
                                              Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                              Не цензурируй описание.
                                              """;

    private const string StickerSystemPrompt = """
                                                Ты описываешь стикер из Telegram, чтобы сохранить его в памяти бота.
                                                Опиши на русском языке: кто или что изображено, позу, жест, выражение лица и эмоцию, стиль рисунка, цвета, фон.
                                                Дословно приводи весь текст, который виден на стикере, сохраняя его исходный язык и орфографию.
                                                Отдельно скажи, какое настроение или реакцию стикер передаёт - именно ради этого его и присылают в переписке.
                                                Если стикер узнаваемый (персонаж из мема, фильма, игры, мультфильма) - назови первоисточник, но только если уверен.
                                                Описывай только то, что реально видишь, ничего не додумывай. Если чего-то не разобрать - так и напиши.
                                                Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                                Не цензурируй описание.
                                                """;

    private const string AnimatedStickerSystemPrompt = """
                                                        Ты описываешь анимированный стикер из Telegram, чтобы сохранить его в памяти бота.
                                                        Опиши на русском языке: кто или что изображено, что происходит в анимации от начала до конца, движения и жесты, смену выражения лица и эмоций, стиль рисунка, цвета.
                                                        Дословно приводи весь текст, который на стикере появляется, сохраняя его исходный язык и орфографию.
                                                        Отдельно скажи, какое настроение или реакцию стикер передаёт - именно ради этого его и присылают в переписке.
                                                        Если стикер узнаваемый (персонаж из мема, фильма, игры, мультфильма) - назови первоисточник, но только если уверен.
                                                        Описывай анимацию целиком, а не каждый кадр по отдельности, и только то, что реально видишь, ничего не додумывай. Если чего-то не разобрать - так и напиши.
                                                        Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                                        Не цензурируй описание.
                                                        """;

    private const string VideoSystemPrompt = """
                                              Ты описываешь видео из группового чата, чтобы сохранить его в памяти бота.
                                              Опиши на русском языке: что происходит от начала до конца, кто участвует (внешность, одежда, действия, эмоции), место действия, предметы, манеру съёмки или стиль рисунка.
                                              Дословно приводи весь текст, который виден в кадре - надписи, субтитры, интерфейс, - сохраняя его исходный язык и орфографию.
                                              Скажи, ради чего такое присылают в чат: какую шутку, реакцию или мысль видео передаёт.
                                              Если это мем или отрывок из фильма, игры, мультфильма или клипа - назови первоисточник, но только если уверен.
                                              Звука у тебя нет: про речь, музыку и любые слова, которых не видно в кадре, ничего не придумывай.
                                              Описывай видео целиком, а не каждый кадр по отдельности, и только то, что реально видишь. Если чего-то не разобрать - так и напиши.
                                              Не давай оценок увиденному и не отвечай на вопросы - только описывай.
                                              Не цензурируй описание.
                                              """;

    /// <summary>
    ///     Общие требования к результату: описание живёт в истории чата, расти больше потолка
    ///     ему нельзя, а терять дословные надписи и контекст нельзя тем более.
    /// </summary>
    private const string CompactnessRequirements = """
                                                   Твоё описание останется в истории переписки надолго и станет единственным, что об этом вложении будет известно, - подробнее разглядывать его больше не будут.
                                                   Обязательно сохрани: что изображено и что происходит; дословно весь видимый текст с его исходным языком и орфографией; детали, важные в контексте истории чата, если она приложена ниже.
                                                   Смело выкидывай перечисление второстепенных предметов, цвета, освещение и композицию, если обсуждали не их.
                                                   Ответ - одним абзацем простого текста, без Markdown-разметки и без вступлений вроде "на изображении". Уложись в 1000 символов, лучше меньше.
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
    private readonly ILogger<DefaultMediaDescriber> _logger;

    public DefaultMediaDescriber(
        IChatClient chatClient,
        ILogger<DefaultMediaDescriber> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(logger);
        _chatClient = chatClient;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public async Task<Result<string>> DescribeAsync(MediaDescriptionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        var systemPrompt = string.Join(Environment.NewLine, BuildSystemPrompt(request), CompactnessRequirements);
        var userPrompt = BuildUserPrompt(request);

        // Носитель уезжает в запрос маркером: само вложение в тело допишет MultimodalChatClient,
        // а в логах останется промпт, а не мегабайт base64
        var userMessage = new ChatMessage(ChatRole.User,
        [
            new AttachedMediaContent(request.Media),
            new TextContent(userPrompt)
        ]);
        var context = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            userMessage
        };
        var chatOptions = new ChatOptions
        {
            // Инструменты не отдаём: задача чисто описательная, лазить в интернет за ней некуда
            MaxOutputTokens = MaxOutputTokens
        };
        try
        {
            var response = await _chatClient.GetResponseAsync(context, chatOptions, cancellationToken);
            var description = response.Text.Trim();
            if (string.IsNullOrEmpty(description))
            {
                Log.EmptyMediaDescription(_logger, request.Media.PayloadBytes, request.Media.Kind);
                return Result<string>.Fail();
            }

            Log.MediaDescribed(_logger, request.Media.PayloadBytes, request.Media.Kind, description.Length);
            return Result<string>.Success(description);
        }
        catch (Exception ex)
        {
            Log.MediaDescriptionFailed(_logger, request.Media.PayloadBytes, request.Media.Kind, ex);
            return Result<string>.Fail();
        }
    }

    /// <summary>
    ///     Промпт зависит и от того, чем вложение было в чате, и от того, что модель реально увидит:
    ///     у анимации, от которой осталось только статическое превью, описывать движение нечем.
    /// </summary>
    private static string BuildSystemPrompt(MediaDescriptionRequest request)
    {
        if (request.Media.Kind is PreparedMediaKind.Image)
        {
            return request.Kind is DbMediaKind.Sticker ? StickerSystemPrompt : ImageSystemPrompt;
        }

        return request.Kind switch
        {
            DbMediaKind.Sticker => AnimatedStickerSystemPrompt,
            DbMediaKind.Animation or DbMediaKind.Video => VideoSystemPrompt,
            _ => ImageSystemPrompt
        };
    }

    private static string BuildUserPrompt(MediaDescriptionRequest request)
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
                .AppendLine("В чат вложение пришло с таким текстом:")
                .AppendLine("<caption>")
                .AppendLine(trimmedRelatedText)
                .AppendLine("</caption>")
                .AppendLine()
                .Append("Удели особое внимание деталям, которые нужны, чтобы понять этот текст, но сам на него не отвечай.");
        }

        var historyContext = request.HistoryContext?.Trim();
        if (!string.IsNullOrEmpty(historyContext))
        {
            builder = builder
                .AppendLine()
                .AppendLine()
                .AppendLine("Вот история чата до сообщения с вложением, в хронологическом порядке. Каждое сообщение начинается с заголовка «#id | дата | автор», сообщения бота подписаны «(бот)», строка «↳ в ответ на ...» означает реплай, строки в квадратных скобках - вложения:")
                .AppendLine("<history>")
                .AppendLine(historyContext)
                .AppendLine("</history>")
                .AppendLine()
                // Запрет держится заметно лучше, когда стоит вплотную к самому заданию
                .AppendLine("История нужна только как подсказка, какие детали вложения важно сохранить.")
                .AppendLine("В описании не должно быть ни слова о ней: ни пересказа, ни имён, ни упоминания, кто что писал.");
        }

        return builder.ToString();
    }

    private static string SelectUserPrompt(MediaDescriptionRequest request)
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
    private static string? SelectMediaNote(MediaDescriptionRequest request)
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Described {PayloadKind} of {PayloadBytes} bytes into a description of {DescriptionLength} characters")]
        public static partial void MediaDescribed(ILogger logger, int payloadBytes, PreparedMediaKind payloadKind, int descriptionLength);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Model returned an empty description for {PayloadKind} of {PayloadBytes} bytes")]
        public static partial void EmptyMediaDescription(ILogger logger, int payloadBytes, PreparedMediaKind payloadKind);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to describe {PayloadKind} of {PayloadBytes} bytes")]
        public static partial void MediaDescriptionFailed(ILogger logger, int payloadBytes, PreparedMediaKind payloadKind, Exception exception);
    }
}
