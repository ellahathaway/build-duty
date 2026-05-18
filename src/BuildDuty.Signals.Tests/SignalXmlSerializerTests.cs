using BuildDuty.Configuration.Models;
using BuildDuty.Services.Configuration;
using Microsoft.TeamFoundation.Build.WebApi;
using Xunit;

namespace BuildDuty.Signals.Tests;

public class SignalXmlSerializerTests
{
    [Fact]
    public void Serialize_AzureDevOpsPipelineSignal_ProducesValidXml()
    {
        var signal = new AzureDevOpsPipelineSignal
        {
            OrganizationUrl = "https://dev.azure.com/dnceng",
            ProjectName = "internal",
            PipelineId = 1525,
            Url = "https://dev.azure.com/dnceng/internal/_build/results?buildId=42",
            Context = "VMR source-only build outer loop.",
            Build = new AzureDevOpsBuildInfo
            {
                Id = 42,
                Result = BuildResult.Failed,
                DefinitionId = 1525,
                SourceBranch = "refs/heads/main",
                FinishTime = new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc)
            },
            TimelineRecords =
            [
                new AzureDevOpsTimelineRecordInfo
                {
                    Result = TaskResult.Failed,
                    RecordType = "Job",
                    Name = "Build x64",
                    LogId = 15
                }
            ]
        };

        var xml = SignalXmlSerializer.Serialize([signal]);

        Assert.Contains("AzureDevOpsPipelineSignal", xml);
        Assert.Contains("dnceng", xml);
        Assert.Contains("Build x64", xml);
    }

    [Fact]
    public void Serialize_GitHubSignals_ProducesValidXml()
    {
        var signals = new List<Signal>
        {
            new GitHubIssueSignal
            {
                Organization = "dotnet",
                Repository = "source-build",
                Url = "https://github.com/dotnet/source-build/issues/1",
                Context = "Operational issue",
                Item = new GitHubItemInfo
                {
                    Number = 1,
                    Title = "Pipeline failure in outer loop",
                    State = "Open",
                    Labels = ["build-failed", "source-build"]
                }
            },
            new GitHubPullRequestSignal
            {
                Organization = "dotnet",
                Repository = "dotnet",
                Url = "https://github.com/dotnet/dotnet/pull/99",
                Merged = false,
                Item = new GitHubItemInfo
                {
                    Number = 99,
                    Title = "Update Source-Build License Scan Baselines",
                    State = "Open",
                },
                Checks =
                [
                    new GitHubCheckInfo
                    {
                        Name = "CI",
                        Status = "completed",
                        Conclusion = "success"
                    }
                ]
            }
        };

        var xml = SignalXmlSerializer.Serialize(signals);

        Assert.Contains("GitHubIssueSignal", xml);
        Assert.Contains("GitHubPullRequestSignal", xml);
        Assert.Contains("Pipeline failure in outer loop", xml);
        Assert.Contains("Update Source-Build License Scan Baselines", xml);
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrips()
    {
        var original = new List<Signal>
        {
            new AzureDevOpsPipelineSignal
            {
                OrganizationUrl = "https://dev.azure.com/org",
                ProjectName = "proj",
                PipelineId = 100,
                Url = "https://dev.azure.com/org/proj/_build/results?buildId=1",
                Build = new AzureDevOpsBuildInfo
                {
                    Id = 1,
                    Result = BuildResult.Failed,
                    DefinitionId = 100,
                    SourceBranch = "refs/heads/main",
                }
            },
            new GitHubIssueSignal
            {
                Organization = "dotnet",
                Repository = "runtime",
                Url = "https://github.com/dotnet/runtime/issues/42",
                Item = new GitHubItemInfo
                {
                    Number = 42,
                    Title = "Something is broken",
                    State = "Open",
                }
            }
        };

        var xml = SignalXmlSerializer.Serialize(original);
        var deserialized = SignalXmlSerializer.Deserialize(xml);

        Assert.Equal(2, deserialized.Count);

        var adoSignal = Assert.IsType<AzureDevOpsPipelineSignal>(deserialized[0]);
        Assert.Equal(100, adoSignal.PipelineId);
        Assert.NotNull(adoSignal.Build);
        Assert.Equal(BuildResult.Failed, adoSignal.Build.Result);

        var ghSignal = Assert.IsType<GitHubIssueSignal>(deserialized[1]);
        Assert.Equal(42, ghSignal.Item.Number);
    }

    [Fact]
    public void Serialize_EmptyList_ProducesValidXml()
    {
        var xml = SignalXmlSerializer.Serialize([]);
        var deserialized = SignalXmlSerializer.Deserialize(xml);

        Assert.Empty(deserialized);
    }

    [Fact]
    public void SerializeToFile_DeserializeFromFile_RoundTrips()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var signals = new List<Signal>
            {
                new GitHubPullRequestSignal
                {
                    Organization = "dotnet",
                    Repository = "sdk",
                    Url = "https://github.com/dotnet/sdk/pull/1",
                    Merged = true,
                    Item = new GitHubItemInfo
                    {
                        Number = 1,
                        Title = "Test PR",
                        State = "Closed",
                    }
                }
            };

            SignalXmlSerializer.SerializeToFile(signals, tempFile);
            var deserialized = SignalXmlSerializer.DeserializeFromFile(tempFile);

            var pr = Assert.IsType<GitHubPullRequestSignal>(Assert.Single(deserialized));
            Assert.True(pr.Merged);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
