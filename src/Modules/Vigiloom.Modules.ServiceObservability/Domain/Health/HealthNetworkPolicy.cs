using System.Net;
using System.Text.Json;

namespace Vigiloom.Modules.ServiceObservability.Domain.Health;

/// <summary>The central allowlist for network addresses reached by health checks.</summary>
public sealed class HealthNetworkPolicy
{
    public const int MaxNetworks = 64;
    private HealthNetworkPolicy() { }

    public HealthNetworkPolicy(IEnumerable<string> networks, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        CreatedAt = now.ToUniversalTime();
        ReplaceNetworks(networks, now);
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = "central";
    public string NetworksJson { get; private set; } = "[]";
    public long Generation { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void ReplaceNetworks(IEnumerable<string> networks, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(networks);
        NetworksJson = JsonSerializer.Serialize(Normalize(networks));
        Generation = checked(Generation + 1);
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>Revalidates stored data before it can be used as a network security policy.</summary>
    public static IReadOnlyList<string> ParseNetworks(string json)
    {
        if (json is null || json.Length > 4608) throw new InvalidOperationException("Invalid stored network policy.");
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidOperationException("Invalid stored network policy.");
            return Normalize(values);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidOperationException("Invalid stored network policy.", ex);
        }
    }

    private static string[] Normalize(IEnumerable<string> networks)
    {
        var values = networks.Take(MaxNetworks + 1).ToArray();
        if (values.Length > MaxNetworks) throw new ArgumentException("Too many monitoring networks.", nameof(networks));
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Contains('%'))
                throw new ArgumentException("Each entry must be an IP address or CIDR.", nameof(networks));
            var text = value.Trim();
            IPNetwork network;
            if (!text.Contains('/') && IPAddress.TryParse(text, out var address))
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !string.Equals(text, address.ToString(), StringComparison.Ordinal))
                    throw new ArgumentException("Use a complete IPv4 address.", nameof(networks));
                network = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            }
            else if (!IPNetwork.TryParse(text, out network))
                throw new ArgumentException("Each entry must be an IP address or CIDR.", nameof(networks));
            if (
                network.BaseAddress.IsIPv4MappedToIPv6 ||
                (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && network.BaseAddress.ScopeId != 0))
                throw new ArgumentException("Each monitoring network must be a valid IPv4 or IPv6 CIDR.", nameof(networks));
            normalized.Add(network.ToString());
        }
        return normalized.Order(StringComparer.Ordinal).ToArray();
    }
}
