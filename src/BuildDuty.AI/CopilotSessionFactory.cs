using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Factory for creating Copilot sessions pre-configured with build-duty
/// tools and MCP servers. The GitHub MCP server is built-in to Copilot CLI
/// and does not need explicit configuration.
/// </summary>
public static class CopilotSessionFactory
{
    /// <summary>
    /// Build MCP server config for Azure DevOps pipeline scanning.
    /// Uses the official @azure-devops/mcp server with az CLI auth and
    /// only the pipelines domain enabled.
    /// </summary>
    public static Dictionary<string, object> AdoPipelineServers(string organizationName) => new()
    {
        ["azure-devops"] = new McpLocalServerConfig
        {
            Command = "npx",
            Args = ["-y", "@azure-devops/mcp", organizationName, "-a", "azcli", "-d", "pipelines"],
            Tools = ["*"],
            Env = new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
            },
        },
    };

    /// <summary>
    /// Empty MCP server config — used when only built-in servers (GitHub) are needed.
    /// </summary>
    public static Dictionary<string, object> NoExtraServers() => [];

    /// <summary>
    /// Build MCP server config with the ADO server — used for triage and
    /// other operations that may need access to ADO. The GitHub MCP server
    /// is built-in and always available.
    /// </summary>
    public static Dictionary<string, object> AllServers(string? adoOrganizationName = null)
    {
        if (adoOrganizationName is not null)
            return AdoPipelineServers(adoOrganizationName);
        return NoExtraServers();
    }

    private const string DefaultInstructions = """
        You are a build-duty assistant that helps on-call engineers triage CI/build failures.

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
        IEnumerable<AIFunction>? tools = null,
        string? instructions = null,
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
                Content = instructions ?? DefaultInstructions,
            },
        };

        if (tools is not null)
            config.Tools = tools.ToList();

        if (model is not null)
            config.Model = model;

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
