using Rowfinch.Core;
using Xunit;

namespace Rowfinch.Tests;

public sealed class SqlCompletionTests
{
    private static readonly DatabaseObject Customers = new("dbo", "Customers", "Table", 1);
    private static readonly DatabaseObject Orders = new("sales", "Orders", "Table", 2);
    private static readonly DatabaseObject[] Objects = [Customers, Orders,
        new("reporting", "MonthlySales", "View"), new("dbo", "GetCustomers", "Procedure"),
        new("dbo", "OrderTotal", "Function")];

    private static SqlCompletionContext Context(string marked, IReadOnlyList<DatabaseObject>? objects = null)
    {
        var offset = marked.IndexOf('|');
        return Assert.IsType<SqlCompletionContext>(SqlCompletion.Analyze(marked.Remove(offset, 1), offset, objects ?? Objects));
    }

    [Theory]
    [InlineData("SELECT * FROM Cu|")]
    [InlineData("SELECT * FROM Customers c JOIN Cu|")]
    [InlineData("UPDATE Cu|")]
    [InlineData("INSERT INTO Cu|")]
    public void ObjectPositionsOfferTablesViewsAndQualifiedInsertion(string sql)
    {
        var context = Context(sql);
        Assert.Contains(context.Items, c => c.Text == "Customers" && c.InsertionText == "[dbo].[Customers]");
        Assert.Contains(context.Items, c => c.Text == "MonthlySales");
        Assert.DoesNotContain(context.Items, c => c.Text is "SELECT" or "GetCustomers");
    }

    [Fact]
    public void SchemaAndProcedureCompletionRespectTheTypedQualifier()
    {
        var tables = Context("SELECT * FROM [sales].Or|");
        Assert.Equal("[Orders]", Assert.Single(tables.Items).InsertionText);
        var procedures = Context("EXEC dbo.Ge|");
        Assert.Equal("[GetCustomers]", Assert.Single(procedures.Items).InsertionText);
        Assert.Empty(Context("SELECT * FROM other.database.dbo.Cu|").Items);
    }

