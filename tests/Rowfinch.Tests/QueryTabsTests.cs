using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Xunit;

namespace Rowfinch.Tests;

public sealed class QueryTabsTests
{
    [Fact]
    public async Task NewTabCopiesSettingsButNeverSharesPasswordOrConnectsAutomatically()
    {
        var sessionCount = 0;
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => { sessionCount++; return new Session("test"); });
        var first = workspace.SelectedTab!;
        Configure(first, "development");
        first.Password = "session-only-secret";
        var second = workspace.NewTab();
        Assert.Equal(first.Server, second.Server);
        Assert.Equal(first.Database, second.Database);
        Assert.Equal("", second.Password);
        Assert.False(second.IsConnected);
        Assert.False(second.IsDirty);
        Assert.Equal("", second.Sql);
        Assert.Equal(0, sessionCount);
    }

    [Fact]
    public async Task TabsKeepSeparateSessionsDocumentsResultsAndTransactionState()
    {
        var sessions = new Queue<Session>([new("first") { Transactions = 1 }, new("second")]);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => sessions.Dequeue());
        var first = workspace.SelectedTab!;
        Configure(first, "one");
        first.Sql = "SELECT first;";
        await first.ConnectAsync();
        await first.ExecuteAsync();
        var firstResult = first.SelectedResult;
        var second = workspace.NewTab();
        Configure(second, "two");
        second.Sql = "SELECT second;";
        await second.ConnectAsync();
        await second.ExecuteAsync();
        workspace.SelectedTab = first;
        Assert.Equal("SELECT first;", first.Sql);
        Assert.Equal("SELECT second;", second.Sql);
        Assert.Same(firstResult, first.SelectedResult);
        Assert.Equal("first", first.SelectedResult!.Rows[0][0]);
        Assert.Equal("second", second.SelectedResult!.Rows[0][0]);
        Assert.Equal(1, first.TransactionCount);
        Assert.Equal(0, second.TransactionCount);
        Assert.Contains("one", first.ConnectionLabel);
        Assert.Contains("two", second.ConnectionLabel);
    }

    [Fact]
    public async Task CancellationInOneTabDoesNotInterruptAnotherRunningTab()
    {
        var firstSession = new Session("first") { Block = true };
        var secondSession = new Session("second") { Block = true };
        var sessions = new Queue<Session>([firstSession, secondSession]);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => sessions.Dequeue());
        var first = workspace.SelectedTab!;
        Configure(first, "one"); await first.ConnectAsync();
        var firstExecution = first.ExecuteAsync("SELECT first;");
        await firstSession.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = workspace.NewTab();
        Configure(second, "two"); await second.ConnectAsync();
        var secondExecution = second.ExecuteAsync("SELECT second;");
        await secondSession.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        first.Cancel();
        await firstExecution;
        Assert.Equal("Cancelled", first.Status);
        Assert.True(second.IsBusy);
        Assert.Same(second, workspace.SelectedTab);
        secondSession.Release.TrySetResult();
        await secondExecution;
        Assert.Equal("second", second.SelectedResult!.Rows[0][0]);
    }

    [Fact]
    public async Task ClosingRunningBackgroundTabWaitsForItsQueryAndPreservesActiveTab()
    {
        var firstSession = new Session("first") { Block = true };
        var secondSession = new Session("second");
        var sessions = new Queue<Session>([firstSession, secondSession]);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => sessions.Dequeue());
        var first = workspace.SelectedTab!;
        Configure(first, "one"); await first.ConnectAsync();
        var execution = first.ExecuteAsync("SELECT first;");
        await firstSession.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = workspace.NewTab();
        Configure(second, "two"); await second.ConnectAsync();
        await second.ExecuteAsync("SELECT second;");
        var askedToConfirm = false;
        Assert.True(await workspace.TryCloseTabAsync(first, tab =>
        {
            Assert.Same(first, tab); askedToConfirm = true; return Task.FromResult(true);
        }));
        await execution;
        Assert.True(askedToConfirm);
        Assert.True(firstSession.Disposed);
        Assert.False(firstSession.DisposedWhileExecuting);
        Assert.False(secondSession.Disposed);
        Assert.Same(second, workspace.SelectedTab);
        Assert.Equal("second", second.SelectedResult!.Rows[0][0]);
    }

    [Fact]
    public async Task RejectingTabClosePreservesUnsavedSqlAndOpenTransaction()
    {
        var session = new Session("first") { Transactions = 1 };
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => session);
        var tab = workspace.SelectedTab!;
        Configure(tab, "one"); await tab.ConnectAsync();
        tab.Sql = "BEGIN TRANSACTION;";
        await tab.ExecuteAsync();
        Assert.False(await workspace.TryCloseTabAsync(tab, _ => Task.FromResult(false)));
        Assert.True(tab.IsDirty);
        Assert.Equal("BEGIN TRANSACTION;", tab.Sql);
        Assert.Equal(1, tab.TransactionCount);
        Assert.True(tab.CanExecute);
        Assert.False(session.Disposed);
        Assert.Contains(tab, workspace.Tabs);
    }

    [Fact]
    public async Task RejectingWindowCloseLeavesEveryRunningSessionUntouched()
    {
        var sessions = new[] { new Session("first") { Block = true }, new Session("second") { Block = true } };
        var queue = new Queue<Session>(sessions);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => queue.Dequeue());
        var first = workspace.SelectedTab!;
        Configure(first, "one"); await first.ConnectAsync();
        var second = workspace.NewTab();
        Configure(second, "two"); await second.ConnectAsync();
        var executions = new[] { first.ExecuteAsync("SELECT first;"), second.ExecuteAsync("SELECT second;") };
        await Task.WhenAll(sessions.Select(s => s.Started.Task)).WaitAsync(TestContext.Current.CancellationToken);
        var decisions = 0;
        Assert.False(await workspace.TryCloseAllAsync(_ => Task.FromResult(++decisions == 1)));
        Assert.Equal(2, decisions);
        Assert.True(first.IsBusy);
        Assert.True(second.IsBusy);
        Assert.True(workspace.CanManageTabs);
        Assert.All(sessions, s => Assert.False(s.Disposed));
        foreach (var session in sessions) session.Release.TrySetResult();
        await Task.WhenAll(executions);
    }

    [Fact]
    public async Task ClosingLastCleanTabCreatesFreshDisconnectedDocumentWithoutPrompt()
    {
        await using var workspace = new WorkspaceViewModel(new MemoryStore());
        var first = workspace.SelectedTab!;
        Assert.True(await workspace.TryCloseTabAsync(first, _ => throw new InvalidOperationException("A clean tab should not prompt.")));
        var next = Assert.Single(workspace.Tabs);
        Assert.NotSame(first, next);
        Assert.Same(next, workspace.SelectedTab);
        Assert.Equal("", next.Sql);
        Assert.False(next.IsDirty);
        Assert.False(next.IsConnected);
        Assert.NotEqual(first.DocumentName, next.DocumentName);
    }

    [Fact]
    public async Task ConcurrentProfileSavesRemainSharedWithoutOverwritingEachOther()
    {
        var store = new MemoryStore();
        await using var workspace = new WorkspaceViewModel(store);
        await workspace.InitializeAsync();
        var first = workspace.SelectedTab!;
        Configure(first, "one");
        var second = workspace.NewTab();
        Configure(second, "two");
        await Task.WhenAll(first.SaveProfileAsync(), second.SaveProfileAsync());
        Assert.Equal(2, store.Saved.Count);
        Assert.Equal(2, first.Profiles.Count);
        Assert.Same(first.Profiles, second.Profiles);
        Assert.Contains(store.Saved, p => p.Database == "one");
        Assert.Contains(store.Saved, p => p.Database == "two");
        Assert.Equal(1, store.MaximumConcurrentSaves);
    }

    private static void Configure(QueryTabViewModel tab, string database)
    { tab.Server = "test.database.windows.net"; tab.Database = database; tab.UserName = "developer"; tab.ProfileName = database; }

    private sealed class MemoryStore : IProfileStore
    {
        private int activeSaves;
        public int MaximumConcurrentSaves { get; private set; }
        public IReadOnlyList<ConnectionProfile> Saved { get; private set; } = [];
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
        {
            activeSaves++; MaximumConcurrentSaves = Math.Max(MaximumConcurrentSaves, activeSaves);
            await Task.Yield();
            Saved = profiles.ToArray(); activeSaves--;
        }
    }

    private sealed class Session(string identity) : IQuerySession
    {
        private bool executing;
        public bool Block { get; init; }
        public int Transactions { get; init; }
        public bool IsConnected { get; private set; }
        public bool Disposed { get; private set; }
        public bool DisposedWhileExecuting { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken)
        { IsConnected = true; return Task.CompletedTask; }
        public async Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds, IProgress<string>? messages, CancellationToken cancellationToken)
        {
            executing = true; Started.TrySetResult();
            try
            {
                if (Block) await Release.Task.WaitAsync(cancellationToken);
                return new QueryExecution([new("Result 1", [new("Session", "nvarchar"), new("SQL", "nvarchar")], [[identity, sql]], false)], Transactions, TimeSpan.Zero);
            }
            finally { executing = false; }
        }
        public Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DatabaseObject>>([]);
        public Task<int> GetTransactionCountAsync(CancellationToken cancellationToken) => Task.FromResult(Transactions);
        public ValueTask DisposeAsync()
        { DisposedWhileExecuting = executing; Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
    }
}
