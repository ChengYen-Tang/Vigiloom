using System.Buffers;
using System.Text;

namespace Vigiloom.Contracts.Agent.Integrity;

/// <summary>Canonical, typed observation identity. It is not an operating-system path.</summary>
public static class IntegrityKey
{
    public static string Create(IntegrityItemKind kind, IReadOnlyList<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var prefix = Prefix(kind);
        if (components.Count == 0 && kind is IntegrityItemKind.File or IntegrityItemKind.RegistryValue)
            throw new ArgumentException("This item requires a name.", nameof(components));
        var parts = new string[components.Count + 1];
        parts[0] = prefix;
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            if (component is null || component is "." or ".." || component.Any(char.IsControl) || !WellFormed(component) ||
                component.Length == 0 && (kind != IntegrityItemKind.RegistryValue || index != components.Count - 1))
                throw new ArgumentException("Invalid integrity key component.", nameof(components));
            parts[index + 1] = "+" + Uri.EscapeDataString(component);
        }
        var key = string.Join('/', parts);
        return key.Length <= 2048 ? key : throw new ArgumentException("Integrity key exceeds its limit.", nameof(components));
    }

    public static bool IsValid(IntegrityItemKind kind, string? key)
    {
        if (!Enum.IsDefined(kind) || string.IsNullOrEmpty(key) || key.Length > 2048) return false;
        var parts = key.Split('/');
        if (parts[0] != Prefix(kind) || parts.Length == 1 && kind is IntegrityItemKind.File or IntegrityItemKind.RegistryValue) return false;
        for (var index = 1; index < parts.Length; index++)
        {
            if (!parts[index].StartsWith('+')) return false;
            var encoded = parts[index][1..];
            if (encoded.Length == 0 && (kind != IntegrityItemKind.RegistryValue || index != parts.Length - 1)) return false;
            try
            {
                var decoded = Uri.UnescapeDataString(encoded);
                if (decoded is "." or ".." || decoded.Any(char.IsControl) || !WellFormed(decoded) || Uri.EscapeDataString(decoded) != encoded) return false;
            }
            catch (UriFormatException) { return false; }
        }
        return true;
    }

    private static bool WellFormed(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static string Prefix(IntegrityItemKind kind) => kind switch
    {
        IntegrityItemKind.File => "f", IntegrityItemKind.Directory => "d",
        IntegrityItemKind.RegistryKey => "k", IntegrityItemKind.RegistryValue => "v",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
