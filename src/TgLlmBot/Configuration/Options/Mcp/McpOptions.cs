using System.ComponentModel.DataAnnotations;

namespace TgLlmBot.Configuration.Options.Mcp;

public class McpOptions
{
    [Required]
    public McpGithubOptions Github { get; set; } = default!;
}
