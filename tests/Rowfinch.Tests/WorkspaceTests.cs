using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Xunit;

namespace Rowfinch.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task CancellationKeepsConnectionAndRefreshesOpenTransactionState()
    {
        var session = new TestSession { WaitForCancellation = true, OpenTransactions = 1 };
        await using var workspace = CreateWorkspace(session);
        await workspace.ConnectAsync();
        var execution = workspace.ExecuteAsync("WAITFOR DELAY '00:01:00';");
        await session.Started.Task;
        workspace.Cancel();
        await execution;
        Assert.False(workspace.IsBusy);
        Assert.True(workspace.IsConnected);
        Assert.Equal(1, workspace.TransactionCount);
        Assert.Equal("Cancelled", workspace.Status);
        Assert.True(workspace.RollbackCommand.CanExecute(null));
    }

    [Fact]
    public async Task InvalidLaterBatchPreventsAllExecution()
    {
        var session = new TestSession();
        await using var workspace = CreateWorkspace(session);
        await workspace.ConnectAsync();
        await workspace.ExecuteAsync("DELETE FROM dbo.Customers;\nGO\n:r external.sql");
        Assert.Equal(0, session.ExecutionCalls);
        Assert.Equal("Action failed", workspace.Status);
    }

    [Fact]
    public async Task ClosingWaitsForCancelledExecutionBeforeDisposingConnection()
    {
        var session = new TestSession { WaitForCancellation = true };
        var workspace = CreateWorkspace(session);
        await workspace.ConnectAsync();
        var execution = workspace.ExecuteAsync("SELECT 1;");
        await session.Started.Task;
        await workspace.DisposeAsync();
        await execution;
        Assert.False(session.DisposedWhileExecuting);
        Assert.False(workspace.IsConnected);
    }

    [Fact]
    public async Task FailedQueryDoesNotPretendTransactionWasRolledBack()
    {
        var session = new TestSession { FailExecution = true, OpenTransactions = 2 };
        await using var workspace = CreateWorkspace(session);
        await workspace.ConnectAsync();
        await workspace.ExecuteAsync("SELECT broken;");
        Assert.Equal(2, workspace.TransactionCount);
        Assert.Equal("Action failed", workspace.Status);
        Assert.Contains("Query failed", workspace.Messages);
    }

    private static QueryTabViewModel CreateWorkspace(TestSession session) => new(new MemoryProfileStore(), () => session)
        { Server = "test.database.windows.net", Database = "test", UserName = "test" };

    private sealed class MemoryProfileStore : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestSession : IQuerySession
    {
        public bool IsConnected { get; private set; }
        public bool WaitForCancellation { get; init; }
        public bool FailExecution { get; init; }
        public int OpenTransactions { get; init; }
        public int ExecutionCalls { get; private set; }
        private bool executing;
        public bool DisposedWhileExecuting { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken) { IsConnected = true; return Task.CompletedTask; }
        public async Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds, IProgress<string>? messages, CancellationToken cancellationToken)
        {
            ExecutionCalls++; executing = true; Started.TrySetResult();
            try
            {
                if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (FailExecution) throw new InvalidOperationException("Query failed");
                return new QueryExecution([], OpenTransactions, TimeSpan.Zero);
            }
            finally { executing = false; }
        }
        public Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DatabaseObject>>([]);
        public Task<int> GetTransactionCountAsync(CancellationToken cancellationToken) => Task.FromResult(OpenTransactions);
        public ValueTask DisposeAsync() { DisposedWhileExecuting = executing; IsConnected = false; return ValueTask.CompletedTask; }
    }
}
