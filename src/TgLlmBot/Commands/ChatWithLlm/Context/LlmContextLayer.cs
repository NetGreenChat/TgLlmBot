using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;

namespace TgLlmBot.Commands.ChatWithLlm.Context
{
    public sealed class LlmContextLayer
    {
        public required string Id { get; init; }

        public required LlmContextStage Stage { get; init; }

        public int Order { get; init; }

        public required ChatRole Role { get; init; }

        public required IList<AIContent> Contents { get; init; }

        public bool IsInstruction { get; init; }

        public bool IsHistory { get; init; }

        public bool IsRequired { get; init; }

        public static LlmContextLayer Text(
            string id,
            LlmContextStage stage,
            ChatRole role,
            string content,
            int order = 0,
            bool isInstruction = false,
            bool isHistory = false,
            bool isRequired = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(content);

            return new()
            {
                Id = id,
                Stage = stage,
                Order = order,
                Role = role,
                Contents = new List<AIContent>
            {
                new TextContent(content)
            },
                IsInstruction = isInstruction,
                IsHistory = isHistory,
                IsRequired = isRequired
            };
        }
    }
}
