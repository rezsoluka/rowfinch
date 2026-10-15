using Rowfinch.Core;
using Xunit;

namespace Rowfinch.Tests;

public sealed class SqlBatchParserTests
{
    [Fact]
    public void PreservesGoInsideMultilineStringsAndNestedComments()
    {
        const string sql = "SELECT 'a''b\nGO\nc';\n/* outer\n/* inner */\nGO\n*/\nGO\nSELECT 2;\nGO 3 -- repeat";
        var batches = SqlBatchParser.Split(sql);
        Assert.Equal(2, batches.Count);
        Assert.Contains("GO", batches[0].Text);
        Assert.Equal(1, batches[0].RepeatCount);
        Assert.Equal(9, batches[1].StartLine);
        Assert.Equal(3, batches[1].RepeatCount);
    }

    [Theory]
    [InlineData("SELECT [a]]\nGO\nb];\nGO\nSELECT 2;")]
    [InlineData("SELECT \"a\"\"\nGO\nb\";\nGO\nSELECT 2;")]
    public void PreservesGoInsideQuotedIdentifiers(string sql) => Assert.Equal(2, SqlBatchParser.Split(sql).Count);

    [Theory]
    [InlineData("GO 0")]
    [InlineData("GO 101")]
    [InlineData("GO 999999999999999999999")]
    [InlineData(":r other.sql")]
    [InlineData(":setvar Database production")]
    [InlineData("!! echo test")]
    public void RejectsUnsupportedDirectivesBeforeExecution(string directive) =>
        Assert.Throws<ArgumentException>(() => SqlBatchParser.Split("SELECT 1;\nGO\n" + directive));

    [Fact]
    public void HandlesCrLfAndEmptyBatches()
    {
        var batch = Assert.Single(SqlBatchParser.Split("GO\r\n\r\nSELECT 1;\r\ngo -- finish\r\nGO\r\n"));
        Assert.Equal(2, batch.StartLine);
        Assert.Contains("SELECT 1;", batch.Text);
    }
}
