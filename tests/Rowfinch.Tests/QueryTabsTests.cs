using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Rowfinch.Desktop.ViewModels;
using Xunit;

namespace Rowfinch.Tests;

public sealed class QueryTabsTests
{
    [Fact]
    public async Task AssistantNotesUseTheLiveTargetAndInheritedTabsShareThatContext()
    {
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => new Session("notes"));
        var source = workspace.SelectedTab!;
        Assert.Null(source.AssistantContextConnection);
        Configure(source, "orders"); await source.ConnectAsync();
        var connected = source.AssistantContextConnection!;
        source.Database = "edited-disconnected-target";
        Assert.Equal(connected, source.AssistantContextConnection);
        var second = workspace.NewTab(); await second.ConnectionReady;
        Assert.Equal(AssistantConnectionContextStore.Key(connected), AssistantConnectionContextStore.Key(second.AssistantContextConnection!));
        await source.DisconnectAsync();
        Assert.NotEqual(AssistantConnectionContextStore.Key(connected), AssistantConnectionContextStore.Key(source.AssistantContextConnection!));
        await source.StartDemoAsync(); Assert.Null(source.AssistantContextConnection);
    }

    [Fact]
    public async Task DisconnectedSourceOnlyCopiesSettingsAndLeavesPasswordBlank()
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
    public async Task ConnectedSourceOpensIndependentTabsWithLiveCredentialsAndNeverRunsTheirSql()
    {
        var sessions = new[] { new Session("first"), new Session("second"), new Session("third") };
        var queue = new Queue<Session>(sessions);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => queue.Dequeue());
        var first = workspace.SelectedTab!;
        Configure(first, "one"); first.Password = "session-secret";
        await first.ConnectAsync();
        Assert.Empty(first.Password);
        var second = workspace.NewTab("UPDATE dbo.Example SET Value = 1;", "draft.sql");
        await second.ConnectionReady;
        Assert.True(second.CanExecute);
        Assert.Empty(second.Password);
        Assert.Empty(second.Results);
        Assert.Equal("session-secret", sessions[1].OpenPassword);
        Assert.Equal(sessions[0].OpenProfile, sessions[1].OpenProfile);
        Assert.Equal(0, sessions[1].ExecutionCalls);
        Assert.True(await workspace.TryCloseTabAsync(first, _ => Task.FromResult(true)));
        Assert.True(sessions[0].Disposed);
        Assert.False(sessions[1].Disposed);
        var third = workspace.NewTab();
        await third.ConnectionReady;
        Assert.True(third.CanExecute);
        Assert.Equal("session-secret", sessions[2].OpenPassword);
        await second.DisconnectAsync();
        Assert.True(third.IsConnected);
        var disconnected = workspace.NewTab(connectionSource: second);
        await disconnected.ConnectionReady;
        Assert.False(disconnected.IsConnected);
        Assert.Empty(disconnected.Password);
    }

    [Fact]
    public async Task FailedInheritedLoginKeepsTheDocumentAndSourceSessionUsable()
    {
        var sourceSession = new Session("source");
        var failedSession = new Session("failure") { FailLogin = true };
        var sessions = new Queue<Session>([sourceSession, failedSession]);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => sessions.Dequeue());
        var source = workspace.SelectedTab!;
        Configure(source, "one"); await source.ConnectAsync();
        var child = workspace.NewTab("SELECT 123;", "example.sql");
        await child.ConnectionReady;
        Assert.False(child.IsConnected);
        Assert.True(child.CanEditConnection);
        Assert.Equal("Action failed", child.Status);
        Assert.Equal("SELECT 123;", child.Sql);
        Assert.True(failedSession.Disposed);
        Assert.True(source.CanExecute);
        Assert.False(sourceSession.Disposed);
        Assert.Equal(0, sourceSession.ExecutionCalls);
    }

    [Fact]
    public async Task RapidNewTabsUseTheSameTargetEvenWhileTheFirstInheritedLoginIsPending()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Session("pending") { LoginStarted = started, LoginRelease = release };
        var ready = new Session("ready");
        var sessions = new Queue<Session>([new("source"), pending, ready]);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => sessions.Dequeue());
        var source = workspace.SelectedTab!;
        Configure(source, "one"); source.Password = "session-secret"; await source.ConnectAsync();
        // The live connection target wins over any later form edits.
        source.Database = "edited-form-value";
        var first = workspace.NewTab();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var next = workspace.NewTab();
        await next.ConnectionReady;
        Assert.True(next.CanExecute);
        Assert.Equal("one", next.Database);
        Assert.Equal("one", ready.OpenProfile!.Database);
        Assert.Equal("session-secret", ready.OpenPassword);
        first.Cancel();
        await first.ConnectionReady;
        Assert.False(first.IsConnected);
        Assert.True(pending.Disposed);
        Assert.True(next.IsConnected);
        Assert.True(source.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingAnAutomaticallyConnectingTabCancelsItsLoginAndPreservesTheSource(bool ignoreCancellation)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceSession = new Session("source");
        var pending = new Session("pending") { LoginStarted = started, LoginRelease = release, IgnoreLoginCancellation = ignoreCancellation };
        var sessions = new Queue<Session>([sourceSession, pending]);
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () => sessions.Dequeue());
        var source = workspace.SelectedTab!;
        Configure(source, "one"); await source.ConnectAsync();
        var child = workspace.NewTab();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(child.IsBusy);
        var closing = workspace.TryCloseTabAsync(child, _ => Task.FromResult(true));
        if (ignoreCancellation) release.TrySetResult();
        Assert.True(await closing);
        Assert.True(pending.Disposed);
        Assert.False(child.IsConnected);
        Assert.True(source.CanExecute);
        Assert.Same(source, workspace.SelectedTab);
        Assert.False(sourceSession.Disposed);
    }

    [Fact]
    public async Task ExplicitOriginForOpenedFilesWinsOverTheSelectedTabsConnection()
    {
        var opened = new List<Session>();
        await using var workspace = new WorkspaceViewModel(new MemoryStore(), () =>
        {
            var session = new Session($"session{opened.Count}"); opened.Add(session); return session;
        });
        var source = workspace.SelectedTab!;
        Configure(source, "one");
        var other = workspace.NewTab();
        Configure(other, "two");
        await source.ConnectAsync(); await other.ConnectAsync();
        var firstFile = workspace.NewTab("SELECT 1;", "first.sql", source);
        var secondFile = workspace.NewTab("SELECT 2;", "second.sql", source);
        await Task.WhenAll(firstFile.ConnectionReady, secondFile.ConnectionReady);
        Assert.True(firstFile.CanExecute);
        Assert.True(secondFile.CanExecute);
        Assert.Equal("one", opened[2].OpenProfile!.Database);
        Assert.Equal("one", opened[3].OpenProfile!.Database);
        Assert.Equal("two", other.Database);
        Assert.All(opened, session => Assert.Equal(0, session.ExecutionCalls));
    }

    [Fact]
    public async Task DemoTabsInheritDemoConnectionWithoutRunningTheNewDocument()
    {
        await using var workspace = new WorkspaceViewModel(new MemoryStore());
        var source = workspace.SelectedTab!;
        await source.StartDemoAsync();
        var child = workspace.NewTab("SELECT 2;");
        await child.ConnectionReady;
        Assert.True(child.IsDemo);
        Assert.True(child.CanExecute);
        Assert.Empty(child.Results);
        Assert.Equal("SELECT 2;", child.Sql);
        await child.ExecuteAsync();
        Assert.NotEmpty(child.Results);
        Assert.NotEmpty(source.Results);
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
        await second.ConnectionReady;
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
        Assert.Contains("one", second.ConnectionLabel);
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
        public bool FailLogin { get; init; }
        public bool IgnoreLoginCancellation { get; init; }
        public TaskCompletionSource? LoginStarted { get; init; }
        public TaskCompletionSource? LoginRelease { get; init; }
        public string? OpenPassword { get; private set; }
        public ConnectionProfile? OpenProfile { get; private set; }
        public int ExecutionCalls { get; private set; }
        public int Transactions { get; init; }
        public bool IsConnected { get; private set; }
        public bool Disposed { get; private set; }
        public bool DisposedWhileExecuting { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken)
        {
            if (FailLogin) throw new InvalidOperationException("Login failed.");
            LoginStarted?.TrySetResult();
            if (LoginRelease is not null) await LoginRelease.Task.WaitAsync(IgnoreLoginCancellation ? CancellationToken.None : cancellationToken);
            OpenProfile = profile; OpenPassword = password; IsConnected = true;
        }
        public async Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds, IProgress<string>? messages, CancellationToken cancellationToken)
        {
            ExecutionCalls++; executing = true; Started.TrySetResult();
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
