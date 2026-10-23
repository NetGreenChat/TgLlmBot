using System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace TgLlmBot.Commands.Model;

public static class ModelInfoToolServiceCollectionExtensions
{
    public static IServiceCollection AddModelInfoTool(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddKeyedSingleton<AIFunction>(ModelInfoFormatter.ToolServiceKey, (resolver, _) =>
        {
            var modelOptions = resolver.GetRequiredService<ModelCommandHandlerOptions>();
            return AIFunctionFactory.Create(
                () => ModelInfoFormatter.BuildToolResponse(modelOptions),
                ModelInfoFormatter.ToolName,
                ModelInfoFormatter.ToolHelp);
        });
        return services;
    }
}
