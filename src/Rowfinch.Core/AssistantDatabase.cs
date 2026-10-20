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
    private sealed class ToolArgumentsException(string message) : InvalidOperationException(message);
    private bool queryDeclined;
    public const int MaximumCalls = 8;
    public const int RowLimit = 100;
    public const int TimeoutSeconds = 15;
    public const int MaximumOutputLength = 24_000;
    public bool AllowSchema { get; } = allowSchema;
    public bool AllowQueries => database.CanQuery;
    public string Target => database.Target;
    public object ConversationScope => database;
    public string? LastRequestIssue { get; private set; }

    public async Task<string> InvokeAsync(string name, string arguments, CancellationToken token)
    {
        try { return await InvokeCoreAsync(name, arguments, token); }
        catch (OperationCanceledException) { activity?.Invoke("Database request cancelled."); throw; }
        catch (AssistantSqlPolicyException error)
        {
            token.ThrowIfCancellationRequested();
            LastRequestIssue = "Query blocked by SQL policy. No SQL was executed: " + error.Message;
            activity?.Invoke("Query blocked by SQL policy. No SQL was executed: " + error.Message);
            return JsonSerializer.Serialize(new { status = "rejected", code = "sql_policy_rejected", executed = false,
                message = error.Message.Length <= 2000 ? error.Message : error.Message[..2000] });
        }
        catch (ToolArgumentsException error)
        {
            token.ThrowIfCancellationRequested();
            LastRequestIssue = error.Message;
            activity?.Invoke(error.Message);
            return JsonSerializer.Serialize(new { status = "rejected", code = "invalid_tool_arguments", executed = false,
                message = error.Message });
        }
        catch (Exception error)
        {
            var message = OperationName(name) + " failed: " + error.Message;
            LastRequestIssue = message;
            activity?.Invoke(message);
            throw new InvalidOperationException(message, error);
        }
    }

    private async Task<string> InvokeCoreAsync(string name, string arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (name is not ("list_objects" or "describe_object" or "query"))
            throw new InvalidOperationException("The assistant requested an unknown tool. Ask it to use the available database tools.");
        if (name is "list_objects" or "describe_object" && !AllowSchema)
            throw new InvalidOperationException("Schema inspection is off. Enable it in Database access before requesting table or column metadata.");
        if (name == "query" && !AllowQueries)
            throw new InvalidOperationException("Read-only queries are off. Enable query access with a dedicated read-only connection in Database access.");
        if (name == "query" && queryDeclined)
            return "{\"status\":\"declined\",\"message\":\"Further queries are disabled for this question after the user declined.\"}";
        ToolArgumentsException Invalid(string detail) => new(OperationName(name) + " was not run: " + detail +
            " No database request was executed. The assistant can correct the request and try again.");
        if (arguments.Length > 20_000) throw Invalid($"the request contains {arguments.Length:N0} characters; the limit is 20,000. Split it into smaller requests.");
        JsonDocument ParseArguments()
        {
            try { return JsonDocument.Parse(arguments); }
            catch (JsonException error)
            {
                throw Invalid($"the assistant sent malformed JSON arguments (line {error.LineNumber + 1}, byte {error.BytePositionInLine + 1}). A JSON object with the required text fields is expected.");
            }
        }
        using var json = ParseArguments();
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("the assistant sent " + Kind(json.RootElement.ValueKind) + " instead of an object containing the required text fields.");
        var root = json.RootElement;
        var fields = name switch { "list_objects" => new[] { "filter" }, "describe_object" => ["schema", "name"], _ => ["sql", "reason"] };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal)) throw Invalid("the request contains unsupported fields. Use only " + string.Join(", ", fields) + ".");
            if (!seen.Add(property.Name)) throw Invalid($"the '{property.Name}' field appears more than once. Supply it once.");
        }
        string Read(string property, int limit)
        {
            var guidance = property switch
            {
                "filter" => "Use a literal object-name filter, or an empty string to list all visible objects.",
                "schema" or "name" => "Use the exact schema and object name from list_objects.",
                "sql" => "Use one read-only SELECT or CTE; split long queries into smaller requests.",
                _ => "Provide a short explanation of the query's purpose for approval."
            };
            if (!root.TryGetProperty(property, out var value)) throw Invalid($"the required '{property}' field is missing. " + guidance);
            if (value.ValueKind != JsonValueKind.String) throw Invalid($"'{property}' must be text; received " + Kind(value.ValueKind) + ". " + guidance);
            var text = value.GetString()!;
            if (text.Length > limit) throw Invalid($"'{property}' contains {text.Length:N0} characters; the limit is {limit:N0}. " + guidance);
            if (property != "filter" && string.IsNullOrWhiteSpace(text)) throw Invalid($"'{property}' is empty. " + guidance);
            return text;
        }
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

    private static string OperationName(string name) => name switch
    {
        "list_objects" => "Listing database objects (list_objects)",
        "describe_object" => "Reading column metadata (describe_object)",
        "query" => "Read-only query (query)",
        _ => "Database request"
    };

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "an array", JsonValueKind.Object => "an object", JsonValueKind.Null => "null",
        JsonValueKind.Number => "a number", JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.String => "text", _ => "an unsupported value"
    };
}
