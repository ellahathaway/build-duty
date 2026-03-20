using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Factory for creating Copilot sessions pre-configured with build-duty
/// skills, tools, and MCP servers. Skills are loaded from the <c>skills/</c>
/// directory and MCP servers are defined as defaults alongside them.
/// </summary>
public static class CopilotSessionFactory
{
    private static readonly string[] DefaultSkills =
    [
        "skills/summarize",
        "skills/diagnose-build-break",
        "skills/cluster-incidents",
        "skills/suggest-next-actions",
    ];

    private static readonly Dictionary<string, object> DefaultMcpServers = new()
    {
        ["azure-devops"] = new McpLocalServerConfig
        {
            Command = "npx",
            Args = ["-y", "@mcp-apps/azure-devops-mcp-server"],
            Tools = ["*"],
        },
        ["github"] = new McpRemoteServerConfig
        {
            Url = "https://api.githubcopilot.com/mcp/",
            Type = "http",
            Tools = ["*"],
        },
    };

    private const string DefaultInstructions = """
        You are a build-duty assistant that helps on-call engineers triage CI/build failures.

        - Be concise and actionable
        - Use the available tools to look up work items and signals
        - Use MCP server tools when available to query external services
        - Use available skills and scripts when helpful
        - Summarize findings in structured markdown
        - If an MCP server returns an error (auth failure, connection refused, timeout),
          stop immediately and report the error. Do not attempt workarounds like curl.
          Include the MCP server name, the error message, and a suggested fix
          (e.g. "run 'az login' to authenticate with Azure CLI").
        - If you cannot fulfill the request, say so clearly
        """;

    /// <summary>
    /// Create a new Copilot session with build-duty skills, tools, MCP servers,
    /// and instructions.
    /// </summary>
    public static async Task<CopilotSession> CreateAsync(
        CopilotClient client,
        string? model = null,
        IEnumerable<AIFunction>? tools = null,
        IEnumerable<string>? extraSkills = null,
        string? instructions = null,
        CancellationToken ct = default)
    {
        var skillDirs = DefaultSkills
            .Select(s => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, s)))
            .Concat(extraSkills ?? [])
            .ToList();

        var config = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SkillDirectories = skillDirs,
            McpServers = DefaultMcpServers,
            SystemMessage = new SystemMessageConfig
            {
                Content = instructions ?? DefaultInstructions,
            },
        };

        if (tools is not null)
            config.Tools = tools.ToList();

        if (model is not null)
            config.Model = model;

        return await client.CreateSessionAsync(config, ct);
    }
}
