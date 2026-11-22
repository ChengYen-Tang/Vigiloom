using System.Net;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Matches attributed IOC source lines against structured host-event fields.</summary>
internal sealed class IocMatcher
{
    private readonly Dictionary<(string Kind, string Value), List<IocLine>> indicators = new();

    internal IocMatcher(byte[] hashes, byte[] addresses)
    {
        Add(IocSourceEligibility.HashPath, hashes);
        Add(IocSourceEligibility.C2Path, addresses);
    }

    private void Add(string path, byte[] content)
    {
        foreach (var line in IocParser.Parse(path, content))
        {
            if (line.Value is null || line.Kind == "unsupported") continue;
            var key = (line.Kind, line.Value.ToLowerInvariant());
            if (!indicators.TryGetValue(key, out var matches)) indicators.Add(key, matches = []);
            matches.Add(line);
        }
    }

    internal IEnumerable<IocMatch> Match(HostEvent hostEvent)
    {
        if (hostEvent.Status != HostEventStatus.Ready) yield break;
        foreach (var (field, values) in hostEvent.Fields)
        {
            foreach (var value in values)
            {
                if (field is "DestinationIp" or "SourceIp")
                {
                    if (IPAddress.TryParse(value, out var ip) && !value.Contains('%'))
                        foreach (var match in Find("ip", ip.ToString(), field)) yield return match;
                }
                else if (field is "DestinationHostname" or "SourceHostname" or "QueryName")
                {
                    foreach (var match in Find("domain", value, field)) yield return match;
                }
                else if (field == "Hashes")
                {
                    foreach (var item in value.Split(','))
                    {
                        var separator = item.IndexOf('=');
                        if (separator < 1) continue;
                        var kind = item[..separator].Trim().ToLowerInvariant();
                        if (kind is not ("md5" or "sha1" or "sha256")) continue;
                        foreach (var match in Find(kind, item[(separator + 1)..].Trim(), field)) yield return match;
                    }
                }
            }
        }
    }

    private IEnumerable<IocMatch> Find(string kind, string value, string field)
    {
        if (!indicators.TryGetValue((kind, value.ToLowerInvariant()), out var lines)) yield break;
        foreach (var line in lines) yield return new(kind, line.Value!, field, line.Number);
    }
}

internal sealed record IocMatch(string Kind, string Value, string Field, int SourceLine);
