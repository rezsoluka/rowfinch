using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Xunit;

namespace Rowfinch.Tests;

public sealed class ObjectBrowserTests
{
    [Fact]
    public async Task CompletionUsesUnfilteredMetadataCachesColumnsAndPreservesTheWorkspace()
    {
        var session = new MetadataSession
        {
            ColumnsOverride = [new(1, "Name", "nvarchar(20)", true), new(2, "Internal", "int", false, IsHidden: true)]
        };
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        await tab.ExecuteAsync("SELECT 1;");
        var originalSql = tab.Sql;
        var result = tab.SelectedResult;
        tab.ObjectFilter = "missing";
        tab.ShowProcedures = true;
        Assert.Empty(tab.Objects);
        var objects = await tab.GetCompletionsAsync("SELECT * FROM Cu", 16);
        Assert.Contains(objects!.Items, i => i.InsertionText == "[dbo].[Customers]");
        const string sql = "SELECT c. FROM dbo.Customers c";
        var columns = await tab.GetCompletionsAsync(sql, 9);
        Assert.Equal("[Name]", Assert.Single(columns!.Items).InsertionText);
        Assert.Equal(1, session.ColumnCalls);
        await tab.GetCompletionsAsync(sql, 9);
        Assert.Equal(1, session.ColumnCalls);
        Assert.Equal(1, session.ExecutionCalls);
        Assert.Equal(originalSql, tab.Sql);
        Assert.Same(result, tab.SelectedResult);
        await tab.RefreshObjectsAsync();
        await tab.GetCompletionsAsync(sql, 9);
        Assert.Equal(2, session.ColumnCalls);
        await tab.DisconnectAsync();
        var disconnected = await tab.GetCompletionsAsync("SELECT * FROM Cu", 16);
        Assert.Empty(disconnected!.Items);
    }

