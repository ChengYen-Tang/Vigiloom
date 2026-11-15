using System.Text.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal enum HostEventStatus { Ready, Gap, Invalid }

/// <summary>Fields decoded from one retained host log, preserving repeated values.</summary>
internal sealed record HostEvent(HostEventStatus Status, AgentLogSourceKind SourceKind,
    IReadOnlyDictionary<string, List<string>> Fields, string? RawText = null, bool HasOpaqueFields = false);

internal static class HostEventDecoder
{
    private static readonly XNamespace WindowsNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    internal static HostEvent Decode(HostLogEntry log)
    {
        if (log.Gap is not null) return Empty(HostEventStatus.Gap);
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? windowsText = null;
        try
        {
            switch (log.SourceKind)
            {
                case AgentLogSourceKind.WindowsEvent:
                    using (var input = new StringReader(log.Content))
                    using (var xml = XmlReader.Create(input, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                        MaxCharactersInDocument = AgentLogBatchCodec.MaximumContentCharacters
                    }))
                    {
                        var root = XDocument.Load(xml).Root;
                        var system = root?.Element(WindowsNamespace + "System");
                        if (root?.Name != WindowsNamespace + "Event" || system is null) return Empty(HostEventStatus.Invalid);
                        var text = new StringBuilder();
                        foreach (var node in root.DescendantNodes().OfType<XText>()) text.AppendLine(node.Value);
                        windowsText = text.ToString();
                        Add("EventID", system.Element(WindowsNamespace + "EventID")?.Value);
                        Add("Channel", system.Element(WindowsNamespace + "Channel")?.Value);
                        Add("EventLog", system.Element(WindowsNamespace + "Channel")?.Value);
                        Add("Provider_Name", system.Element(WindowsNamespace + "Provider")?.Attribute("Name")?.Value);
                        foreach (var data in root.Element(WindowsNamespace + "EventData")?.Elements(WindowsNamespace + "Data") ?? [])
                        {
                            var name = data.Attribute("Name")?.Value;
                            if (name is not null && (name.Equals("EventID", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("Channel", StringComparison.OrdinalIgnoreCase) || name.Equals("EventLog", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("Provider_Name", StringComparison.OrdinalIgnoreCase)))
                                return Empty(HostEventStatus.Invalid);
                            Add(name, data.Value);
                        }
                        if (fields.TryGetValue("Channel", out var channels) && channels.Contains("Security", StringComparer.OrdinalIgnoreCase) &&
                            fields.TryGetValue("EventID", out var ids) && ids.Contains("4688", StringComparer.Ordinal) &&
                            fields.TryGetValue("Provider_Name", out var providers) && providers.Contains("Microsoft-Windows-Security-Auditing", StringComparer.OrdinalIgnoreCase))
                        {
                            Alias("NewProcessName", "Image");
                            Alias("ParentProcessName", "ParentImage");
                            Alias("ProcessCommandLine", "CommandLine");
                        }
                    }
                    return new(HostEventStatus.Ready, log.SourceKind, fields, windowsText);
                case AgentLogSourceKind.Journal:
                    using (var document = JsonDocument.Parse(log.Content))
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Array) return Empty(HostEventStatus.Invalid);
                        var opaque = false;
                        foreach (var field in document.RootElement.EnumerateArray())
                        {
                            if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("Encoding", out var encoding) ||
                                !field.TryGetProperty("Value", out var value) || encoding.ValueKind != JsonValueKind.String || value.ValueKind != JsonValueKind.String)
                                return Empty(HostEventStatus.Invalid);
                            if (encoding.GetString() == "base64") { opaque = true; continue; }
                            if (encoding.GetString() != "utf8") return Empty(HostEventStatus.Invalid);
                            var text = value.GetString()!;
                            var separator = text.IndexOf('=');
                            if (separator <= 0) return Empty(HostEventStatus.Invalid);
                            Add(text[..separator], text[(separator + 1)..]);
                        }
                        return new(HostEventStatus.Ready, log.SourceKind, fields, log.Content, opaque);
                    }
                case AgentLogSourceKind.TextFile:
                    return new(HostEventStatus.Ready, log.SourceKind, fields, log.Content);
                default:
                    return Empty(HostEventStatus.Invalid);
            }
        }
        catch (Exception error) when (error is JsonException or XmlException or InvalidOperationException)
        {
            return Empty(HostEventStatus.Invalid);
        }

        void Add(string? name, string? value)
        {
            if (string.IsNullOrWhiteSpace(name) || value is null) return;
            if (!fields.TryGetValue(name, out var values)) fields.Add(name, values = []);
            values.Add(value);
        }
        void Alias(string source, string target)
        {
            if (!fields.ContainsKey(target) && fields.TryGetValue(source, out var values)) fields.Add(target, values);
        }
        HostEvent Empty(HostEventStatus status) => new(status, log.SourceKind, new Dictionary<string, List<string>>());
    }
}
