using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Rowfinch.Infrastructure;

/// <summary>A deliberately small SELECT grammar, in addition to database-enforced permissions.</summary>
public static class ReadOnlySqlPolicy
{
    public static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 12_000) throw new ArgumentException("Enter one SELECT of at most 12,000 characters.");
        var fragment = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        if (errors.Count != 0 || fragment is not TSqlScript { Batches.Count: 1 } script ||
            script.Batches[0].Statements.Count != 1 || script.Batches[0].Statements[0] is not SelectStatement)
            throw new InvalidOperationException("Assistant queries must contain exactly one valid SELECT statement, optionally with a CTE.");
        fragment.Accept(new ReadOnlyVisitor());
    }

    private sealed class ReadOnlyVisitor : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> AllowedNodes = new(StringComparer.Ordinal)
        {
            "TSqlScript", "TSqlBatch", "SelectStatement", "QuerySpecification", "QueryParenthesisExpression", "BinaryQueryExpression",
            "WithCtesAndXmlNamespaces", "CommonTableExpression", "NamedTableReference", "SchemaObjectName", "Identifier",
            "SelectScalarExpression", "SelectStarExpression", "ColumnReferenceExpression", "MultiPartIdentifier", "IdentifierOrValueExpression",
            "FromClause", "WhereClause", "BooleanComparisonExpression", "BooleanBinaryExpression", "BooleanParenthesisExpression",
            "BooleanNotExpression", "BooleanIsNullExpression", "BooleanTernaryExpression", "InPredicate", "LikePredicate", "ExistsPredicate",
            "ParenthesisExpression", "ScalarSubquery", "BinaryExpression", "UnaryExpression", "FunctionCall", "ParameterlessCall",
            "IntegerLiteral", "NumericLiteral", "RealLiteral", "StringLiteral", "NullLiteral", "MoneyLiteral", "BinaryLiteral",
            "TopRowFilter", "OrderByClause", "ExpressionWithSortOrder", "GroupByClause", "ExpressionGroupingSpecification", "HavingClause",
            "QualifiedJoin", "UnqualifiedJoin", "JoinParenthesisTableReference", "QueryDerivedTable", "InlineDerivedTable", "RowValue",
            "CastCall", "ConvertCall", "SqlDataTypeReference", "SimpleCaseExpression", "SimpleWhenClause", "SearchedCaseExpression", "SearchedWhenClause",
            "OverClause", "WindowFrameClause", "WindowDelimiter", "OffsetClause", "CoalesceExpression", "NullIfExpression", "IIfCall",
            "ExtractFromExpression", "WithinGroupClause"
        };
        private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
        {
            "COUNT", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP", "STRING_AGG",
            "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE",
            "ISNULL", "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT", "SIGN", "LOG", "LOG10", "EXP",
            "LEN", "DATALENGTH", "LOWER", "UPPER", "LTRIM", "RTRIM", "TRIM", "LEFT", "RIGHT", "SUBSTRING", "REPLACE",
            "CONCAT", "CONCAT_WS", "CHARINDEX", "PATINDEX", "UNICODE", "CHAR", "NCHAR", "REVERSE", "SPACE", "REPLICATE",
            "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "YEAR", "MONTH", "DAY",
            "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATEFROMPARTS", "EOMONTH", "DATETRUNC",
            "JSON_VALUE", "JSON_QUERY", "ISJSON", "TRY_CAST", "TRY_CONVERT", "DB_NAME", "SCHEMA_NAME", "OBJECT_NAME", "USER_NAME"
        };
        public override void Visit(TSqlFragment node)
        {
            if (!AllowedNodes.Contains(node.GetType().Name)) Reject();
            if (node is SelectStatement select && (select.Into is not null || select.On is not null || select.OptimizerHints.Count > 0)) Reject();
            if (node is NamedTableReference table && (table.SchemaObject.DatabaseIdentifier is not null || table.SchemaObject.ServerIdentifier is not null ||
                table.SchemaObject.BaseIdentifier.Value.StartsWith('#') || table.TableHints.Count > 0)) Reject();
            if (node is FunctionCall function && (function.CallTarget is not null || !Functions.Contains(function.FunctionName.Value))) Reject();
            base.Visit(node);
        }
        private static void Reject() => throw new InvalidOperationException("This SQL is outside the assistant's restricted SELECT syntax. Writes, INTO, variables, hints, external/cross-database sources and user-defined functions are blocked. Open it in an editor tab for manual review instead.");
    }
}