    [Fact]
    public async Task ConcurrentCompletionsShareAColumnLoadAndRejectALostConnection()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new MetadataSession { ColumnGate = gate };
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        const string sql = "SELECT c. FROM dbo.Customers c";
        var first = tab.GetCompletionsAsync(sql, 9);
        await session.ColumnsStarted.Task;
        var second = tab.GetCompletionsAsync(sql, 9);
        Assert.Equal(1, session.ColumnCalls);
        gate.SetResult();
        Assert.Single((await first)!.Items);
        Assert.Single((await second)!.Items);
        await tab.RefreshObjectsAsync();
        session.ColumnGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = tab.GetCompletionsAsync(sql, 9);
        session.LoseConnection();
        session.ColumnGate.SetResult();
        Assert.Null(await stale);
    }

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
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), SessionFactory(session));
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
        await generated.ConnectionReady;
        Assert.True(generated.IsConnected);
        Assert.True(generated.IsDirty);
        Assert.Equal("CREATE VIEW [dbo].[CustomerView] AS SELECT 1 AS [Id];", generated.Sql);
        Assert.Equal(0, session.ExecutionCalls);
    }

    [Fact]
    public async Task GenerateSelectCachesColumnsAndNeverExecutesSql()
    {
        var session = new MetadataSession();
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), SessionFactory(session));
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
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), SessionFactory(session));
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
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), SessionFactory(session));
        var origin = workspace.SelectedTab!;
        origin.Server = "test"; origin.Database = "db"; origin.UserName = "reader";
        await origin.ConnectAsync();
        await origin.OpenDefinitionAsync(origin.Objects[1]);
        Assert.Single(workspace.Tabs);
        Assert.Contains("Definition unavailable", origin.Messages);
    }

    private static Func<IQuerySession> SessionFactory(MetadataSession source)
    {
        var usedSource = false;
        return () =>
        {
            if (usedSource) return new MetadataSession();
            usedSource = true;
            return source;
        };
    }

    private static QueryTabViewModel CreateTab(MetadataSession session) => new(new MemoryProfiles(), () => session)
        { Server = "test.database.windows.net", Database = "test", UserName = "reader" };

    [Fact]
    public async Task ObjectTypeSelectionsCombineWithTextAndAllRestoresEveryKind()
    {
        await using var tab = CreateTab(new MetadataSession());
        await tab.ConnectAsync();
        Assert.True(tab.AllObjectTypes);
        Assert.Equal(4, tab.Objects.Count);
        tab.ShowTables = true;
        Assert.Equal("Table", Assert.Single(tab.Objects).Object!.Kind);
        tab.ShowViews = true;
        Assert.Equal(2, tab.Objects.Count);
        tab.ObjectFilter = "  customerVIEW  ";
        Assert.Equal("View", Assert.Single(tab.Objects).Object!.Kind);
        tab.ShowViews = false;
        Assert.Empty(tab.Objects);
        tab.AllObjectTypes = true;
        Assert.Equal("View", Assert.Single(tab.Objects).Object!.Kind);
        tab.ObjectFilter = "";
        tab.ShowFunctions = true;
        Assert.Equal("Function", Assert.Single(tab.Objects).Object!.Kind);
        tab.ShowFunctions = false;
        Assert.True(tab.AllObjectTypes);
        Assert.Equal(4, tab.Objects.Count);
    }

    [Fact]
    public async Task SelectedColumnsDriveEveryGeneratorWithoutResettingOrExecuting()
    {
        var session = new MetadataSession
        {
            ColumnsOverride = [new(1, "Id", "int", false, 1, IsIdentity: true), new(2, "Name", "nvarchar(100)", false), new(3, "Email", "nvarchar(100)", true)]
        };
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), SessionFactory(session));
        var source = workspace.SelectedTab!;
        source.Server = "test"; source.Database = "db"; source.UserName = "reader";
        await source.ConnectAsync();
        var sql = source.Sql;
        var root = source.Objects[0];
        root.IsExpanded = true;
        await root.LoadTask;
        root.UnselectAllColumnsCommand!.Execute(null);
        var email = Assert.Single(root.Children, c => c.Label == "Email");
        email.IsSelected = true;
        source.ObjectFilter = "missing";
        source.ObjectFilter = "";
        Assert.Same(root, source.Objects[0]);
        await source.GenerateSelectAsync(root);
        Assert.Contains("SELECT\n    [Email]\nFROM", workspace.SelectedTab!.Sql);
        await source.GenerateInsertAsync(root);
        Assert.Contains("INSERT INTO [dbo].[Customers] (\n    [Email]", workspace.SelectedTab!.Sql);
        await source.GenerateUpdateAsync(root);
        Assert.Contains("SET\n    [Email] = <value_1>\nWHERE\n    [Id] = <key_1>;", workspace.SelectedTab!.Sql);
        Assert.Same(email, Assert.Single(root.Children, c => c.Label == "Email"));
        Assert.Equal("Email", Assert.Single(root.SelectedColumns).Name);
        Assert.Equal(sql, source.Sql);
        Assert.Equal("db", workspace.SelectedTab.Database);
        await workspace.SelectedTab.ConnectionReady;
        Assert.True(workspace.SelectedTab.IsConnected);
        Assert.True(workspace.SelectedTab.IsDirty);
        Assert.Equal(0, session.ExecutionCalls);
        Assert.Equal(1, session.ColumnCalls);
        root.UnselectAllColumnsCommand.Execute(null);
        var count = workspace.Tabs.Count;
        await source.GenerateSelectAsync(root);
        await source.GenerateInsertAsync(root);
        await source.GenerateUpdateAsync(root);
        Assert.Equal(count, workspace.Tabs.Count);
        Assert.Contains("Select at least one", source.Messages);
    }

    [Fact]
    public async Task ColumnRefreshPreservesChoicesAndDefaultsNewVisibleColumnsToSelected()
    {
        var session = new MetadataSession { ColumnsOverride = [new(1, "Name", "nvarchar(100)", false), new(2, "Email", "nvarchar(100)", true)] };
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        var root = tab.Objects[0];
        root.IsExpanded = true;
        await root.LoadTask;
        root.Children[0].IsSelected = false;
        session.ColumnsOverride = [new(1, "Name", "nvarchar(200)", false), new(2, "Email", "nvarchar(200)", true), new(3, "NewColumn", "int", true), new(4, "Hidden", "int", false, IsHidden: true)];
        await tab.RefreshObjectAsync(root);
        Assert.False(root.Children[0].IsSelected);
        Assert.Equal(new[] { "Email", "NewColumn" }, root.SelectedColumns.Select(c => c.Name));
        Assert.Equal("2 of 3 selected", root.SelectionSummary);
        root.SelectAllColumnsCommand!.Execute(null);
        Assert.Equal(3, root.SelectedColumns.Count);
        Assert.False(root.Children[3].IsSelected);
        root.UnselectAllColumnsCommand!.Execute(null);
        Assert.Empty(root.SelectedColumns);
    }

    [Fact]
    public async Task CancellingTemplateMetadataLoadDoesNotCreateDocumentsAfterTabSwitch()
    {
        var session = new MetadataSession { WaitForColumns = true };
        await using var workspace = new WorkspaceViewModel(new MemoryProfiles(), SessionFactory(session));
        var origin = workspace.SelectedTab!;
        origin.Server = "test"; origin.Database = "db"; origin.UserName = "reader";
        await origin.ConnectAsync();
        var generation = origin.GenerateInsertAsync(origin.Objects[0]);
        await session.ColumnsStarted.Task;
        var other = workspace.NewTab();
        origin.Cancel();
        await generation;
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Same(other, workspace.SelectedTab);
        Assert.Equal("Cancelled", origin.Status);
        Assert.Equal(0, session.ExecutionCalls);
        Assert.Empty(other.Sql);
    }

    [Fact]
    public async Task ReconnectingWithFailedDiscoveryCannotUseThePreviousDatabaseObjects()
    {
        var session = new MetadataSession();
        await using var tab = CreateTab(session);
        await tab.ConnectAsync();
        var old = tab.Objects[0];
        old.IsExpanded = true;
        await old.LoadTask;
        session.LoseConnection();
        session.FailObjectList = true;
        tab.Database = "other";
        await tab.ConnectAsync();
        Assert.True(tab.IsConnected);
        Assert.Empty(tab.Objects);
        await tab.GenerateInsertAsync(old);
        Assert.Contains("object list has changed", tab.Messages);
        Assert.Equal(1, session.ColumnCalls);
        Assert.Equal(0, session.ExecutionCalls);
    }

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
        public bool FailObjectList { get; set; }
        public void LoseConnection() => IsConnected = false;
        public string ColumnName { get; set; } = "Id";
        public IReadOnlyList<DatabaseColumn>? ColumnsOverride { get; set; }
        public bool WaitForDefinition { get; init; }
        public bool WaitForColumns { get; set; }
        public TaskCompletionSource? ColumnGate { get; set; }
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
            if (FailObjectList) throw new InvalidOperationException("Metadata denied");
            return Task.FromResult<IReadOnlyList<DatabaseObject>>([new("dbo", "Customers", "Table", 1), new("dbo", "CustomerView", "View", 2), new("dbo", "GetCustomer", "Procedure", 3), new("dbo", "CustomerTotal", "Function", 4)]);
        }
        public async Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(DatabaseObject item, CancellationToken cancellationToken)
        {
            ColumnCalls++;
            ColumnsStarted.TrySetResult();
            if (ColumnGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            if (WaitForColumns) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailColumns) throw new InvalidOperationException("Metadata denied");
            return ColumnsOverride ?? [new(1, ColumnName, "int", false, 1)];
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
