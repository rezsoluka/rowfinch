using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rowfinch.Core;

public static class CSharpResultGenerator
{
    private static readonly HashSet<string> ReservedClassNames = new(StringComparer.Ordinal)
    {
        "System", "Column", "ColumnAttribute", "Key", "KeyAttribute", "Required", "RequiredAttribute",
        "StringLength", "StringLengthAttribute", "MaxLength", "MaxLengthAttribute", "Timestamp", "TimestampAttribute",
        "DatabaseGenerated", "DatabaseGeneratedAttribute", "DatabaseGeneratedOption", "JsonPropertyName", "JsonPropertyNameAttribute",
        "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Guid"
    };

    public static string ClassName(string name)
    {
        var identifier = Identifier(name, "QueryResult");
        return ReservedClassNames.Contains(identifier) ? identifier + "Result" : identifier;
    }

    public static string Generate(QueryResultSet result, string className = "QueryResult")
    {
        className = ClassName(className);
        var text = new StringBuilder("// Generated from result column metadata. Review before using as an entity.\n")
            .AppendLine("#nullable enable")
            .AppendLine("using System;")
            .AppendLine("using System.ComponentModel.DataAnnotations;")
            .AppendLine("using System.ComponentModel.DataAnnotations.Schema;")
            .AppendLine("using System.Text.Json.Serialization;")
            .AppendLine()
            .Append("public class ").Append(className).AppendLine("\n{");
        if (result.Columns.Count(c => c.IsKey == true) > 1)
            text.AppendLine("    // Composite result key: configure the key explicitly in your ORM.");
        var names = JsonExporter.PropertyNames(result.Columns);
        var used = new HashSet<string>(StringComparer.Ordinal)
            { className, "ToString", "Equals", "GetHashCode", "GetType", "MemberwiseClone", "Finalize", "ReferenceEquals" };
        for (var index = 0; index < result.Columns.Count; index++)
        {
            var column = result.Columns[index];
            var name = Identifier(column.Name, $"Column{index + 1}");
            var property = name;
            for (var suffix = 2; !used.Add(property); suffix++) property = name + suffix.ToString(CultureInfo.InvariantCulture);
            var type = TypeName(column);
            var nullable = column.IsNullable != false;
            var reference = type is "string" or "byte[]" or "object";
            if (column.IsNullable is null) text.AppendLine("    // Nullability is unknown; this property allows null.");
            if (column.IsKey == true && result.Columns.Count(c => c.IsKey == true) == 1) text.AppendLine("    [Key]");
            if (!nullable && reference) text.AppendLine("    [Required(AllowEmptyStrings = true)]");
            if (type == "string" && column.MaxLength is > 0 and < int.MaxValue)
                text.Append("    [StringLength(").Append(column.MaxLength.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(")]");
            if (type == "byte[]" && column.MaxLength is > 0 and < int.MaxValue)
                text.Append("    [MaxLength(").Append(column.MaxLength.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(")]");
            if (column.IsIdentity == true) text.AppendLine("    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]");
            if (SqlType(column) is "timestamp" or "rowversion" && type == "byte[]") text.AppendLine("    [Timestamp]");
            if (!string.IsNullOrWhiteSpace(column.Name))
            {
                text.Append("    [Column(").Append(Literal(column.Name));
                if (!string.IsNullOrWhiteSpace(column.TypeName)) text.Append(", TypeName = ").Append(Literal(StoreType(column)));
                text.AppendLine(")]");
            }
            else text.AppendLine("    // Unnamed result column: no database column mapping is available.");
            text.Append("    [JsonPropertyName(").Append(Literal(names[index])).AppendLine(")]");
            text.Append("    public ").Append(type).Append(nullable ? "?" : "").Append(' ').Append(property).Append(" { get; set; }");
            if (!nullable && reference) text.Append(type switch { "string" => " = string.Empty;", "byte[]" => " = global::System.Array.Empty<byte>();", _ => " = default!;" });
            text.AppendLine().AppendLine();
        }
        return text.AppendLine("}").ToString();
    }

    private static string Literal(string value) => JsonSerializer.Serialize(value);

    private static string Identifier(string value, string fallback)
    {
        var text = new StringBuilder();
        var capitalize = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character)) { capitalize = true; continue; }
            if (text.Length == 0 && char.IsDigit(character)) text.Append('_');
            text.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }
        return text.Length == 0 ? fallback : text.ToString();
    }

    private static string SqlType(ResultColumn column) => column.TypeName.Split('(')[0].Trim().ToLowerInvariant();

    private static string StoreType(ResultColumn column)
    {
        if (column.TypeName.Contains('(')) return column.TypeName;
        var sql = SqlType(column);
        if (sql is "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" && column.MaxLength is { } length)
            return $"{column.TypeName}({(length is < 0 or int.MaxValue ? "max" : length.ToString(CultureInfo.InvariantCulture))})";
        if (sql is "decimal" or "numeric" && column.NumericPrecision is { } precision && column.NumericScale is { } scale)
            return $"{column.TypeName}({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})";
        return column.TypeName;
    }

    private static string TypeName(ResultColumn column)
    {
        var type = column.ClrType;
        if (type == typeof(string)) return "string";
        if (type == typeof(byte[])) return "byte[]";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(short)) return "short";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(DateTime)) return "DateTime";
        if (type == typeof(DateTimeOffset)) return "DateTimeOffset";
        if (type == typeof(TimeSpan)) return "TimeSpan";
        if (type == typeof(DateOnly)) return "DateOnly";
        if (type == typeof(TimeOnly)) return "TimeOnly";
        if (type == typeof(Guid)) return "Guid";
        if (type is not null) return "object";
        return SqlType(column) switch
        {
            "bigint" => "long", "int" => "int", "smallint" => "short", "tinyint" => "byte", "bit" => "bool",
            "decimal" or "numeric" or "money" or "smallmoney" => "decimal", "real" => "float", "float" => "double",
            "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime", "datetimeoffset" => "DateTimeOffset",
            "time" => "TimeSpan", "uniqueidentifier" => "Guid", "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "byte[]",
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" or "json" => "string", _ => "object"
        };
    }
}
