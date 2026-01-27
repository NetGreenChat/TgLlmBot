using System.Threading;
using System.Threading.Tasks;

namespace TgLlmBot.Services.PromptRewrite;

public interface IPromptRewriteService
{
    Task<PromptRewriteResult> RewriteIfViolatesAsync(string globalSystemPrompt, string userPrompt, CancellationToken cancellationToken);
}

public record PromptRewriteResult(string Prompt, bool WasRewritten);
