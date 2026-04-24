using System.Xml;
using System.Xml.Serialization;

namespace BuildDuty.Signals;

/// <summary>
/// Serializes and deserializes signal collections to/from XML.
/// Produces clean, AI-readable XML output.
/// </summary>
public static class SignalXmlSerializer
{
    private static readonly XmlSerializer s_serializer = new(typeof(SignalCollection));

    private static readonly XmlWriterSettings s_writerSettings = new()
    {
        Indent = true,
        IndentChars = "  ",
        OmitXmlDeclaration = false,
    };

    /// <summary>
    /// Serializes a list of signals to an XML string.
    /// </summary>
    public static string Serialize(IReadOnlyList<Signal> signals)
    {
        var collection = new SignalCollection { Signals = signals.ToList() };
        using var writer = new StringWriter();
        using var xmlWriter = XmlWriter.Create(writer, s_writerSettings);
        s_serializer.Serialize(xmlWriter, collection);
        return writer.ToString();
    }

    /// <summary>
    /// Deserializes signals from an XML string.
    /// </summary>
    public static IReadOnlyList<Signal> Deserialize(string xml)
    {
        using var reader = new StringReader(xml);
        var collection = (SignalCollection?)s_serializer.Deserialize(reader)
            ?? throw new InvalidOperationException("Failed to deserialize signal collection.");
        return collection.Signals;
    }

    /// <summary>
    /// Serializes signals to an XML file.
    /// </summary>
    public static void SerializeToFile(IReadOnlyList<Signal> signals, string filePath)
    {
        var collection = new SignalCollection { Signals = signals.ToList() };
        using var stream = File.Create(filePath);
        using var xmlWriter = XmlWriter.Create(stream, s_writerSettings);
        s_serializer.Serialize(xmlWriter, collection);
    }

    /// <summary>
    /// Deserializes signals from an XML file.
    /// </summary>
    public static IReadOnlyList<Signal> DeserializeFromFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var collection = (SignalCollection?)s_serializer.Deserialize(stream)
            ?? throw new InvalidOperationException($"Failed to deserialize signals from '{filePath}'.");
        return collection.Signals;
    }
}

/// <summary>
/// Wrapper for XML serialization of a signal list.
/// </summary>
[XmlRoot("Signals")]
public sealed class SignalCollection
{
    [XmlElement("AzureDevOpsPipelineSignal", typeof(AzureDevOpsPipelineSignal))]
    [XmlElement("GitHubIssueSignal", typeof(GitHubIssueSignal))]
    [XmlElement("GitHubPullRequestSignal", typeof(GitHubPullRequestSignal))]
    public List<Signal> Signals { get; set; } = [];
}
