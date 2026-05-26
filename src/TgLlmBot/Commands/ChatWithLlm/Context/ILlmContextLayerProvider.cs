using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace TgLlmBot.Commands.ChatWithLlm.Context
{
    public interface ILlmContextLayerProvider
    {
        Task<IReadOnlyList<LlmContextLayer>> BuildLayersAsync(
            LlmContextBuildRequest request);
    }
}
