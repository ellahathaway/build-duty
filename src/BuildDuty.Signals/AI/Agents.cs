using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace BuildDuty.Signals.AI;

public class Agents
{
    private string _azureDevopsToken;
    private string _githubToken;

    public record Agent(Type type, CustomAgentConfig config, Dictionary<string, object> mcpServers, ICollection<AIFunction> tools);

    public Agents(string azureDevopsToken, string githubToken)
    {
        _azureDevopsToken = azureDevopsToken;
        _githubToken = githubToken;
    }

    public enum Type
    {
        AzureDevOps,
        GitHub        
    }

    public List<Agent> GetAgents(List<Type> agentTypes)
    {
        var agents = new List<Agent>();
        foreach (var agentType in agentTypes)
        {
            agents.Add(GetAgent(agentType));
        }
        return agents;
    }

    public Agent GetAgent(Type agentType)
    {
        var tools = SignalTools.GetTools();
        return agentType switch
        {
            Type.AzureDevOps => new Agent(agentType, GetAzureDevOpsSignalAgent(), GetAzureDevOpsMcpServer(), tools),
            Type.GitHub => new Agent(agentType, GetGitHubSignalAgent(), GetGitHubMcpServer(), tools),
            _ => throw new ArgumentOutOfRangeException(nameof(agentType), agentType, null)
        };
    }

    private CustomAgentConfig GetAzureDevOpsSignalAgent() => new CustomAgentConfig
    {
        Name = "AzureDevOpsSignalAgent",
        Description = "Reads and analyzes Azure DevOps build pipeline logs using the read_pipeline_log tool. Use this agent when you need to retrieve and interpret CI/CD log content.",
        McpServers = GetAzureDevOpsMcpServer(),
        Tools = [ "deserialize_signals_from_file", "pipelines_get_build_changes", "pipelines_get_build_definition_revisions", "pipelines_get_build_definitions", "pipelines_get_build_log", "pipelines_get_build_log_by_id", "pipelines_get_build_status" ],
        Prompt = "Read and analyze Azure DevOps signals.",
    };

    private CustomAgentConfig GetGitHubSignalAgent() => new CustomAgentConfig
    {
        Name = "GitHubSignalAgent",
        Description = "Reads and analyzes GitHub repository data using the GitHub MCP server. Use this agent when you need to retrieve and interpret GitHub repository information.",
        McpServers = GetGitHubMcpServer(),
        Tools = [ "deserialize_signals_from_file", "issue_read", "pull_request_read", "get_commit", "get_file_contents", "get_job_logs", "search_issues", "search_pull_requests" ],
        Prompt = "Read and analyze GitHub signals."
    };

    private Dictionary<string, object> GetAzureDevOpsMcpServer()
    {
        if (string.IsNullOrWhiteSpace(_azureDevopsToken))
        {
            throw new InvalidOperationException("Azure DevOps token is required for the Azure DevOps MCP server but was not found.");
        }

        return new Dictionary<string, object>
        {
            ["azure-devops-mcp-server"] = new McpRemoteServerConfig
            {
                Url = "https://mcp.dev.azure.com/",
                Tools = ["*"],
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {_azureDevopsToken}",
                    ["X-MCP-Toolsets"] = "pipelines"
                }
            }
        };
    }

    private Dictionary<string, object> GetGitHubMcpServer()
    {
        if (string.IsNullOrWhiteSpace(_githubToken))
        {
            throw new InvalidOperationException("GitHub token is required for the GitHub MCP server but was not found.");
        }

        return new Dictionary<string, object>
        {
            ["github-mcp-server"] = new McpRemoteServerConfig
            {
                Url = "https://api.githubcopilot.com/mcp/",
                Tools = ["*"],
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {_githubToken}"
                }
            }
        };
    }
}
