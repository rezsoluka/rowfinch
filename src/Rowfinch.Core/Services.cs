namespace Rowfinch.Core;

public interface IQuerySession : IAsyncDisposable
{
    bool IsConnected { get; }
    Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken);
    Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds,
        IProgress<string>? messages, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken cancellationToken);
    Task<int> GetTransactionCountAsync(CancellationToken cancellationToken);
}

public interface IObjectMetadataSession
{
    Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(DatabaseObject item, CancellationToken cancellationToken);
    Task<string> GetDefinitionAsync(DatabaseObject item, CancellationToken cancellationToken);
}

public interface IProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default);
}
