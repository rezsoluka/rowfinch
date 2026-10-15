using System.Text;

namespace Rowfinch.Core;

public static class CsvExporter
{
    public static async Task WriteAsync(QueryResultSet result, Stream destination, CancellationToken cancellationToken = default)
    {
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(string.Join(',', result.Columns.Select(c => Escape(c.Name))).AsMemory(), cancellationToken);
        foreach (var row in result.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CSV has no universal NULL representation; null exports as an empty field.
            var values = row.Select(value => value is null or DBNull ? "" : Escape(CellFormatter.Format(value)));
            await writer.WriteLineAsync(string.Join(',', values).AsMemory(), cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
        : value;
}
