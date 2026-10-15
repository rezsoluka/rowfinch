using Rowfinch.Core;
using Xunit;

namespace Rowfinch.Tests;

public sealed class SqlTemplateTests
{
    private static readonly DatabaseObject Table = new("sales] data", "Orders", "Table");

    [Fact]
    public void InsertUsesOnlySelectedWritableColumnsInSchemaOrder()
    {
        DatabaseColumn[] selected =
        [
            new(4, "Computed", "int", false, IsComputed: true),
            new(3, "Last]Name", "nvarchar(100)", true),
            new(1, "Id", "int", false, 1, IsIdentity: true),
            new(2, "Name", "nvarchar(100)", false),
            new(5, "StartTime", "datetime2(7)", false, IsGeneratedAlways: true),
            new(6, "Version", "timestamp", false),
            new(7, "Internal", "int", false, IsHidden: true),
            new(8, "AliasedVersion", "[dbo].[VersionType]", false, IsRowVersion: true)
        ];
        var script = ObjectSql.Insert(Table, selected);
        Assert.Contains("INSERT INTO [sales]] data].[Orders] (\n    [Name],\n    [Last]]Name]\n)", script);
        Assert.Contains("VALUES (\n    <value_1>,\n    <value_2>\n);", script);
        foreach (var name in new[] { "[Id]", "[Computed]", "[StartTime]", "[Version]", "[Internal]", "[AliasedVersion]" })
            Assert.DoesNotContain(name, script);
    }

    [Fact]
    public void UpdateUsesSelectedNonKeyColumnsAndAllCompositeKeysInKeyOrder()
    {
        DatabaseColumn[] columns =
        [new(1, "TenantId", "int", false, 2), new(2, "OrderId", "int", false, 1),
         new(3, "Name", "nvarchar(100)", false), new(4, "Total", "decimal(18,2)", false)];
        var script = ObjectSql.Update(Table, columns, [columns[3], columns[0]]);
        Assert.Contains("SET\n    [Total] = <value_1>\nWHERE\n    [OrderId] = <key_1>\n    AND [TenantId] = <key_2>;", script);
        Assert.DoesNotContain("[Name]", script);
        Assert.DoesNotContain("SET\n    [TenantId]", script);
        Assert.Contains("Replace every <...> placeholder", script);
    }

    [Fact]
    public void UpdateWithoutKeyRequiresExplicitPredicateAndSupportsViews()
    {
        var column = new DatabaseColumn(1, "Total", "decimal(18,2)", true);
        var script = ObjectSql.Update(Table with { Kind = "View" }, [column], [column]);
        Assert.Contains("WHERE\n    <row_filter>;", script);
        Assert.Contains("view must be updatable", script);
        Assert.DoesNotContain("1 = 1", script);
    }

    [Fact]
    public void EmptyOrUnwritableSelectionsDoNotProduceWriteScripts()
    {
        DatabaseColumn[] columns = [new(1, "Id", "int", false, 1, IsIdentity: true), new(2, "Computed", "int", true, IsComputed: true)];
        Assert.Throws<InvalidOperationException>(() => ObjectSql.Select(Table, []));
        Assert.Throws<InvalidOperationException>(() => ObjectSql.Insert(Table, []));
        Assert.Throws<InvalidOperationException>(() => ObjectSql.Insert(Table, columns));
        Assert.Throws<InvalidOperationException>(() => ObjectSql.Update(Table, columns, columns));
        Assert.Throws<ArgumentException>(() => ObjectSql.Insert(Table with { Kind = "Procedure" }, columns));
        Assert.Throws<ArgumentException>(() => ObjectSql.Update(Table with { Kind = "Function" }, columns, columns));
    }

    [Fact]
    public void QuotedNamesCannotBecomeExtraSqlBatchesOrComments()
    {
        var item = new DatabaseObject("a]b", "x]\nGO\n--", "Table");
        DatabaseColumn[] columns = [new(1, "Id] --", "int", false, 1), new(2, "x] /*\n:r secret.sql", "int", false)];
        var insert = ObjectSql.Insert(item, [columns[1]]);
        var update = ObjectSql.Update(item, columns, [columns[1]]);
        Assert.Contains("[x]] /*\n:r secret.sql]", insert);
        Assert.Contains("[Id]] --] = <key_1>", update);
        Assert.Single(SqlBatchParser.Split(insert));
        Assert.Single(SqlBatchParser.Split(update));
    }
}
