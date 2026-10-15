using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Xunit;

namespace Rowfinch.Tests;

public sealed class ObjectBrowserTests
{
    [Theory]
    [InlineData("nvarchar", 400, 0, 0, "nvarchar(200)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 100, 0, 0, "varchar(100)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("datetime2", 8, 0, 7, "datetime2(7)")]
    [InlineData("float", 8, 53, 0, "float(53)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void TypesPreserveSqlLengthsAndPrecision(string name, int length, int precision, int scale, string expected)
        => Assert.Equal(expected, ObjectSql.FormatType(name, length, precision, scale));

    [Fact]
    public void GeneratedSelectQuotesIdentifiersOrdersColumnsAndOmitsHiddenColumns()
    {
        var item = new DatabaseObject("we]ird", "table; DROP TABLE x;--", "Table");
        DatabaseColumn[] columns = [new(3, "hidden", "int", false, IsHidden: true), new(2, "a]b", "int", true), new(1, "O'Brien", "int", false, 2)];
        Assert.Equal("SELECT\n    [O'Brien],\n    [a]]b]\nFROM [we]]ird].[table; DROP TABLE x;--];", ObjectSql.Select(item, columns));
        Assert.Contains("PK 2", columns[2].Description);
        Assert.Contains("NOT NULL", columns[2].Description);
        Assert.Throws<ArgumentException>(() => ObjectSql.Preview(item with { Kind = "Function" }));
        Assert.Throws<InvalidOperationException>(() => ObjectSql.Select(item, []));
    }

    [Fact]
    public async Task ColumnsLoadOnExpansionCacheAcrossFilteringAndRefreshIndividually()
    {
        var session = new MetadataSession();
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        var node = tab.Objects[0];
        Assert.Equal(0, session.ColumnCalls);
        node.IsExpanded = true;
        await node.LoadTask;
        Assert.Equal(1, session.ColumnCalls);
        Assert.Contains("PK 1", node.Children[0].Detail);
        node.IsExpanded = false; node.IsExpanded = true;
        await node.LoadTask;
        tab.ObjectFilter = "missing";
        Assert.Empty(tab.Objects);
        tab.ObjectFilter = "Customers";
        Assert.Same(node, Assert.Single(tab.Objects));
        Assert.Equal(1, session.ColumnCalls);
        session.ColumnName = "RenamedId";
        await tab.RefreshObjectAsync(node);
        Assert.Equal("RenamedId", node.Children[0].Label);
        Assert.Equal(2, session.ColumnCalls);
        Assert.Equal(1, session.ObjectCalls);
    }

    [Fact]
    public async Task FailedMetadataCanBeRetriedWithoutClearingEditorOrResults()
    {
        var session = new MetadataSession { FailColumns = true };
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        await tab.ExecuteAsync("SELECT 1;");
        var sql = tab.Sql;
        var results = tab.SelectedResult;
        var node = tab.Objects[0];
        node.IsExpanded = true;
        await node.LoadTask;
        Assert.Null(node.Columns);
        Assert.Contains("Metadata denied", tab.Messages);
        Assert.Equal(sql, tab.Sql);
        Assert.Same(results, tab.SelectedResult);
        session.FailColumns = false;
        await tab.RefreshObjectAsync(node);
        Assert.NotNull(node.Columns);
    }

    [Fact]
    public async Task CancelledColumnLoadCanBeRetried()
    {
        var session = new MetadataSession { WaitForColumns = true };
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        var node = tab.Objects[0];
        node.IsExpanded = true;
        await session.ColumnsStarted.Task;
        Assert.True(tab.IsBusy);
        tab.Cancel();
        await node.LoadTask;
        Assert.Null(node.Columns);
        Assert.Equal("Cancelled", tab.Status);
        Assert.True(tab.IsConnected);
        session.WaitForColumns = false;
        await tab.RefreshObjectAsync(node);
        Assert.NotNull(node.Columns);
        Assert.Equal(2, session.ColumnCalls);
    }

    [Fact]
    public async Task DefinitionUsesOriginSettingsWhenUserSwitchesTabsWhileLoading()
    {
        var session = new MetadataSession { WaitForDefinition = true };
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), () => session);
        var origin = workspace.SelectedTab!;
        origin.Server = "origin.database.windows.net"; origin.Database = "origin"; origin.UserName = "reader";
        origin.Password = "session-only";
        await origin.ConnectAsync();
        var other = workspace.NewTab();
        other.Server = "other.database.windows.net"; other.Database = "other";
        var load = origin.OpenDefinitionAsync(origin.Objects[1]);
        await session.DefinitionStarted.Task;
        Assert.Same(other, workspace.SelectedTab);
        session.DefinitionGate.TrySetResult();
        await load;
        var generated = workspace.SelectedTab!;
        Assert.Equal(3, workspace.Tabs.Count);
        Assert.Equal("origin", generated.Database);
        Assert.Equal("origin.database.windows.net", generated.Server);
        Assert.Equal("reader", generated.UserName);
        Assert.Empty(generated.Password);
        Assert.False(generated.IsConnected);
        Assert.True(generated.IsDirty);
        Assert.Equal("CREATE VIEW [dbo].[CustomerView] AS SELECT 1 AS [Id];", generated.Sql);
        Assert.Equal(0, session.ExecutionCalls);
    }

    [Fact]
    public async Task GenerateSelectCachesColumnsAndNeverExecutesSql()
    {
        var session = new MetadataSession();
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), () => session);
        var origin = workspace.SelectedTab!;
        origin.Server = "test"; origin.Database = "db"; origin.UserName = "reader";
        await origin.ConnectAsync();
        var sql = origin.Sql;
        await origin.GenerateSelectAsync(origin.Objects[0]);
        Assert.Contains("[Id]", workspace.SelectedTab!.Sql);
        Assert.Equal(sql, origin.Sql);
        Assert.NotNull(origin.Objects[0].Columns);
        await origin.GenerateSelectAsync(origin.Objects[0]);
        Assert.Equal(1, session.ColumnCalls);
        Assert.Equal(0, session.ExecutionCalls);
    }

    [Fact]
    public async Task PreviewUsesSourceSessionPreservesSqlAndRejectsStaleNodes()
    {
        var session = new MetadataSession();
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        var node = tab.Objects[0];
        var sql = tab.Sql;
        await tab.PreviewRowsAsync(node);
        Assert.Equal("SELECT TOP (100) *\nFROM [dbo].[Customers];", session.LastSql);
        Assert.Equal(sql, tab.Sql);
        await tab.RefreshObjectsAsync();
        await tab.PreviewRowsAsync(node);
        await tab.GenerateSelectAsync(node);
        Assert.Equal(1, session.ExecutionCalls);
        Assert.Equal(0, session.ColumnCalls);
        Assert.Contains("object list has changed", tab.Messages);
        await tab.PreviewRowsAsync(tab.Objects[2]);
        Assert.Equal(1, session.ExecutionCalls);
    }

    [Fact]
    public async Task MetadataCancellationAndCloseDoNotCreateLateDocumentsOrOverlapCommands()
    {
        var session = new MetadataSession { WaitForDefinition = true };
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), () => session);
        var origin = workspace.SelectedTab!;
        origin.Server = "test"; origin.Database = "db"; origin.UserName = "reader";
        await origin.ConnectAsync();
        var load = origin.OpenDefinitionAsync(origin.Objects[1]);
        await session.DefinitionStarted.Task;
        await origin.ExecuteAsync("SELECT should_not_run;");
        Assert.Equal(0, session.ExecutionCalls);
        Assert.True(await workspace.TryCloseTabAsync(origin, _ => Task.FromResult(true)));
        await load;
        Assert.Equal("Cancelled", origin.Status);
        Assert.Single(workspace.Tabs);
        Assert.NotSame(origin, workspace.SelectedTab);
        Assert.DoesNotContain(workspace.Tabs, t => t.Sql.Contains("CREATE VIEW", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyDefinitionDoesNotCreateAnEmptyTab()
    {
        var session = new MetadataSession { Definition = "" };
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), () => session);
        var origin = workspace.SelectedTab!;
        origin.Server = "test"; origin.Database = "db"; origin.UserName = "reader";
        await origin.ConnectAsync();
        await origin.OpenDefinitionAsync(origin.Objects[1]);
        Assert.Single(workspace.Tabs);
        Assert.Contains("Definition unavailable", origin.Messages);
    }

    private static QueryTabViewModel CreateTab(MetadataSession session) => new(new MemoryProfiles(), () => session)
        { Server = "test.database.windows.net", Database = "test", UserName = "reader" };

    private sealed class MemoryProfiles : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MetadataSession : IQuerySession, IObjectMetadataSession
    {
        public bool IsConnected { get; private set; }
        public int ColumnCalls { get; private set; }
        public int ObjectCalls { get; private set; }
        public int ExecutionCalls { get; private set; }
        public string LastSql { get; private set; } = "";
        public bool FailColumns { get; set; }
        public string ColumnName { get; set; } = "Id";
        public bool WaitForDefinition { get; init; }
        public bool WaitForColumns { get; set; }
        public TaskCompletionSource ColumnsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Definition { get; init; } = "CREATE VIEW [dbo].[CustomerView] AS SELECT 1 AS [Id];";
        public TaskCompletionSource DefinitionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DefinitionGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken) { IsConnected = true; return Task.CompletedTask; }
        public Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds, IProgress<string>? messages, CancellationToken cancellationToken)
        {
            ExecutionCalls++; LastSql = sql;
            return Task.FromResult(new QueryExecution([new("Result", [new("Id", "int")], [new object?[] { 1 }], false)], 0, TimeSpan.Zero));
        }
        public Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken cancellationToken)
        {
            ObjectCalls++;
            return Task.FromResult<IReadOnlyList<DatabaseObject>>([new("dbo", "Customers", "Table", 1), new("dbo", "CustomerView", "View", 2), new("dbo", "GetCustomer", "Procedure", 3)]);
        }
        public async Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(DatabaseObject item, CancellationToken cancellationToken)
        {
            ColumnCalls++;
            ColumnsStarted.TrySetResult();
            if (WaitForColumns) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailColumns) throw new InvalidOperationException("Metadata denied");
            return [new(1, ColumnName, "int", false, 1)];
        }
        public async Task<string> GetDefinitionAsync(DatabaseObject item, CancellationToken cancellationToken)
        {
            DefinitionStarted.TrySetResult();
            if (WaitForDefinition) await DefinitionGate.Task.WaitAsync(cancellationToken);
            return Definition;
        }
        public Task<int> GetTransactionCountAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
}
