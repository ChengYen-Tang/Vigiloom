using System.Text;
using Vigiloom.Contracts.Events;

namespace Vigiloom.Web.Events;

internal static class EventTimelineCsv
{
    public static byte[] Encode(IReadOnlyList<EventTimelineEntry> entries)
    {
        var csv = new StringBuilder("時間（UTC）,來源,事件,證據,對象類型,對象識別碼\r\n");
        foreach (var entry in entries)
        {
            csv.Append(Cell(entry.Time.ToUniversalTime().ToString("O"))).Append(',').Append(Cell(entry.Source)).Append(',')
                .Append(Cell(entry.Summary)).Append(',').Append(Cell(entry.Evidence)).Append(',').Append(Cell(entry.ResourceKind)).Append(',')
                .Append(Cell(entry.ResourceId.ToString("D"))).Append("\r\n");
        }
        return [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private static string Cell(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (value.Length > 0 && (value[0] is '\t' or '\r' or '\n' || trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '-' or '@'))) value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
