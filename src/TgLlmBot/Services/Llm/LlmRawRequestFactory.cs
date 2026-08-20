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
}
