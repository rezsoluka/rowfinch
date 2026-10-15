namespace Rowfinch.Core;

public sealed record DatabaseColumn(int Ordinal, string Name, string TypeName, bool IsNullable,
    int PrimaryKeyOrdinal = 0, bool IsIdentity = false, bool IsComputed = false, bool IsHidden = false)
{
    public string Description => $"{TypeName} · {(IsNullable ? "NULL" : "NOT NULL")}" +
        (PrimaryKeyOrdinal > 0 ? $" · PK {PrimaryKeyOrdinal}" : "") +
        (IsIdentity ? " · identity" : "") + (IsComputed ? " · computed" : "") + (IsHidden ? " · hidden" : "");
}

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
        if (names.Length == 0) throw new InvalidOperationException("No visible columns are available. Refresh the object or check metadata permissions.");
        return $"SELECT\n    {string.Join(",\n    ", names)}\nFROM {item.QuotedName};";
    }

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
