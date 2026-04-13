using GitHub.Copilot.SDK;

namespace BuildDuty.AI;

/// <summary>
/// Factory for creating Copilot sessions pre-configured with build-duty
/// tools and MCP servers. The GitHub MCP server is built-in to Copilot CLI
/// and does not need explicit configuration.
/// </summary>
public static class CopilotSessionFactory
{
    private const string DefaultInstructions = """
        - Be concise and actionable
        - Use the available tools to look up work items and sources
        - Use MCP server tools when available to query external services
        - Use available skills and scripts when helpful
        - Summarize findings in structured markdown
        - **NEVER prompt for user input.** You are running in a non-interactive context.
          If any tool or MCP server requires interactive input (credentials, confirmation,
          browser login, etc.), stop immediately and report the error. Do NOT wait for input.
        - If an MCP server returns an error (auth failure, connection refused, timeout),
          stop immediately and report the error. Do not attempt workarounds like curl.
          Include the MCP server name, the error message, and a suggested fix
          (e.g. "run 'az login' to authenticate with Azure CLI").
        - If you cannot fulfill the request, say so clearly
        """;

    /// <summary>
    /// Create a new Copilot session with the specified skills, tools, MCP servers,
    /// and instructions. Skills are enabled via RPC after session creation.
    /// </summary>
    public static async Task<CopilotSession> CreateAsync(
        CopilotClient client,
        IEnumerable<string> skills,
        Dictionary<string, object> mcpServers,
        string? model = null,
        CancellationToken ct = default)
    {
        var skillDirs = skills
            .Select(s => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, s)))
            .ToList();

        var config = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SkillDirectories = skillDirs,
            McpServers = mcpServers,
            SystemMessage = new SystemMessageConfig
            {
                Content = DefaultInstructions,
            },
        };

        if (model is not null)
        {
            config.Model = model;
        }

        var session = await client.CreateSessionAsync(config, ct);

        // Enable all configured skills via RPC
        foreach (var skillDir in skillDirs)
        {
            var skillName = Path.GetFileName(skillDir);
            await session.Rpc.Skills.EnableAsync(skillName, ct);
        }

        return session;
    }
}
