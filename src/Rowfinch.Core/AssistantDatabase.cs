using System.Text.Json;

namespace Rowfinch.Core;

public interface IAssistantReadOnlySession
{
    Task ValidateReadOnlyAccessAsync(CancellationToken token);
    Task<QueryResultSet> ExecuteReadOnlyAsync(string sql, CancellationToken token);
}

public interface IAssistantDatabaseSession : IAsyncDisposable
{
    string Target { get; }
    bool CanQuery { get; }
    Task<string> ListObjectsAsync(string filter, CancellationToken token);
    Task<string> DescribeObjectAsync(string schema, string name, CancellationToken token);
    Task<string> QueryAsync(string sql, CancellationToken token);
}

/// <summary>A local SQL grammar rejection, before any database execution.</summary>
public sealed class AssistantSqlPolicyException(string message) : InvalidOperationException(message);

public sealed record AssistantQueryApproval(string Target, string Sql, string Reason);

/// <summary>All model requests pass through this capability and approval boundary.</summary>
public sealed class AssistantDatabaseTools(IAssistantDatabaseSession database, bool allowSchema,
    Func<AssistantQueryApproval, CancellationToken, Task<bool>> approve,
    Action<string>? activity = null)
{
    private bool queryDeclined;
    public const int MaximumCalls = 8;
    public const int RowLimit = 100;
    public const int TimeoutSeconds = 15;
    public const int MaximumOutputLength = 24_000;
    public bool AllowSchema { get; } = allowSchema;
    public bool AllowQueries => database.CanQuery;
    public string Target => database.Target;
    public object ConversationScope => database;

    public async Task<string> InvokeAsync(string name, string arguments, CancellationToken token)
    {
        try { return await InvokeCoreAsync(name, arguments, token); }
        catch (OperationCanceledException) { activity?.Invoke("Database request cancelled."); throw; }
        catch (AssistantSqlPolicyException error)
        {
            token.ThrowIfCancellationRequested();
            activity?.Invoke("Query blocked by SQL policy. No SQL was executed: " + error.Message);
            return JsonSerializer.Serialize(new { status = "rejected", code = "sql_policy_rejected", executed = false,
                message = error.Message.Length <= 2000 ? error.Message : error.Message[..2000] });
        }
        catch (Exception error) { activity?.Invoke("Database request failed: " + error.Message); throw; }
    }

    private async Task<string> InvokeCoreAsync(string name, string arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (arguments.Length > 20_000) throw new InvalidOperationException("The assistant database request was too large.");
        using var json = JsonDocument.Parse(arguments);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid database tool arguments.");
        var root = json.RootElement;
        string Read(string property, int limit) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= limit
            ? text : throw new InvalidOperationException("Invalid database tool arguments.");
        string result;
        switch (name)
        {
            case "list_objects" when AllowSchema:
                var filter = Read("filter", 128);
                activity?.Invoke("Schema: list objects · " + filter);
                result = await database.ListObjectsAsync(filter, token);
                break;
            case "describe_object" when AllowSchema:
                var schema = Read("schema", 128); var objectName = Read("name", 128);
                activity?.Invoke("Schema: columns for " + ObjectSql.Quote(schema) + "." + ObjectSql.Quote(objectName));
                result = await database.DescribeObjectAsync(schema, objectName, token);
                break;
            case "query" when AllowQueries:
                if (queryDeclined) return "{\"status\":\"declined\",\"message\":\"Further queries are disabled for this question after the user declined.\"}";
                var sql = Read("sql", 12_000); var reason = Read("reason", 1000);
                activity?.Invoke("Query requested · " + database.Target + "\n" + sql);
                if (!await approve(new(database.Target, sql, reason), token))
                {
                    queryDeclined = true;
                    activity?.Invoke("Query declined. No SQL was executed or rows shared.");
                    return "{\"status\":\"declined\",\"message\":\"The user declined this query. Do not retry it or an equivalent query in this question.\"}";
                }
                token.ThrowIfCancellationRequested();
                result = await database.QueryAsync(sql, token);
                break;
            default: throw new InvalidOperationException("The assistant requested a database capability that is not enabled.");
        }
        token.ThrowIfCancellationRequested();
        if (result.Length > MaximumOutputLength) throw new InvalidOperationException("The database response was too large. Narrow the request.");
        activity?.Invoke("Shared database response (untrusted data):\n" + result);
        return result;
    }
}
