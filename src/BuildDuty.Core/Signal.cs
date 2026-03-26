namespace BuildDuty.Core;

public interface ISignal
{
    DateTime CollectionTime { get; }
    Enum Type { get; }
    object Info { get; }
    List<string> WorkItemIds { get; }
}

public abstract class Signal<TType, TInfo> : ISignal
    where TType : struct, Enum
    where TInfo : class
{
    public DateTime CollectionTime { get; init; } = DateTime.UtcNow;

    public abstract TType Type { get; }

    public required TInfo Info { get; init; }

    public List<string> WorkItemIds { get; init; } = [];

    Enum ISignal.Type => Type;
    object ISignal.Info => Info;
}
