using System.Globalization;
using System.Text;
using CsvHelper;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record EpssMetadata(string ModelVersion, DateTimeOffset ScoredAt);
internal readonly record struct EpssScore(string Cve, decimal Probability, decimal Percentile);

/// <summary>Streams the current FIRST/EPSS CSV. The caller must atomically discard all rows if reading fails.</summary>
internal static class EpssCsv
{
    internal static async Task<int> ReadAsync(Stream csvStream, DateTimeOffset now, int maximumRecords,
        Func<EpssMetadata, CancellationToken, ValueTask> onMetadata,
        Func<EpssScore, CancellationToken, ValueTask> onScore, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(csvStream);
        ArgumentNullException.ThrowIfNull(onMetadata);
        ArgumentNullException.ThrowIfNull(onScore);
        if (maximumRecords is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        var options = new CsvReaderOptions
        {
            HasHeader = false, Separator = ',', BufferSize = 4096, MaxRecordSize = 512,
            MalformedData = CsvMalformedDataHandling.Throw, SkipEmptyLines = false,
            IncludeValuesInExceptions = false, Encoding = new UTF8Encoding(false, true)
        };
        try
        {
            using var reader = CsvReader.Create(csvStream, options, leaveOpen: true);
            if (!await reader.ReadAsync(token) || reader.FieldCount != 2) throw Invalid();
            var modelField = reader.GetString(0);
            var dateField = reader.GetString(1);
            const string modelPrefix = "#model_version:";
            const string datePrefix = "score_date:";
            if (!modelField.StartsWith(modelPrefix, StringComparison.Ordinal) || !dateField.StartsWith(datePrefix, StringComparison.Ordinal)) throw Invalid();
            var model = modelField[modelPrefix.Length..];
            if (model.Length is < 2 or > 64 || model.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
                !TryScoredAt(dateField[datePrefix.Length..], out var scoredAt) ||
                scoredAt > now.AddMinutes(5) || scoredAt < new DateTimeOffset(2021, 4, 14, 0, 0, 0, TimeSpan.Zero)) throw Invalid();
            if (!await reader.ReadAsync(token) || reader.FieldCount != 3 ||
                reader.GetString(0) != "cve" || reader.GetString(1) != "epss" || reader.GetString(2) != "percentile") throw Invalid();
            await onMetadata(new(model, scoredAt), token);
            var count = 0;
            while (await reader.ReadAsync(token))
            {
                if (++count > maximumRecords || !TryReadScore(reader, out var score)) throw Invalid();
                await onScore(score, token);
            }
            if (count == 0) throw Invalid();
            return count;
        }
        catch (Exception ex) when (ex is CsvFormatException or DecoderFallbackException)
        { throw new InvalidDataException("Invalid EPSS CSV.", ex); }
    }

    private static bool TryReadScore(CsvReader reader, out EpssScore score)
    {
        score = default;
        if (reader.FieldCount != 3) return false;
        var cve = reader.GetSpan(0);
        var probability = reader.GetSpan(1);
        var percentile = reader.GetSpan(2);
        if (!ValidCve(cve) || !Fraction(probability, out var p) || !Fraction(percentile, out var q)) return false;
        score = new(Encoding.ASCII.GetString(cve), p, q);
        return true;
    }

    private static bool ValidCve(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 13 or > 28 || !value.StartsWith("CVE-"u8) || value[8] != (byte)'-') return false;
        for (var i = 4; i < value.Length; i++) if (i != 8 && value[i] is < (byte)'0' or > (byte)'9') return false;
        return true;
    }

    private static bool Fraction(ReadOnlySpan<byte> text, out decimal value)
    {
        value = default;
        if (text.Length is < 1 or > 32 || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value)) return false;
        var exponent = text.IndexOfAny((byte)'e', (byte)'E');
        var mantissa = exponent < 0 ? text : text[..exponent];
        var exponentValue = 0;
        if (exponent >= 0 && !int.TryParse(text[(exponent + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponentValue)) return false;
        var decimalPoint = mantissa.IndexOf((byte)'.');
        var integerDigits = decimalPoint < 0 ? mantissa.Length : decimalPoint;
        var digitIndex = 0;
        foreach (var c in mantissa)
        {
            if (c == (byte)'.') continue;
            if (c != (byte)'0' && (long)digitIndex >= (long)integerDigits + exponentValue + 9) return false;
            digitIndex++;
        }
        if (value == 0 && mantissa.ContainsAnyInRange((byte)'1', (byte)'9')) return false;
        return value is >= 0 and <= 1 && decimal.Round(value, 9) == value;
    }

    private static bool TryScoredAt(string input, out DateTimeOffset value)
    {
        value = default;
        if (input.EndsWith("+0000", StringComparison.Ordinal)) input = input[..^5] + "+00:00";
        if (!input.EndsWith('Z') && !input.EndsWith("+00:00", StringComparison.Ordinal)) return false;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        return DateTimeOffset.TryParseExact(input, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static InvalidDataException Invalid() => new("Invalid EPSS CSV.");
}
