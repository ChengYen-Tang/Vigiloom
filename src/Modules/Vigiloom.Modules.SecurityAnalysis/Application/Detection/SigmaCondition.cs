using System.Text.RegularExpressions;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Compiles Sigma selection expressions into reusable, three-valued predicates.</summary>
internal static class SigmaCondition
{
    internal static Func<HostEvent, SigmaMatch> Compile(string expression, IReadOnlyDictionary<string, SigmaFieldCondition[][]> selections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(selections);
        var parser = new Parser(expression, selections);
        var result = parser.Or();
        if (!parser.End) throw new InvalidDataException("Unsupported Sigma condition.");
        return result;
    }

    private sealed class Parser
    {
        private readonly string[] tokens;
        private readonly IReadOnlyDictionary<string, SigmaFieldCondition[][]> selections;
        private int position;

        internal Parser(string expression, IReadOnlyDictionary<string, SigmaFieldCondition[][]> selections)
        {
            tokens = Regex.Matches(expression, @"\(|\)|[^\s()]+").Select(x => x.Value).Take(257).ToArray();
            if (tokens.Length > 256) throw new InvalidDataException("Sigma condition has too many terms.");
            this.selections = selections;
        }

        internal bool End => position == tokens.Length;

        internal Func<HostEvent, SigmaMatch> Or()
        {
            var left = And();
            while (Take("or"))
            {
                var prior = left;
                var right = And();
                left = log => { var value = prior(log); return value == SigmaMatch.Yes ? value : OrResult(value, right(log)); };
            }
            return left;
        }

        private Func<HostEvent, SigmaMatch> And()
        {
            var left = Unary();
            while (Take("and"))
            {
                var prior = left;
                var right = Unary();
                left = log => { var value = prior(log); return value == SigmaMatch.No ? value : AndResult(value, right(log)); };
            }
            return left;
        }

        private Func<HostEvent, SigmaMatch> Unary()
        {
            if (Take("not"))
            {
                var inner = Unary();
                return log => inner(log) switch { SigmaMatch.Yes => SigmaMatch.No, SigmaMatch.No => SigmaMatch.Yes, _ => SigmaMatch.Unknown };
            }
            if (Take("("))
            {
                var inner = Or();
                if (!Take(")")) throw new InvalidDataException("Unclosed Sigma condition.");
                return inner;
            }
            if (End) throw new InvalidDataException("Incomplete Sigma condition.");
            var name = tokens[position++];
            if (name is "1" or "all")
            {
                if (!Take("of") || End) throw new InvalidDataException("Invalid Sigma selection group.");
                var filter = tokens[position++];
                string[] names;
                if (filter == "them") names = selections.Keys.Where(x => !x.StartsWith('_')).ToArray();
                else if (filter.Contains('*'))
                {
                    var pattern = new Regex(@"\A" + Regex.Escape(filter).Replace(@"\*", ".*", StringComparison.Ordinal) + @"\z",
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                    names = selections.Keys.Where(x => !x.StartsWith('_') && pattern.IsMatch(x)).ToArray();
                }
                else throw new InvalidDataException("Unsupported Sigma selection pattern.");
                if (names.Length == 0) throw new InvalidDataException("Sigma selection pattern has no matches.");
                var predicates = names.Select(Selection).ToArray();
                return log =>
                {
                    var result = name == "all" ? SigmaMatch.Yes : SigmaMatch.No;
                    foreach (var predicate in predicates)
                    {
                        result = name == "all" ? AndResult(result, predicate(log)) : OrResult(result, predicate(log));
                        if (name == "all" && result == SigmaMatch.No || name != "all" && result == SigmaMatch.Yes) break;
                    }
                    return result;
                };
            }
            return Selection(name);
        }

        private Func<HostEvent, SigmaMatch> Selection(string name)
        {
            if (!selections.TryGetValue(name, out var alternatives) || alternatives.Length == 0 || alternatives.Any(x => x.Length == 0))
                throw new InvalidDataException("Unknown or empty Sigma selection.");
            return log =>
            {
                var combined = SigmaMatch.No;
                foreach (var fields in alternatives)
                {
                    var result = SigmaMatch.Yes;
                    foreach (var field in fields)
                    {
                        result = AndResult(result, field.Evaluate(log));
                        if (result == SigmaMatch.No) break;
                    }
                    combined = OrResult(combined, result);
                    if (combined == SigmaMatch.Yes) break;
                }
                return combined;
            };
        }

        private bool Take(string token)
        {
            if (End || tokens[position] != token) return false;
            position++;
            return true;
        }
    }

    private static SigmaMatch AndResult(SigmaMatch left, SigmaMatch right) =>
        left == SigmaMatch.No || right == SigmaMatch.No ? SigmaMatch.No :
        left == SigmaMatch.Unknown || right == SigmaMatch.Unknown ? SigmaMatch.Unknown : SigmaMatch.Yes;

    private static SigmaMatch OrResult(SigmaMatch left, SigmaMatch right) =>
        left == SigmaMatch.Yes || right == SigmaMatch.Yes ? SigmaMatch.Yes :
        left == SigmaMatch.Unknown || right == SigmaMatch.Unknown ? SigmaMatch.Unknown : SigmaMatch.No;
}
