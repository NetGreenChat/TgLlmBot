using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TgLlmBot.Services.PromptRewrite;

public class DefaultPromptRewriteService : IPromptRewriteService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<DefaultPromptRewriteService> _logger;

    public DefaultPromptRewriteService(IChatClient chatClient, ILogger<DefaultPromptRewriteService> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(logger);
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<PromptRewriteResult> RewriteIfViolatesAsync(string globalSystemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(globalSystemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);

        var systemMessage = $"""
                             Ты — модератор системных промптов для ИИ-бота. Тебе дают пользовательский системный промпт.
                             Твоя задача — проверить, не нарушает ли он правила, установленные в глобальном системном промпте бота.

                             Глобальный системный промпт бота:
                             ---
                             {globalSystemPrompt}
                             ---

                             Если пользовательский промпт нарушает правила глобального промпта, перепиши его так, чтобы:
                             1. Убрать все нарушения
                             2. Максимально сохранить исходное намерение пользователя

                             Ответь строго в формате JSON (без markdown-блоков):
                             {{"violates": true/false, "rewritten": "переписанный промпт или оригинал если нет нарушений"}}
                             """;

        var options = new ChatOptions { MaxOutputTokens = 1024 };

        var response = await _chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, systemMessage),
                new ChatMessage(ChatRole.User, userPrompt)
            ],
            options,
            cancellationToken);

        var text = StripMarkdownFences(response.Text?.Trim() ?? string.Empty);

        try
        {
            var result = JsonSerializer.Deserialize<RewriteResponse>(text, JsonOptions);
            if (result is { Violates: true, Rewritten: not null })
            {
                return new PromptRewriteResult(result.Rewritten, true);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse prompt rewrite LLM response: {Response}", text);
        }

        return new PromptRewriteResult(userPrompt, false);
    }

    private static string StripMarkdownFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var startIndex = text.IndexOf('\n');
        var endIndex = text.LastIndexOf("```", StringComparison.Ordinal);
        if (startIndex >= 0 && endIndex > startIndex)
        {
            return text[(startIndex + 1)..endIndex].Trim();
        }

        return text;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class RewriteResponse
    {
        public bool Violates { get; set; }
        public string? Rewritten { get; set; }
    }
}
