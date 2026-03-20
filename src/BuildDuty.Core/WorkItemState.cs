using System.Text.Json.Serialization;

namespace BuildDuty.Core;

[JsonConverter(typeof(JsonStringEnumConverter<WorkItemState>))]
public enum WorkItemState
{
    Unresolved,
    InProgress,
    Resolved
}
