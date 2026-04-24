using BuildDuty.Signals.Configuration;
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
            Id = "sig_test123",
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
                    Id = "abc-123",
                    Result = TaskResult.Failed,
                    RecordType = "Job",
                    Name = "Build x64",
                    LogId = 15,
                    Parents =
                    [
                        new AzureDevOpsTimelineParentInfo
                        {
                            Name = "VMR Source Build",
                            Type = "Stage",
                            LogId = 0
                        }
                    ]
                }
            ]
        };

        var xml = SignalXmlSerializer.Serialize([signal]);

        Assert.Contains("sig_test123", xml);
        Assert.Contains("AzureDevOpsPipelineSignal", xml);
        Assert.Contains("dnceng", xml);
        Assert.Contains("Build x64", xml);
        Assert.Contains("VMR Source Build", xml);
    }

    [Fact]
    public void Serialize_GitHubSignals_ProducesValidXml()
    {
        var signals = new List<Signal>
        {
            new GitHubIssueSignal
            {
                Id = "sig_issue1",
                Organization = "dotnet",
                Repository = "source-build",
                Url = "https://github.com/dotnet/source-build/issues/1",
                Context = "Operational issue",
                Item = new GitHubItemInfo
                {
                    Number = 1,
                    Title = "Pipeline failure in outer loop",
                    State = "Open",
                    Body = "The build is broken."
                }
            },
            new GitHubPullRequestSignal
            {
                Id = "sig_pr1",
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
        Assert.Contains("sig_issue1", xml);
        Assert.Contains("sig_pr1", xml);
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrips()
    {
        var original = new List<Signal>
        {
            new AzureDevOpsPipelineSignal
            {
                Id = "sig_roundtrip",
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
                Id = "sig_issue_rt",
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
        Assert.Equal("sig_roundtrip", adoSignal.Id);
        Assert.Equal(100, adoSignal.PipelineId);
        Assert.NotNull(adoSignal.Build);
        Assert.Equal(BuildResult.Failed, adoSignal.Build.Result);

        var ghSignal = Assert.IsType<GitHubIssueSignal>(deserialized[1]);
        Assert.Equal("sig_issue_rt", ghSignal.Id);
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
                    Id = "sig_file_test",
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
            Assert.Equal("sig_file_test", pr.Id);
            Assert.True(pr.Merged);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
