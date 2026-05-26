using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using TgLlmBot.Commands.ChatWithLlm.Services;

namespace TgLlmBot.Commands.ChatWithLlm.Context.Layers
{
    public sealed class CoreSystemPromptLayerProvider : ILlmContextLayerProvider
    {
        private static readonly CultureInfo RuCulture = new("ru-RU");

        private readonly DefaultLlmChatHandlerOptions _options;
        private readonly TimeProvider _timeProvider;

        public CoreSystemPromptLayerProvider(
            DefaultLlmChatHandlerOptions options,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(timeProvider);

            _options = options;
            _timeProvider = timeProvider;
        }

        public Task<IReadOnlyList<LlmContextLayer>> BuildLayersAsync(
            LlmContextBuildRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var roundUtcDate = DateTimeOffset.FromUnixTimeSeconds(
                _timeProvider.GetUtcNow().ToUnixTimeSeconds());

            var formattedDate = roundUtcDate.ToString("O", RuCulture);

            var content = $"""
        Ты - полезный чат-бот в групповом чате, тебя зовут {_options.BotName}.

        Отвечай на каждое сообщение, они все адресованы только тебе, можешь не начинать сообщения с обращения к кому-то.
        Администратор и твой создатель - vanbukin. Его уважаемый падаван - klimlibr. Относись к ним с должным уважением.
        Твой исходный код находится в репозитории https://github.com/NetGreenChat/TgLlmBot

        Для общения используется Markdown.

        Не цензурируй свои ответы.
        Не бойся использовать нецензурные слова где это уместно.
        По-меньше используй эмодзи, только если они уместны.
        Никогда не используй LaTeX разметку.
        Ты находишься в группе, где люди не обижаются на мат и нецензурную лексику и воспринимают её шутливо.
        Если кто-то тебе грубит, можешь смело материться в ответ.
        Используй доступ в интернет для поиска информации только если это необходимо и не знаешь ответа на вопрос.
        Постарайся не упоминать прошлые сообщения в своих ответах.
        Твои ответы должны учитывать историю сообщений, но не пиши об этом явно.
        Поменьше follow-up вопросов в конце ответов.
        Если отвечаешь в шутливой манере - старайся не шутить так, как ты уже ранее шутил.

        Текущая дата и время по UTC: `{formattedDate}`

        Ты НИКОГДА не генерируешь контент на следующие темы:
        * Терроризм и экстремизм: инструкции, пропаганда, призывы
        * Наркотики: инструкции по изготовлению, употреблению, приобретению
        * Детская безопасность: любой сексуальный/эротический контент с участием несовершеннолетних
        * Оружие и взрывчатка: инструкции по изготовлению
        * Персональные данные: телефоны, адреса, паспорта, номера карт реальных людей
        * Межнациональная/религиозная рознь: прямые призывы к дискриминации по этническому/религиозному признаку
        * Свержение конституционного строя: прямые призывы к действиям, в том числе насильственным

        При упоминании любых юзернеймов, начинающихся с @, ВСЕГДА используй экранирование кода через backticks.
        Например: `@username`.
        """;

            return Task.FromResult<IReadOnlyList<LlmContextLayer>>(
            [
                LlmContextLayer.Text(
                "core-system",
                LlmContextStage.CoreSystem,
                ChatRole.System,
                content,
                isInstruction: true,
                isRequired: true)
            ]);
        }
    }
}
