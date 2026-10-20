using System.Text.Json;
using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class AssistantDatabaseTests
{
    [Theory]
    [InlineData("list_objects", "{}", "'filter' field is missing")]
    [InlineData("list_objects", "{\"filter\":[]}", "'filter' must be text; received an array")]
    [InlineData("list_objects", "{\"filter\":null}", "'filter' must be text; received null")]
    [InlineData("list_objects", "[]", "instead of an object")]
    [InlineData("list_objects", "{", "malformed JSON")]
    [InlineData("query", "{\"sql\":42,\"reason\":\"check\"}", "'sql' must be text; received a number")]
    [InlineData("query", "{\"sql\":\"SELECT 1;\"}", "'reason' field is missing")]
    [InlineData("query", "{\"sql\":\" \",\"reason\":\"check\"}", "'sql' is empty")]
    [InlineData("describe_object", "{\"schema\":\"dbo\",\"name\":\"\"}", "'name' is empty")]
    [InlineData("list_objects", "{\"filter\":\"\",\"extra-secret\":\"private-value\"}", "unsupported fields")]
    [InlineData("list_objects", "{\"filter\":\"\",\"filter\":\"Customers\"}", "'filter' field appears more than once")]
    public async Task InvalidArgumentsExplainTheFieldAndReturnRecoverableFeedbackWithoutCallingTheDatabase(string tool, string arguments, string expected)
    {
        await using var database = new Database { CanQuery = true };
        var approvals = 0; var activity = new List<string>();
        var tools = new AssistantDatabaseTools(database, true, (_, _) => { approvals++; return Task.FromResult(true); }, activity.Add);
        using var result = JsonDocument.Parse(await tools.InvokeAsync(tool, arguments, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_tool_arguments", result.RootElement.GetProperty("code").GetString());
        Assert.False(result.RootElement.GetProperty("executed").GetBoolean());
        var message = result.RootElement.GetProperty("message").GetString()!;
        Assert.Contains(tool, message); Assert.Contains(expected, message);
        Assert.Contains("No database request was executed", message);
        Assert.DoesNotContain("extra-secret", message); Assert.DoesNotContain("private-value", message);
        Assert.Equal(message, tools.LastRequestIssue); Assert.Contains(message, activity);
        Assert.Equal(0, approvals); Assert.Equal(0, database.Queries); Assert.Equal(0, database.SchemaCalls);
        await tools.InvokeAsync("list_objects", "{\"filter\":\"\"}", TestContext.Current.CancellationToken);
        Assert.Equal(1, database.SchemaCalls);
    }

    [Theory]
    [InlineData("filter", 129, "128")]
    [InlineData("sql", 12001, "12,000")]
    [InlineData("reason", 1001, "1,000")]
    public async Task LengthRejectionsReportTheFieldSizeAndLimitWithoutEchoingValues(string field, int length, string limit)
    {
        await using var database = new Database { CanQuery = true };
        var approvals = 0;
        var tools = new AssistantDatabaseTools(database, true, (_, _) => { approvals++; return Task.FromResult(true); });
        var text = "private-value-" + new string('x', length - 14);
        var arguments = field == "filter" ? JsonSerializer.Serialize(new { filter = text }) :
            JsonSerializer.Serialize(new { sql = field == "sql" ? text : "SELECT 1;", reason = field == "reason" ? text : "Check" });
        using var result = JsonDocument.Parse(await tools.InvokeAsync(field == "filter" ? "list_objects" : "query", arguments, TestContext.Current.CancellationToken));
        var message = result.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("'" + field + "'", message); Assert.Contains("limit is " + limit, message);
        Assert.DoesNotContain("private-value", message);
        Assert.Equal(0, approvals); Assert.Equal(0, database.Queries); Assert.Equal(0, database.SchemaCalls);
    }

    [Fact]
    public void SqlSyntaxFailuresIdentifyTheLineAndColumn()
    {
        var error = Assert.Throws<AssistantSqlPolicyException>(() => ReadOnlySqlPolicy.Validate("SELECT 1\nWHERE (1 = );"));
        Assert.Contains("SQL syntax error at line 2, column", error.Message);
    }

    [Fact]
    public async Task RuntimeFailuresRetainTheDatabaseReasonAndDoNotClaimTheRequestWasNeverExecuted()
    {
        await using var database = new Database { CanQuery = true, QueryError = "SQL Server error 207, line 3: Invalid column name 'MissingDate'." };
        var tools = new AssistantDatabaseTools(database, false, (_, _) => Task.FromResult(true));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.InvokeAsync("query", "{\"sql\":\"SELECT MissingDate FROM dbo.Customers;\",\"reason\":\"Check dates\"}", TestContext.Current.CancellationToken));
        Assert.Contains("Read-only query (query) failed", error.Message);
        Assert.Contains("error 207, line 3", error.Message); Assert.Contains("MissingDate", error.Message);
        Assert.DoesNotContain("No database request was executed", error.Message);
        Assert.Equal(error.Message, tools.LastRequestIssue); Assert.Equal(1, database.Queries);
    }

    [Theory]
    [InlineData("SELECT 42;")]
    [InlineData("-- UPDATE is only a comment\n SELECT 'DROP TABLE' AS [text];")]
    [InlineData("SELECT TOP (10) c.CustomerId, COUNT(*) AS Total FROM dbo.Customers c INNER JOIN dbo.Orders o ON c.CustomerId = o.CustomerId WHERE c.Name LIKE 'A%' GROUP BY c.CustomerId HAVING COUNT(*) > 0 ORDER BY c.CustomerId;")]
    [InlineData("WITH c AS (SELECT CustomerId, SUM(Total) AS Total FROM dbo.Orders GROUP BY CustomerId) SELECT * FROM c WHERE Total > 10;")]
    [InlineData("SELECT CAST(42 AS decimal(10,2)), COALESCE(NULL, 'x'), CASE WHEN 1 = 1 THEN 1 ELSE 0 END;")]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY CustomerId) AS n FROM dbo.Customers;")]
    [InlineData("SELECT a.CustomerId FROM dbo.Customers a WHERE EXISTS (SELECT 1 FROM dbo.Orders b WHERE b.CustomerId=a.CustomerId);")]
    [InlineData("SELECT DATEADD(week, DATEDIFF(week, 0, GETDATE()), 0);")]
    [InlineData("SELECT DATETRUNC(iso_week, SYSUTCDATETIME()), DATEPART(weekday, GETDATE()), @@DATEFIRST;")]
    [InlineData("SELECT TRY_CAST(qty AS decimal(18,2)), TRY_CONVERT(decimal(18,2), DiscountPrice) FROM dbo.POitems;")]
    [InlineData("SELECT SUM(poi.qty), SUM(poi.DiscountPrice * poi.qty) FROM dbo.PO po JOIN dbo.POitems poi ON po.po_id = poi.po_id WHERE po.[date] >= DATEADD(day, -(DATEDIFF(day, '19000101', CAST(GETDATE() AS date)) % 7), CAST(GETDATE() AS date)) AND po.[date] < DATEADD(day, 7 - DATEDIFF(day, '19000101', CAST(GETDATE() AS date)) % 7, CAST(GETDATE() AS date));")]
    [InlineData("WITH bounds AS (SELECT DATEADD(day, -(DATEPART(weekday, GETDATE()) + @@DATEFIRST - 2) % 7, CAST(GETDATE() AS date)) AS StartDate) SELECT SUM(qty) FROM dbo.POitems CROSS JOIN bounds;")]
    public void OrdinarySelectsAndCtesAreAllowed(string sql) => ReadOnlySqlPolicy.Validate(sql);

    [Theory]
    [InlineData("DELETE FROM dbo.Customers;")]
    [InlineData("SELECT 1; DROP TABLE dbo.Customers;")]
    [InlineData("WITH c AS (SELECT * FROM dbo.Customers) DELETE FROM c;")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Customers;")]
    [InlineData("SELECT @x = 1;")]
    [InlineData("SELECT NEXT VALUE FOR dbo.sequence;")]
    [InlineData("SELECT * FROM otherdb.dbo.Customers;")]
    [InlineData("SELECT * FROM [server].[db].[dbo].[table];")]
    [InlineData("SELECT * FROM OPENROWSET(BULK '/etc/passwd', SINGLE_CLOB) x;")]
    [InlineData("SELECT * FROM OPENQUERY(remote, 'DELETE FROM t');")]
    [InlineData("SELECT dbo.CustomFunction();")]
    [InlineData("SELECT * FROM dbo.TableFunction();")]
    [InlineData("SELECT * FROM dbo.Customers WITH (UPDLOCK);")]
    [InlineData("SELECT * FROM dbo.Customers OPTION (MAXDOP 1);")]
    [InlineData("SELECT * FROM #tmp;")]
    [InlineData("EXEC('SELECT 1');")]
    [InlineData("SET ROWCOUNT 0; SELECT 1;")]
    [InlineData("SELECT 1;\nGO\nSELECT 2;")]
    [InlineData("SELECT GET_FILESTREAM_TRANSACTION_CONTEXT();")]
    [InlineData("SELECT * FROM dbo.Customers c CROSS APPLY OPENJSON(c.Name);")]
    [InlineData("SELECT @@SERVERNAME;")]
    [InlineData("SELECT @@ROWCOUNT;")]
    [InlineData("SELECT DATEADD(day, 1, dbo.CustomFunction());")]
    [InlineData("SELECT TRY_CAST(dbo.CustomFunction() AS int);")]
    [InlineData("SELECT DATEPART(week, GETDATE()) FROM otherdb.dbo.PO;")]
    [InlineData("SELECT DATEADD(day, 1, GETDATE()) INTO dbo.Copy;")]
    [InlineData("DECLARE @start date = GETDATE(); SELECT SUM(qty) FROM dbo.POitems;")]
    public void WritesAndUnsupportedSyntaxAreRejected(string sql)
        => Assert.ThrowsAny<Exception>(() => ReadOnlySqlPolicy.Validate(sql));

    [Fact]
    public async Task SqlPolicyRejectionsReturnFeedbackWithoutApprovalOrExecutionAndSafeCorrectionsRequireApproval()
    {
        await using var database = new Database { CanQuery = true };
        var approvals = 0; var activity = new List<string>();
        var tools = new AssistantDatabaseTools(database, false, (request, _) =>
        {
            ReadOnlySqlPolicy.Validate(request.Sql); approvals++; return Task.FromResult(true);
        }, activity.Add);
        var rejected = await tools.InvokeAsync("query", JsonSerializer.Serialize(new { sql = "DECLARE @week date = GETDATE(); SELECT @week;", reason = "Current week" }), TestContext.Current.CancellationToken);
        using var result = JsonDocument.Parse(rejected);
        Assert.Equal("sql_policy_rejected", result.RootElement.GetProperty("code").GetString());
        Assert.False(result.RootElement.GetProperty("executed").GetBoolean());
        Assert.Equal(0, approvals); Assert.Equal(0, database.Queries);
        Assert.Contains(activity, entry => entry.Contains("No SQL was executed"));
        await tools.InvokeAsync("query", JsonSerializer.Serialize(new { sql = "SELECT DATEADD(week, DATEDIFF(week, 0, GETDATE()), 0);", reason = "Current week using one SELECT" }), TestContext.Current.CancellationToken);
        Assert.Equal(1, approvals); Assert.Equal(1, database.Queries);
        tools = new(database, false, (_, _) => throw new InvalidOperationException("Approval service failed."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.InvokeAsync("query", "{\"sql\":\"SELECT 1;\",\"reason\":\"Test\"}", TestContext.Current.CancellationToken));
        Assert.Equal(1, database.Queries);
    }

    [Fact]
    public async Task ToolsRespectCapabilitiesAndNeverExecuteDeclinedQueries()
    {
        await using var database = new Database { CanQuery = true };
        var decisions = new List<AssistantQueryApproval>(); var log = new List<string>();
        var tools = new AssistantDatabaseTools(database, false, (request, _) => { decisions.Add(request); return Task.FromResult(false); }, log.Add);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.InvokeAsync("list_objects", "{\"filter\":\"\"}", TestContext.Current.CancellationToken));
        var result = await tools.InvokeAsync("query", "{\"sql\":\"SELECT 1;\",\"reason\":\"Count orders\"}", TestContext.Current.CancellationToken);
        Assert.Equal("declined", JsonDocument.Parse(result).RootElement.GetProperty("status").GetString());
        Assert.Equal("Test database", Assert.Single(decisions).Target);
        await tools.InvokeAsync("query", "{\"sql\":\"SELECT 2;\",\"reason\":\"Retry\"}", TestContext.Current.CancellationToken);
        Assert.Single(decisions);
        Assert.Equal(0, database.Queries);
        Assert.Contains(log, entry => entry.Contains("declined"));
    }

    [Fact]
    public async Task ApprovedQueryExecutesOnlyOnceAndCancellationAfterApprovalRunsNothing()
    {
        await using var database = new Database { CanQuery = true };
        var tools = new AssistantDatabaseTools(database, true, (_, _) => Task.FromResult(true));
        Assert.Contains("success", await tools.InvokeAsync("query", "{\"sql\":\"SELECT 1;\",\"reason\":\"Test\"}", TestContext.Current.CancellationToken));
        Assert.Equal(1, database.Queries);
        using var cancellation = new CancellationTokenSource();
        tools = new AssistantDatabaseTools(database, true, (_, _) => { cancellation.Cancel(); return Task.FromResult(true); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.InvokeAsync("query", "{\"sql\":\"SELECT 1;\",\"reason\":\"Test\"}", cancellation.Token));
        Assert.Equal(1, database.Queries);
        database.CanQuery = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.InvokeAsync("query", "{\"sql\":\"SELECT 1;\",\"reason\":\"Test\"}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SchemaInspectionFetchesUnexpandedColumnsAndNeverRunsSql()
    {
        var session = new DemoQuerySession();
        await using var database = new AssistantDatabaseSession(session, "Demo");
        Assert.False(database.CanQuery);
        var objects = await database.ListObjectsAsync("Customers", TestContext.Current.CancellationToken);
        Assert.Contains("Customers", objects); Assert.DoesNotContain("Orders", objects);
        var columns = await database.DescribeObjectAsync("dbo", "Customers", TestContext.Current.CancellationToken);
        Assert.Contains("CustomerId", columns); Assert.Contains("nvarchar(320)", columns);
        Assert.DoesNotContain("Northwind", columns);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.QueryAsync("SELECT 1;", TestContext.Current.CancellationToken));
        await database.EnableQueriesAsync(TestContext.Current.CancellationToken);
        Assert.True(database.CanQuery);
        var result = await database.QueryAsync("SELECT 1;", TestContext.Current.CancellationToken);
        Assert.Contains("Northwind", result);
    }

    [Fact]
    public async Task AssistantConnectionsUseSeparateSessionsAndTheActualConnectedCredentials()
    {
        var sessions = new List<Session>();
        await using var source = new Rowfinch.Desktop.ViewModels.QueryTabViewModel(sessionFactory: () => { var candidate = new Session(); sessions.Add(candidate); return candidate; });
        source.Server = "original.example"; source.Database = "original-db"; source.UserName = "reader"; source.Password = "in-memory-secret";
        await source.ConnectAsync();
        Assert.True(source.IsConnected);
        source.Server = "edited.example"; source.Database = "edited-db"; source.UserName = "edited-user"; source.Password = "edited-secret";
        await using (var assistant = await source.OpenAssistantDatabaseAsync(true, TestContext.Current.CancellationToken))
        {
            Assert.Equal(2, sessions.Count);
            Assert.Contains("original-db", assistant.Target); Assert.DoesNotContain("edited", assistant.Target);
            Assert.Equal("original.example", sessions[1].OpenedProfile!.Server);
            Assert.Equal("in-memory-secret", sessions[1].OpenedPassword);
            Assert.True(sessions[1].ReadOnlyChecked);
            Assert.False(sessions[0].ReadOnlyChecked);
            Assert.True(assistant.CanQuery);
        }
        Assert.True(sessions[1].Disposed); Assert.False(sessions[0].Disposed); Assert.True(source.IsConnected);
        sessions[0].FailValidation = true; // Only the new candidate should be checked.
        await using var schema = await source.OpenAssistantDatabaseAsync(false, TestContext.Current.CancellationToken);
        Assert.False(sessions[2].ReadOnlyChecked);
    }

    [Fact]
    public async Task RejectedQueryIdentityClosesTheCandidateAndPreservesTheNormalConnection()
    {
        var sessions = new List<Session>();
        await using var source = new Rowfinch.Desktop.ViewModels.QueryTabViewModel(sessionFactory: () => { var candidate = new Session { FailValidation = sessions.Count > 0 }; sessions.Add(candidate); return candidate; });
        source.Server = "test.example"; source.Database = "db"; source.UserName = "writer"; source.Password = "secret";
        await source.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.OpenAssistantDatabaseAsync(true, TestContext.Current.CancellationToken));
        Assert.True(sessions[1].Disposed); Assert.False(sessions[0].Disposed); Assert.True(source.IsConnected);
    }

    private sealed class Session : IQuerySession, IAssistantReadOnlySession
    {
        public bool IsConnected { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailValidation { get; set; }
        public bool ReadOnlyChecked { get; private set; }
        public ConnectionProfile? OpenedProfile { get; private set; }
        public string? OpenedPassword { get; private set; }
        public Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken token) { IsConnected = true; OpenedProfile = profile; OpenedPassword = password; return Task.CompletedTask; }
        public Task ValidateReadOnlyAccessAsync(CancellationToken token)
        { ReadOnlyChecked = true; if (FailValidation) throw new InvalidOperationException("Account is not read-only."); return Task.CompletedTask; }
        public Task<QueryResultSet> ExecuteReadOnlyAsync(string sql, CancellationToken token) => throw new NotImplementedException();
        public Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds, IProgress<string>? messages, CancellationToken token) => throw new InvalidOperationException("Normal query execution must never be used for assistant access.");
        public Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<DatabaseObject>>([]);
        public Task<int> GetTransactionCountAsync(CancellationToken token) => Task.FromResult(0);
        public ValueTask DisposeAsync() { Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
    }

    private sealed class Database : IAssistantDatabaseSession
    {
        public string Target => "Test database";
        public bool CanQuery { get; set; }
        public int Queries { get; private set; }
        public string? QueryError { get; init; }
        public int SchemaCalls { get; private set; }
        public Task<string> ListObjectsAsync(string filter, CancellationToken token) { SchemaCalls++; return Task.FromResult("[]"); }
        public Task<string> DescribeObjectAsync(string schema, string name, CancellationToken token) { SchemaCalls++; return Task.FromResult("[]"); }
        public Task<string> QueryAsync(string sql, CancellationToken token)
        {
            Queries++;
            if (QueryError is { } error) throw new InvalidOperationException(error);
            return Task.FromResult("{\"status\":\"success\"}");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
