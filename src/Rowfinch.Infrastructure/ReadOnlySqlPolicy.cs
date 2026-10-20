using Microsoft.SqlServer.TransactSql.ScriptDom;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

/// <summary>A deliberately small SELECT grammar, in addition to database-enforced permissions.</summary>
public static class ReadOnlySqlPolicy
{
    public static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 12_000) throw new AssistantSqlPolicyException("Use one SELECT of at most 12,000 characters.");
        var fragment = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
        {
            var error = errors[0];
            throw new AssistantSqlPolicyException($"SQL syntax error at line {error.Line}, column {error.Column}: {error.Message} Use one valid read-only SELECT or CTE.");
        }
        if (fragment is not TSqlScript { Batches.Count: 1 } script ||
            script.Batches[0].Statements.Count != 1 || script.Batches[0].Statements[0] is not SelectStatement)
            throw new AssistantSqlPolicyException("Use exactly one valid SELECT statement, optionally with a CTE. DECLARE, SET, local variables and multiple statements are not supported. Use CTEs or inline expressions for date boundaries.");
        fragment.Accept(new ReadOnlyVisitor());
    }

    private sealed class ReadOnlyVisitor : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> AllowedNodes = new(StringComparer.Ordinal)
        {
            "TSqlScript", "TSqlBatch", "SelectStatement", "QuerySpecification", "QueryParenthesisExpression", "BinaryQueryExpression",
            "WithCtesAndXmlNamespaces", "CommonTableExpression", "NamedTableReference", "SchemaObjectName", "Identifier",
            "SelectScalarExpression", "SelectStarExpression", "ColumnReferenceExpression", "MultiPartIdentifier", "IdentifierOrValueExpression", "IdentifierLiteral",
            "FromClause", "WhereClause", "BooleanComparisonExpression", "BooleanBinaryExpression", "BooleanParenthesisExpression",
            "BooleanNotExpression", "BooleanIsNullExpression", "BooleanTernaryExpression", "InPredicate", "LikePredicate", "ExistsPredicate",
            "ParenthesisExpression", "ScalarSubquery", "BinaryExpression", "UnaryExpression", "FunctionCall", "ParameterlessCall",
            "IntegerLiteral", "NumericLiteral", "RealLiteral", "StringLiteral", "NullLiteral", "MoneyLiteral", "BinaryLiteral",
            "TopRowFilter", "OrderByClause", "ExpressionWithSortOrder", "GroupByClause", "ExpressionGroupingSpecification", "HavingClause",
            "QualifiedJoin", "UnqualifiedJoin", "JoinParenthesisTableReference", "QueryDerivedTable", "InlineDerivedTable", "RowValue",
            "CastCall", "ConvertCall", "TryCastCall", "TryConvertCall", "GlobalVariableExpression", "SqlDataTypeReference", "SimpleCaseExpression", "SimpleWhenClause", "SearchedCaseExpression", "SearchedWhenClause",
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
            if (!AllowedNodes.Contains(node.GetType().Name)) Reject("Unsupported SELECT expression (" + node.GetType().Name + "). Use a simpler SELECT or CTE with supported built-in functions.");
            if (node is GlobalVariableExpression global && !string.Equals(global.Name, "@@DATEFIRST", StringComparison.OrdinalIgnoreCase))
                Reject("Only the read-only @@DATEFIRST session value is supported; other global variables are blocked.");
            if (node is SelectStatement select && (select.Into is not null || select.On is not null || select.OptimizerHints.Count > 0)) Reject("SELECT INTO, file targets and query hints are blocked.");
            if (node is NamedTableReference table && (table.SchemaObject.DatabaseIdentifier is not null || table.SchemaObject.ServerIdentifier is not null ||
                table.SchemaObject.BaseIdentifier.Value.StartsWith('#') || table.TableHints.Count > 0)) Reject("Use local database tables or views without table hints or temporary tables. Cross-database and external sources are blocked.");
            if (node is FunctionCall function && (function.CallTarget is not null || !Functions.Contains(function.FunctionName.Value))) Reject("Use supported built-in functions. User-defined, qualified and unknown functions are blocked.");
            base.Visit(node);
        }
        private static void Reject(string reason) => throw new AssistantSqlPolicyException(reason);
    }
}
