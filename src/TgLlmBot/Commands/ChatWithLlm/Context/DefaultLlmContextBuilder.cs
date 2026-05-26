using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TgLlmBot.Commands.ChatWithLlm.Context
{

    public sealed partial class DefaultLlmContextBuilder : ILlmContextBuilder
    {
        private readonly IEnumerable<ILlmContextLayerProvider> _providers;

        public DefaultLlmContextBuilder(IEnumerable<ILlmContextLayerProvider> providers)
        {
            ArgumentNullException.ThrowIfNull(providers);

            _providers = providers;
        }

        public async Task<ChatMessage[]> BuildAsync(LlmContextBuildRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var layers = new List<LlmContextLayer>();

            foreach (var provider in _providers)
            {
                var providerLayers = await provider.BuildLayersAsync(request);
                layers.AddRange(providerLayers);
            }

            return layers
                .Where(static x => x.Contents.Count > 0)
                .OrderBy(static x => x.Stage)
                .ThenBy(static x => x.Order)
                .Select(static x => new ChatMessage(x.Role, x.Contents))
                .ToArray();
        }
    }
}
