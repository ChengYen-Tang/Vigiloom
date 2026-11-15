using System.Text;
using System.Text.RegularExpressions;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>A compiled Sigma string value with case-insensitive wildcard semantics.</summary>
internal sealed class SigmaPattern
{
    private readonly Regex expression;

    internal SigmaPattern(string value, string? modifier = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (modifier is not (null or "contains" or "startswith" or "endswith"))
            throw new ArgumentException("Unsupported Sigma string modifier.", nameof(modifier));
        var pattern = new StringBuilder(@"\A");
        if (modifier is "contains" or "endswith") pattern.Append(".*");
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current == '\\' && i + 1 < value.Length && value[i + 1] is '*' or '?' or '\\')
                pattern.Append(Regex.Escape(value[++i].ToString()));
            else if (current == '*') pattern.Append(".*");
            else if (current == '?') pattern.Append('.');
            else pattern.Append(Regex.Escape(current.ToString()));
        }
        if (modifier is "contains" or "startswith") pattern.Append(".*");
        pattern.Append(@"\z");
        expression = new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
            RegexOptions.Singleline | RegexOptions.NonBacktracking);
    }

    internal bool Matches(string value) => expression.IsMatch(value);
}