    [Theory]
    [InlineData("SELECT c.| FROM dbo.Customers AS c")]
    [InlineData("SELECT C.Na| FROM [dbo].[Customers] c")]
    [InlineData("SELECT [c].| FROM dbo.Customers AS [c]")]
    [InlineData("SELECT Customers.| FROM dbo.Customers")]
    [InlineData("SELECT dbo.Customers.| FROM dbo.Customers")]
    [InlineData("SELECT (c.|) FROM dbo.Customers c")]
    [InlineData("SELECT c.| FROM dbo.Customers c, sales.Orders o")]
    [InlineData("UPDATE c SET c.| FROM dbo.Customers c")]
    public void AliasesResolveEvenWhenTheirDeclarationFollowsTheCaret(string sql)
    {
        var context = Context(sql);
        Assert.Equal(Customers, Assert.Single(context.ColumnSources));
        Assert.Empty(context.Items);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Customers c; SELECT c.|")]
    [InlineData("SELECT * FROM dbo.Customers c\nGO\nSELECT c.|")]
    [InlineData("SELECT * FROM dbo.Customers c\nGO 2\nSELECT c.|")]
    [InlineData("SELECT * FROM dbo.Customers c\nGO -- next batch\nSELECT c.|")]
    [InlineData("SELECT c.| FROM sales.Orders o WHERE EXISTS (SELECT 1 FROM dbo.Customers c)")]
    public void AliasesDoNotLeakAcrossStatementsBatchesOrSiblingScopes(string sql)
        => Assert.Empty(Context(sql).ColumnSources);

    [Fact]
    public void NestedAliasesShadowOuterAliasesAndCorrelatedReferencesRemainVisible()
    {
        var nested = Context("SELECT * FROM dbo.Customers c WHERE EXISTS (SELECT c.| FROM sales.Orders c)");
        Assert.Equal(Orders, Assert.Single(nested.ColumnSources));
        var correlated = Context("SELECT * FROM dbo.Customers c WHERE EXISTS (SELECT c.| FROM sales.Orders o)");
        Assert.Equal(Customers, Assert.Single(correlated.ColumnSources));
        var shadow = Context("SELECT * FROM dbo.Customers c WHERE EXISTS (SELECT c.| FROM (SELECT 1 AS X) c)");
        Assert.Empty(shadow.ColumnSources);
    }

    [Theory]
    [InlineData("WITH Customers AS (SELECT 1 AS X) SELECT c.| FROM Customers c")]
    [InlineData("WITH Customers (X) AS (SELECT 1) SELECT c.| FROM Customers c")]
    [InlineData("SELECT c.| FROM (SELECT 1 AS X) c")]
    [InlineData("SELECT c.| FROM other.dbo.Customers c")]
    public void UnknownProjectionsAndOtherDatabasesAreNotGuessed(string sql)
        => Assert.Empty(Context(sql).ColumnSources);

    [Fact]
    public void AmbiguousUnqualifiedTablesRequireASchema()
    {
        DatabaseObject[] ambiguous = [Customers, Customers with { Schema = "sales", ObjectId = 2 }];
        Assert.Empty(Context("SELECT c.| FROM Customers c", ambiguous).ColumnSources);
        Assert.Equal(Customers, Assert.Single(Context("SELECT c.| FROM dbo.Customers c", ambiguous).ColumnSources));
    }

    [Theory]
    [InlineData("SELECT c.| FROM dbo.Customers c UNION ALL SELECT c.OrderId FROM sales.Orders c")]
    [InlineData("SELECT c.OrderId FROM sales.Orders c UNION ALL SELECT c.| FROM dbo.Customers c")]
    [InlineData("SELECT c.OrderId FROM sales.Orders c\nSELECT c.| FROM dbo.Customers c")]
    public void ReusedAliasesResolveWithinTheirOwnQuery(string sql)
        => Assert.Equal(Customers, Assert.Single(Context(sql).ColumnSources));

    [Theory]
    [InlineData("SELECT 'Cu|")]
    [InlineData("SELECT N'It''s Cu|'")]
    [InlineData("-- SELECT * FROM Cu|")]
    [InlineData("/* outer /* nested */ Cu| */")]
    [InlineData("SELECT @Cu|")]
    [InlineData("SELECT #Cu|")]
    public void StringsCommentsAndVariablesDoNotSuggestObjects(string sql)
    {
        var offset = sql.IndexOf('|');
        Assert.Null(SqlCompletion.Analyze(sql.Remove(offset, 1), offset, Objects));
    }

    [Fact]
    public void CommentsDoNotInventAliasesAndNamesAreSafelyQuoted()
    {
        var context = Context("SELECT c.| /* JOIN sales.Orders c */ FROM dbo.Customers c");
        Assert.Equal(Customers, Assert.Single(context.ColumnSources));
        var quoted = Context("SELECT * FROM [odd].[a|b]]c]", [new("odd", "ab]c", "Table")]);
        Assert.Equal("[a", quoted.Prefix);
        Assert.Equal("[ab]]c]", Assert.Single(quoted.Items).InsertionText);
        Assert.Equal("[ab]]c]", Assert.Single(quoted.Items).Text);
        Assert.Equal(quoted.StartOffset + "[ab]]c]".Length, quoted.EndOffset);
        var column = context.Column(Customers, new(1, "a]b", "nvarchar(20)", true));
        Assert.Equal("[a]]b]", column.InsertionText);
    }

    [Fact]
    public void UnqualifiedColumnsUseOnlyVisibleSourcesAndKeywordsWorkDisconnected()
    {
        Assert.Equal(Customers, Assert.Single(Context("SELECT Na| FROM dbo.Customers c").ColumnSources));
        var disconnected = Context("SEL|", []);
        Assert.Contains(disconnected.Items, i => i.Text == "SELECT" && i.Description == "T-SQL keyword");
        Assert.Empty(disconnected.ColumnSources);
        Assert.NotNull(SqlCompletion.Analyze("", 0, []));
    }

    [Theory]
    [InlineData("CRE|", "CREATE TABLE")]
    [InlineData("ALT|", "ALTER TABLE")]
    [InlineData("DRO|", "DROP TABLE IF EXISTS")]
    [InlineData("TRU|", "TRUNCATE TABLE")]
    [InlineData("INS|", "INSERT INTO")]
    [InlineData("DEL|", "DELETE FROM")]
    [InlineData("ORD|", "ORDER BY")]
    [InlineData("GRO|", "GROUP BY")]
    [InlineData("BEG|", "BEGIN TRANSACTION")]
    [InlineData("ROL|", "ROLLBACK TRANSACTION")]
    public void SqlCommandPhrasesAreAvailableWithoutAConnection(string sql, string expected)
    {
        var context = Context(sql, []);
        Assert.Contains(context.Items, i => i.Text == expected && i.InsertionText == expected && i.IsSql);
    }

    [Theory]
    [InlineData("ORDER B|", "BY")]
    [InlineData("group |", "BY")]
    [InlineData("CREATE |", "TABLE")]
    [InlineData("CREATE OR |", "ALTER PROCEDURE")]
    [InlineData("CREATE OR ALTER PR|", "PROCEDURE")]
    [InlineData("LEFT OUTER J|", "JOIN")]
    [InlineData("INSERT I|", "INTO")]
    [InlineData("BEGIN TR|", "TRANSACTION")]
    [InlineData("SET NOCOUNT |", "ON")]
    [InlineData("DROP TABLE IF EX|", "EXISTS")]
    public void ContinuationsInsertOnlyTheMissingWords(string sql, string expected)
    {
        var context = Context(sql, []);
        Assert.Contains(context.Items, i => i.IsCommandContinuation && i.Text == expected && i.InsertionText == expected);
        Assert.True(context.SuggestAfterSpace);
    }

    [Fact]
    public void CommandContinuationDoesNotCrossPunctuationOrEnterIdentifiers()
    {
        Assert.DoesNotContain(Context("ORDER; B|", []).Items, i => i.IsCommandContinuation && i.Text == "BY");
        Assert.DoesNotContain(Context("SELECT [ORDER] B|", []).Items, i => i.IsCommandContinuation && i.Text == "BY");
        Assert.DoesNotContain(Context("SELECT c.| FROM dbo.Customers c").Items, i => i.IsSql);
        Assert.False(Context("SELECT * FROM dbo.Customers |", []).SuggestAfterSpace);
        // Adding types/functions to completion must not mistake ordinary aliases for keywords.
        Assert.Equal(Customers, Assert.Single(Context("SELECT name.| FROM dbo.Customers name").ColumnSources));
    }
}
