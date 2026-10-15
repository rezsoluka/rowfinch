namespace Rowfinch.Core;

public sealed record DatabaseColumn(int Ordinal, string Name, string TypeName, bool IsNullable,
    int PrimaryKeyOrdinal = 0, bool IsIdentity = false, bool IsComputed = false, bool IsHidden = false,
    bool IsGeneratedAlways = false, bool IsRowVersion = false)
{
    public bool IsWritable => !IsIdentity && !IsComputed && !IsHidden && !IsGeneratedAlways && !IsRowVersion &&
        !TypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase) && !TypeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase);

    public string Description => $"{TypeName} · {(IsNullable ? "NULL" : "NOT NULL")}" +
        (PrimaryKeyOrdinal > 0 ? $" · PK {PrimaryKeyOrdinal}" : "") +
        (IsIdentity ? " · identity" : "") + (IsComputed ? " · computed" : "") + (IsHidden ? " · hidden" : "") +
        (IsGeneratedAlways ? " · generated" : "");
}

public enum ObjectScriptKind { Select, Insert, Update }

public static class ObjectSql
{
    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Preview(DatabaseObject item)
    {
        if (!item.HasColumns) throw new ArgumentException("Select rows is available for tables and views.");
        return $"SELECT TOP (100) *\nFROM {item.QuotedName};";
    }

    public static string Select(DatabaseObject item, IReadOnlyList<DatabaseColumn> columns)
    {
        if (!item.HasColumns) throw new ArgumentException("Generate SELECT is available for tables and views.");
        var names = columns.Where(c => !c.IsHidden).OrderBy(c => c.Ordinal).Select(c => Quote(c.Name)).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Select at least one visible column to generate a SELECT.");
        return $"SELECT\n    {string.Join(",\n    ", names)}\nFROM {item.QuotedName};";
    }

    public static string Insert(DatabaseObject item, IReadOnlyList<DatabaseColumn> selectedColumns)
    {
        RequireWritableObject(item);
        var columns = selectedColumns.Where(c => c.IsWritable).OrderBy(c => c.Ordinal).ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("Select at least one writable column for INSERT. Server-managed columns are omitted.");
        var names = string.Join(",\n    ", columns.Select(c => Quote(c.Name)));
        var values = string.Join(",\n    ", columns.Select((_, index) => $"<value_{index + 1}>"));
        return TemplateHelp(item) + $"INSERT INTO {item.QuotedName} (\n    {names}\n)\nVALUES (\n    {values}\n);";
    }

    public static string Update(DatabaseObject item, IReadOnlyList<DatabaseColumn> allColumns, IReadOnlyList<DatabaseColumn> selectedColumns)
    {
        RequireWritableObject(item);
        var columns = selectedColumns.Where(c => c.IsWritable && c.PrimaryKeyOrdinal == 0).OrderBy(c => c.Ordinal).ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("Select at least one writable non-key column for UPDATE. Key and server-managed columns are omitted from SET.");
        var assignments = string.Join(",\n    ", columns.Select((c, index) => $"{Quote(c.Name)} = <value_{index + 1}>"));
        var keys = allColumns.Where(c => c.PrimaryKeyOrdinal > 0).OrderBy(c => c.PrimaryKeyOrdinal).ToArray();
        var predicate = keys.Length == 0 ? "<row_filter>" : string.Join("\n    AND ", keys.Select((c, index) => $"{Quote(c.Name)} = <key_{index + 1}>"));
        return TemplateHelp(item) + $"UPDATE {item.QuotedName}\nSET\n    {assignments}\nWHERE\n    {predicate};";
    }

    private static void RequireWritableObject(DatabaseObject item)
    {
        if (!item.HasColumns) throw new ArgumentException("INSERT and UPDATE templates are available for tables and views.");
    }

    private static string TemplateHelp(DatabaseObject item) =>
        "-- Replace every <...> placeholder before running this template.\n" +
        "-- Server-managed columns are omitted; UPDATE also omits keys from SET.\n" +
        (item.Kind == "View" ? "-- This view must be updatable for this template to work.\n" : "");

    public static string FormatType(string name, int maxLength, int precision, int scale) => name.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" => $"{name}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(System.Globalization.CultureInfo.InvariantCulture))})",
        "varchar" or "char" or "binary" or "varbinary" => $"{name}({(maxLength == -1 ? "max" : maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{name}({precision},{scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{name}({scale})",
        "float" => $"{name}({precision})",
        _ => name
    };
}
