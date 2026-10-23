using System;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TgLlmBot.Commands.Model;
using Xunit;

namespace TgLlmBot.Tests;

public class ModelInfoFormatterTests
{
    [Fact]
    public void Build_UsesModelCommandLayout()
    {
        var options = new ModelCommandHandlerOptions(
            new Uri("https://llm.example/v1"),
            "provider/model",
            true,
            false,
            true);

        var result = ModelInfoFormatter.Build(options);

        Assert.Equal(
            "Провайдер: `https://llm.example`\n" +
            "Модель: `provider/model`\n" +
            "\n" +
            "Возможности модели:\n" +
            "Картинки: `True`\n" +
            "Видео: `False`\n" +
            "Аудио: `True`\n",
            result);
    }

    [Fact]
    public void BuildToolResponse_ReturnsJsonWithoutTelegramMarkup()
    {
        var options = new ModelCommandHandlerOptions(
            new Uri("https://llm.example/v1"),
            "provider/model",
            true,
            false,
            true);

        var result = ModelInfoFormatter.BuildToolResponse(options);
        using var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        var capabilities = root.GetProperty("capabilities");

        Assert.Equal("https://llm.example", root.GetProperty("provider").GetString());
        Assert.Equal("provider/model", root.GetProperty("model").GetString());
        Assert.True(capabilities.GetProperty("image").GetBoolean());
        Assert.False(capabilities.GetProperty("video").GetBoolean());
        Assert.True(capabilities.GetProperty("audio").GetBoolean());
        Assert.DoesNotContain("`", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AddModelInfoTool_RegistersMetadataOnlyUnderItsKey()
    {
        var modelOptions = new ModelCommandHandlerOptions(
            new Uri("https://llm.example/v1"),
            "provider/model",
            true,
            false,
            true);
        var services = new ServiceCollection()
            .AddSingleton(modelOptions)
            .AddModelInfoTool();

        using var serviceProvider = services.BuildServiceProvider();
        var tool = serviceProvider.GetRequiredKeyedService<AIFunction>(ModelInfoFormatter.ToolServiceKey);

        Assert.Equal(ModelInfoFormatter.ToolName, tool.Name);
        Assert.Equal(ModelInfoFormatter.ToolHelp, tool.Description);
        Assert.Null(serviceProvider.GetService<AIFunction>());
    }
}
