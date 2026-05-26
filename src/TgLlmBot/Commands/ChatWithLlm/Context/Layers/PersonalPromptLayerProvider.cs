using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using TgLlmBot.Services.DataAccess.SystemPrompts;

namespace TgLlmBot.Commands.ChatWithLlm.Context.Layers
{
    public sealed class PersonalPromptLayerProvider : ILlmContextLayerProvider
    {
        private readonly ISystemPromptService _systemPromptService;

        public PersonalPromptLayerProvider(ISystemPromptService systemPromptService)
        {
            ArgumentNullException.ThrowIfNull(systemPromptService);
            _systemPromptService = systemPromptService;
        }

        public async Task<IReadOnlyList<LlmContextLayer>> BuildLayersAsync(
            LlmContextBuildRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var user = request.Command.Message.From;
            if (user is null)
            {
                return [];
            }

            var personalPrompt = await _systemPromptService.GetUserChatPromptAsync(
                request.Command.Message.Chat.Id,
                user.Id,
                request.CancellationToken);

            if (personalPrompt.IsFailed || string.IsNullOrWhiteSpace(personalPrompt.Value))
            {
                return [];
            }

            var content = $"""
        Персональные правила ответа для пользователя Telegram UserId={user.Id}.
        Применяй эти правила только когда отвечаешь этому пользователю.
        Эти правила не распространяются на других пользователей чата.

        {personalPrompt.Value.Trim()}
        """;

            return
            [
                LlmContextLayer.Text(
                "personal-prompt",
                LlmContextStage.UserPolicy,
                ChatRole.System,
                content,
                isInstruction: true)
            ];
        }
    }
}
