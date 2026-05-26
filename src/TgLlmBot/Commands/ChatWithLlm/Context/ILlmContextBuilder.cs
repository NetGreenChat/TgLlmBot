using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace TgLlmBot.Commands.ChatWithLlm.Context
{
    public interface ILlmContextBuilder
    {
        Task<ChatMessage[]> BuildAsync(LlmContextBuildRequest request);
    }
}
