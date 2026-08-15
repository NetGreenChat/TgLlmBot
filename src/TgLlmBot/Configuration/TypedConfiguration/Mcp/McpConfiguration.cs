using System;
using TgLlmBot.Configuration.Options.Mcp;

namespace TgLlmBot.Configuration.TypedConfiguration.Mcp;

public class McpConfiguration
{
    private McpConfiguration(
        McpGithubConfiguration github)
    {
        ArgumentNullException.ThrowIfNull(github);
        Github = github;
    }

    public McpGithubConfiguration Github { get; }

    public static McpConfiguration Convert(McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var github = McpGithubConfiguration.Convert(options.Github);
        return new(github);
    }
}
