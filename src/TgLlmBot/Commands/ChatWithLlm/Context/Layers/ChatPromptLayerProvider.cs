using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using TgLlmBot.Services.DataAccess.SystemPrompts;

namespace TgLlmBot.Commands.ChatWithLlm.Context.Layers
{
    public sealed class ChatPromptLayerProvider : ILlmContextLayerProvider
    {
        private readonly ISystemPromptService _systemPromptService;

        public ChatPromptLayerProvider(ISystemPromptService systemPromptService)
        {
            ArgumentNullException.ThrowIfNull(systemPromptService);
            _systemPromptService = systemPromptService;
        }

        public async Task<IReadOnlyList<LlmContextLayer>> BuildLayersAsync(LlmContextBuildRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var chatPrompt = await _systemPromptService.GetChatPromptAsync(
                request.Command.Message.Chat.Id,
                request.CancellationToken);

            if (chatPrompt.IsFailed || string.IsNullOrWhiteSpace(chatPrompt.Value))
            {
                return [];
            }

            var content = $"""
        Правила конкретного чата.
        Эти правила применяются ко всем участникам этого чата, если не конфликтуют с core system prompt.

        {chatPrompt.Value.Trim()}
        """;

            return
            [
                LlmContextLayer.Text(
                "chat-prompt",
                LlmContextStage.ChatPolicy,
                ChatRole.System,
                content,
                isInstruction: true)
            ];
        }
    }
}
