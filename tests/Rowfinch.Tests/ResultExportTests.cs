using System.Data;
using System.Globalization;
using System.Text.Json;
using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class ResultExportTests
{
    [Fact]
    public async Task JsonPreservesTypesEscapesNullsDatesAndBinaryRegardlessOfCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var date = new DateTime(2026, 10, 15, 9, 30, 0, DateTimeKind.Utc);
            var offset = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.FromHours(-4));
            var guid = Guid.NewGuid();
            var values = new object?[] { "München \"HQ\"\nnext", 12.5m, 2147483648L, true, null, DBNull.Value,
                date, offset, guid, new byte[] { 0, 1, 255 }, TimeSpan.FromHours(1), double.NaN };
            var result = new QueryResultSet("data", Enumerable.Range(0, values.Length).Select(i => new ResultColumn($"C{i}", "sql_variant")).ToArray(), [values], true);
            await using var output = new MemoryStream();
            await JsonExporter.WriteAsync(result, output, TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(output.ToArray());
            var row = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal(values[0], row.GetProperty("C0").GetString());
            Assert.Equal(12.5m, row.GetProperty("C1").GetDecimal());
            Assert.Equal(2147483648L, row.GetProperty("C2").GetInt64());
            Assert.True(row.GetProperty("C3").GetBoolean());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("C4").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("C5").ValueKind);
            Assert.Equal(date, row.GetProperty("C6").GetDateTime());
            Assert.Equal(offset, row.GetProperty("C7").GetDateTimeOffset());
            Assert.Equal(guid, row.GetProperty("C8").GetGuid());
            Assert.Equal(new byte[] { 0, 1, 255 }, row.GetProperty("C9").GetBytesFromBase64());
            Assert.Equal("01:00:00", row.GetProperty("C10").GetString());
            Assert.Equal("NaN", row.GetProperty("C11").GetString());
            Assert.True(output.CanWrite);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public async Task JsonExportsEveryDisplayedRowWithUniqueNamesWithoutLosingDuplicateAliases()
    {
        ResultColumn[] columns = [new("Name", "int"), new("Name", "int"), new("Name_2", "int"), new("", "int")];
        var result = new QueryResultSet("data", columns, [[1, 2, 3, 4], [5, 6, 7, 8]], true);
        await using var output = new MemoryStream();
        await JsonExporter.WriteAsync(result, output, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(output.ToArray());
        Assert.Equal(2, json.RootElement.GetArrayLength());
        var row = json.RootElement[0];
        Assert.Equal(new[] { "Name", "Name_2", "Name_2_2", "Column4" }, row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { 1, 2, 3, 4 }, row.EnumerateObject().Select(p => p.Value.GetInt32()));
    }

    [Fact]
    public async Task EmptyResultsProduceArrayAndNullObjectTemplateAndExportsHonorCancellation()
    {
        var result = new QueryResultSet("empty", [new("Id", "int")], [], false);
        await using var output = new MemoryStream();
        await JsonExporter.WriteAsync(result, output, TestContext.Current.CancellationToken);
        using var array = JsonDocument.Parse(output.ToArray());
        Assert.Empty(array.RootElement.EnumerateArray());
        using var obj = JsonDocument.Parse(JsonExporter.Object(result));
        Assert.Equal(JsonValueKind.Null, obj.RootElement.GetProperty("Id").ValueKind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JsonExporter.WriteAsync(result, output, new CancellationToken(true)));
    }

    [Fact]
    public void JsonObjectUsesOnlyFirstRowAndCanReplaceValuesWithNulls()
    {
        var result = new QueryResultSet("data", [new("Id", "int")], [[7], [8]], false);
        using var first = JsonDocument.Parse(JsonExporter.Object(result));
        using var empty = JsonDocument.Parse(JsonExporter.Object(result, false));
        Assert.Equal(7, first.RootElement.GetProperty("Id").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("Id").ValueKind);
    }

    [Fact]
    public void ResultMetadataIncludesTypesAndConstraintsEvenWhenNoRowsAreReturned()
    {
        using var table = new DataTable();
        table.Columns.Add(new DataColumn("Id", typeof(int)) { AllowDBNull = false });
        table.Columns.Add(new DataColumn("Name", typeof(string)) { AllowDBNull = true, MaxLength = 120 });
        using var reader = table.CreateDataReader();
        var columns = ResultMetadata.ReadColumns(reader);
        Assert.Equal(typeof(int), columns[0].ClrType);
        Assert.False(columns[0].IsNullable);
        Assert.Equal(typeof(string), columns[1].ClrType);
        Assert.True(columns[1].IsNullable);
        Assert.Equal(120, columns[1].MaxLength);
        Assert.False(reader.Read());
    }

    [Fact]
    public void CSharpUsesReportedTypesNullabilityAndAnnotationsWithoutInferringKeys()
    {
        var result = new QueryResultSet("empty",
        [new("Id", "int", typeof(int), false), new("Full Name", "nvarchar", typeof(string), false, 120),
         new("Optional", "nvarchar", typeof(string), true, -1),
         new("Balance", "decimal", typeof(decimal), true, NumericPrecision: 18, NumericScale: 2),
         new("Photo", "varbinary", typeof(byte[]), false, 512), new("Unknown", "custom_type")], [], false);
        var code = CSharpResultGenerator.Generate(result, "customer record");
        Assert.Contains("public class CustomerRecord", code);
        Assert.Contains("public int Id { get; set; }", code);
        Assert.Contains("[Required(AllowEmptyStrings = true)]", code);
        Assert.Contains("[StringLength(120)]", code);
        Assert.Contains("[Column(\"Full Name\", TypeName = \"nvarchar(120)\")]", code);
        Assert.Contains("[JsonPropertyName(\"Full Name\")]", code);
        Assert.Contains("public string FullName { get; set; } = string.Empty;", code);
        Assert.Contains("TypeName = \"nvarchar(max)\"", code);
        Assert.Contains("public string? Optional", code);
        Assert.Contains("TypeName = \"decimal(18,2)\"", code);
        Assert.Contains("public decimal? Balance", code);
        Assert.Contains("[MaxLength(512)]", code);
        Assert.Contains("public byte[] Photo", code);
        Assert.Contains("public object? Unknown", code);
        Assert.DoesNotContain("[Key]", code);
    }

    [Theory]
    [InlineData("bigint", "long")]
    [InlineData("tinyint", "byte")]
    [InlineData("smallint", "short")]
    [InlineData("bit", "bool")]
    [InlineData("real", "float")]
    [InlineData("float", "double")]
    [InlineData("money", "decimal")]
    [InlineData("uniqueidentifier", "Guid")]
    [InlineData("date", "DateTime")]
    [InlineData("datetimeoffset", "DateTimeOffset")]
    [InlineData("time", "TimeSpan")]
    [InlineData("rowversion", "byte[]")]
    public void CSharpMapsSqlTypesWithNullableValueTypes(string sql, string expected)
    {
        var result = new QueryResultSet("empty", [new("Value", sql, IsNullable: true)], [], false);
        Assert.Contains($"public {expected}? Value", CSharpResultGenerator.Generate(result));
    }

    [Fact]
    public void CSharpOnlyMarksReportedSingleKeysAndIdentitiesAndDoesNotEmitCompositeKeyAttributes()
    {
        var columns = new ResultColumn[] { new("Id", "int", IsNullable: false, IsKey: true, IsIdentity: true), new("Version", "timestamp") };
        var code = CSharpResultGenerator.Generate(new("data", columns, [], false));
        Assert.Contains("[Key]", code);
        Assert.Contains("[DatabaseGenerated(DatabaseGeneratedOption.Identity)]", code);
        Assert.Contains("[Timestamp]", code);
        var composite = CSharpResultGenerator.Generate(new("data", [columns[0], new("TenantId", "int", IsKey: true)], [], false));
        Assert.DoesNotContain("[Key]", composite);
        Assert.Contains("Composite result key", composite);
    }

    [Fact]
    public void CSharpHandlesDuplicateAliasesKeywordsClassNamesAndEscapesSourceText()
    {
        var aliases = new[] { "QueryResult", "full name", "full_name", "class", "", "1st", "ToString", "Array", "__arglist", "\"\\\n]; //", "Name_2", "Column", "Key", "Required", "DateTime", "Guid", "System" };
        var columns = aliases.Select((name, i) => new ResultColumn(name, i == 7 ? "varbinary" : "nvarchar", IsNullable: false, MaxLength: 40)).ToArray();
        var code = CSharpResultGenerator.Generate(new("data", columns, [], false));
        Assert.Contains("public string QueryResult2", code);
        Assert.Contains("public string FullName ", code);
        Assert.Contains("public string FullName2 ", code);
        Assert.Contains("public string Class ", code);
        Assert.Contains("public string Column5 ", code);
        Assert.DoesNotContain("[Column(\"\"", code);
        Assert.Contains("public string _1st ", code);
        Assert.Contains("public string ToString2 ", code);
        Assert.Contains("public string Arglist ", code);
        Assert.DoesNotContain("\n]; //", code);
        if (Environment.GetEnvironmentVariable("ROWFINCH_GENERATED_CODE_DIRECTORY") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "QueryResult.cs"), code);
            var allTypes = new[] { "bigint", "smallint", "tinyint", "int", "bit", "numeric", "money", "real", "float", "date", "datetime2", "datetimeoffset", "time", "uniqueidentifier", "binary", "xml", "sql_variant" };
            File.WriteAllText(Path.Combine(directory, "AllTypes.cs"), CSharpResultGenerator.Generate(new("empty",
                allTypes.Select((t, i) => new ResultColumn($"Value{i}", t)).ToArray(), [], false), "All Types"));
            File.WriteAllText(Path.Combine(directory, "SystemResult.cs"), CSharpResultGenerator.Generate(new("empty", [new("Now", "datetime2")], [], false), "System"));
        }
    }
}
