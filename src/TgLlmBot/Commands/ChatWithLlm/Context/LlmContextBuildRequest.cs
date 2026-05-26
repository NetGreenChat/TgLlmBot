using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Commands.ChatWithLlm.Context
{
    public sealed class LlmContextBuildRequest
    {
        public required ChatWithLlmCommand Command { get; init; }

        public required DbChatMessage[] ContextMessages { get; init; }

        public required CancellationToken CancellationToken { get; init; }
    }
}
