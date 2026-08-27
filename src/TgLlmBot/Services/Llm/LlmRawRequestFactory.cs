using System;
using OpenAI.Chat;

namespace TgLlmBot.Services.Llm;

/// <summary>
///     Точка расширения тела запроса к LLM.
///     Поля, которых нет в модели OpenAI SDK (например, специфичные для vLLM), дописываются
///     через JsonPatch поверх JSON, сгенерированного SDK.
/// </summary>
public static class LlmRawRequestFactory
{
    /// <summary>
    ///     Создаёт сырые опции запроса для
    ///     <see cref="Microsoft.Extensions.AI.ChatOptions.RawRepresentationFactory" />.
    /// </summary>
    /// <remarks>
    ///     Возвращает НОВЫЙ экземпляр на каждый вызов. Microsoft.Extensions.AI мутирует переданный объект
    ///     (в частности, дописывает в него Tools), а при tool calls запрос уходит несколько раз подряд -
    ///     переиспользование одного экземпляра продублирует список инструментов во втором и последующих запросах.
    /// </remarks>
    public static ChatCompletionOptions CreateChatCompletionOptions()
    {
        var options = new ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is for evaluation purposes only and is subject to change
        // options.Patch.Set("$.chat_template_kwargs.enable_thinking"u8, true);
#pragma warning restore SCME0001
        return options;
    }

    /// <summary>
    ///     Создаёт сырые опции запроса к vision-модели, распознающей вложения.
    /// </summary>
    /// <param name="messagesJson">
    ///     Готовый массив сообщений запроса. Заменяет тот, что собрал Microsoft.Extensions.AI:
    ///     видео-часть сообщения он выбрасывает, и без подмены модель вложения не увидит.
    ///     Собирается в <see cref="Vision.VisionRequestJsonBuilder" />.
    /// </param>
    /// <param name="videoMediaIoKwargsJson">
    ///     Метаданные отправляемых кадров для vLLM либо <see langword="null" />, если отправляется
    ///     не цепочка кадров: у файла видео тайминг сервер определит сам при декодировании.
    /// </param>
    /// <remarks>
    ///     Рассуждения выключены: описание вложения нужно целиком, а не в виде обрубленного
    ///     по лимиту токенов внутреннего монолога модели.
    ///     Как и <see cref="CreateChatCompletionOptions" />, возвращает НОВЫЙ экземпляр на каждый вызов.
    /// </remarks>
    public static ChatCompletionOptions CreateVisionChatCompletionOptions(
        byte[] messagesJson,
        byte[]? videoMediaIoKwargsJson)
    {
        ArgumentNullException.ThrowIfNull(messagesJson);
        var options = new ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is for evaluation purposes only and is subject to change
        options.Patch.Set("$.chat_template_kwargs.enable_thinking"u8, false);
        options.Patch.Set("$.messages"u8, messagesJson);
        if (videoMediaIoKwargsJson is not null)
        {
            options.Patch.Set("$.media_io_kwargs.video"u8, videoMediaIoKwargsJson);
        }
#pragma warning restore SCME0001
        return options;
    }
}
