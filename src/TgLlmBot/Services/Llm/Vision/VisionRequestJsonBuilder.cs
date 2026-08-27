using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm.Vision;

/// <summary>
///     Собирает те части тела запроса к vision-модели, которые не выражаются средствами
///     Microsoft.Extensions.AI: сообщения с медиа-вложением и метаданные кадров.
/// </summary>
/// <remarks>
///     Microsoft.Extensions.AI из вложений умеет только картинку и звук, а часть с видео
///     (<c>video_url</c>) выбрасывает из запроса молча - модель в таком запросе не увидит ничего.
///     Поэтому массив сообщений собирается здесь руками и уезжает в тело запроса JsonPatch-ем
///     через <see cref="LlmRawRequestFactory" />. Собирается целиком: точечный патч по индексу
///     внутри <c>$.messages</c> затирает остальной массив.
/// </remarks>
public static class VisionRequestJsonBuilder
{
    private static readonly JsonSerializerOptions SerializationOptions = new(JsonSerializerDefaults.General)
    {
        // Кириллицу в промпте не экранируем: и в логах читаемо, и тело запроса меньше
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>
    ///     Массив сообщений запроса: системный промпт и сообщение пользователя,
    ///     в котором к тексту приложено вложение.
    /// </summary>
    public static byte[] BuildMessages(string systemPrompt, string userPrompt, PreparedMedia media)
    {
        ArgumentException.ThrowIfNullOrEmpty(systemPrompt);
        ArgumentException.ThrowIfNullOrEmpty(userPrompt);
        ArgumentNullException.ThrowIfNull(media);

        // Цепочка кадров для модели - такое же видео, как файл: отличается только тем,
        // что лежит в data-url
        object mediaPart = media.Kind is PreparedMediaKind.Image
            ? new
            {
                type = "image_url",
                image_url = new
                {
                    url = media.DataUrl
                }
            }
            : new
            {
                type = "video_url",
                video_url = new
                {
                    url = media.DataUrl
                }
            };
        var messages = new object[]
        {
            new
            {
                role = "system",
                content = systemPrompt
            },
            new
            {
                role = "user",
                content = new object[]
                {
                    mediaPart,
                    new
                    {
                        type = "text",
                        text = userPrompt
                    }
                }
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(messages, SerializationOptions);
    }

    /// <summary>
    ///     Метаданные отправляемых кадров для <c>media_io_kwargs.video</c> либо
    ///     <see langword="null" />, если кадры не отправляются.
    /// </summary>
    /// <remarks>
    ///     Из них vLLM считает метки времени вида mm:ss, которые подставляет в промпт перед
    ///     каждым кадром. Без метаданных он берёт fps = 1, и трёхсекундная петля стикера
    ///     растянется для модели на шестнадцать секунд.
    /// </remarks>
    public static byte[]? BuildVideoMediaIoKwargs(PreparedMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var animation = media.Animation;
        if (media.Kind is not PreparedMediaKind.RenderedFrames || animation is null)
        {
            return null;
        }

        var mediaIoKwargs = new
        {
            fps = animation.SourceFps,
            frames_indices = animation.SourceFrameIndices,
            total_num_frames = animation.SourceFrameCount,
            duration = animation.SourceDuration.TotalSeconds,
            num_frames = animation.Frames.Length
        };
        return JsonSerializer.SerializeToUtf8Bytes(mediaIoKwargs, SerializationOptions);
    }
}
