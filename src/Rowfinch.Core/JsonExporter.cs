using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rowfinch.Core;

public static class JsonExporter
{
    public static async Task WriteAsync(QueryResultSet result, Stream destination, CancellationToken cancellationToken = default)
    {
        var names = PropertyNames(result.Columns);
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        cancellationToken.ThrowIfCancellationRequested();
        writer.WriteStartArray();
        foreach (var row in result.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRow(writer, names, row);
            // Flush incrementally so large result sets do not create a second in-memory document.
            await writer.FlushAsync(cancellationToken);
        }
        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
    }

    public static string Object(QueryResultSet result, bool useFirstRow = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            var row = useFirstRow ? result.Rows.FirstOrDefault() : null;
            WriteRow(writer, PropertyNames(result.Columns), row);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // JSON objects need unique names, including SELECTs with duplicate aliases or unnamed expressions.
    public static IReadOnlyList<string> PropertyNames(IReadOnlyList<ResultColumn> columns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        return columns.Select((column, index) =>
        {
            var name = string.IsNullOrEmpty(column.Name) ? $"Column{index + 1}" : column.Name;
            var candidate = name;
            for (var suffix = 2; !used.Add(candidate); suffix++) candidate = $"{name}_{suffix}";
            return candidate;
        }).ToArray();
    }

    private static void WriteRow(Utf8JsonWriter writer, IReadOnlyList<string> names, object?[]? row)
    {
        writer.WriteStartObject();
        for (var index = 0; index < names.Count; index++)
        {
            writer.WritePropertyName(names[index]);
            WriteValue(writer, row is not null && index < row.Length ? row[index] : null);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                JsonSerializer.Serialize(writer, value, value.GetType()); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(number); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
            case DateTime date: writer.WriteStringValue(date); break;
            case DateTimeOffset date: writer.WriteStringValue(date); break;
            case DateOnly date: writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeOnly time: writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan time: writer.WriteStringValue(time.ToString("c", CultureInfo.InvariantCulture)); break;
            case Guid guid: writer.WriteStringValue(guid); break;
            case JsonElement element: element.WriteTo(writer); break;
            default: writer.WriteStringValue(CellFormatter.Format(value)); break;
        }
    }
}
