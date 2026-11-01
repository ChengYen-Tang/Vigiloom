using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Vigiloom.Modules.Resources.Domain;

/// <summary>Validates and canonicalizes an IP literal or DNS host name without resolving it.</summary>
public static class TargetAddress
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        var input = value?.Trim();
        if (string.IsNullOrEmpty(input) || input.Length > 253 || input.Contains('%')) return false;
        if (IPAddress.TryParse(input, out var address))
        {
            // Avoid the historical shorthand/hexadecimal forms accepted by IPAddress.TryParse.
            if (address.AddressFamily == AddressFamily.InterNetwork &&
                (input.Split('.').Length != 4 || input.Split('.').Any(part => part.Length == 0 || (part.Length > 1 && part[0] == '0') || part.Any(c => c is < '0' or > '9')))) return false;
            normalized = address.ToString().ToLowerInvariant();
            return true;
        }
        if (input.All(c => c is >= '0' and <= '9' or '.')) return false;
        if (input.Contains(':') || input.Contains('/') || input.Contains('[') || input.Contains(']')) return false;
        try
        {
            var ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(input.TrimEnd('.')).ToLowerInvariant();
            if (ascii.Length is 0 or > 253) return false;
            var labels = ascii.Split('.');
            if (labels.Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))) return false;
            normalized = ascii;
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
