using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

public sealed partial class SqlQuerySession : IQuerySession, IObjectMetadataSession, IAssistantReadOnlySession
{
    private SqlConnection? connection;
    public bool IsConnected => connection?.State == ConnectionState.Open;

    public static SqlConnectionStringBuilder BuildConnectionString(ConnectionProfile profile, string? password)
    {
        profile.Validate();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server.Trim(), InitialCatalog = profile.Database.Trim(),
            ApplicationName = "Rowfinch", ConnectTimeout = 20,
            Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = false,
            ConnectRetryCount = 0, PersistSecurityInfo = false, Pooling = false
        };
        if (profile.Authentication == AuthenticationKind.EntraInteractive)
        {
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
            if (!string.IsNullOrWhiteSpace(profile.UserName)) builder.UserID = profile.UserName.Trim();
        }
        else
        {
            builder.UserID = profile.UserName.Trim();
            builder.Password = password ?? "";
        }
        return builder;
    }

    public async Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken)
    {
        await DisposeAsync();
        var candidate = new SqlConnection(BuildConnectionString(profile, password).ConnectionString);
        try
        {
            await candidate.OpenAsync(cancellationToken);
            connection = candidate;
        }
        catch { await candidate.DisposeAsync(); throw; }
    }

    public async Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds,
        IProgress<string>? messages, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);
        var current = GetConnection();
        var batches = SqlBatchParser.Split(sql); // Validate before executing any batch.
        var results = new List<QueryResultSet>();
        var stopwatch = Stopwatch.StartNew();
        var remainingRows = rowLimit;
        SqlInfoMessageEventHandler handler = (_, args) => messages?.Report(args.Message);
        current.InfoMessage += handler;
        try
        {
            foreach (var batch in batches)
            for (var repetition = 0; repetition < batch.RepeatCount; repetition++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = current.CreateCommand();
                command.CommandText = batch.Text;
                command.CommandTimeout = timeoutSeconds;
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    do
                    {
                        if (reader.FieldCount == 0) continue;
                        if (results.Count >= 100) throw new InvalidOperationException("The query returned more than 100 result sets. Narrow the script and run it again.");
                        var columns = ResultMetadata.ReadColumns(reader);
                        var rows = new List<object?[]>();
                        var truncated = false;
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            if (remainingRows == 0) { truncated = true; continue; }
                            var row = new object[reader.FieldCount];
                            reader.GetValues(row);
                            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
                            remainingRows--;
                        }
                        results.Add(new QueryResultSet($"Result {results.Count + 1}", columns, rows, truncated));
                        if (truncated) messages?.Report($"Display limit reached: {rowLimit:N0} rows across all results. Use TOP or filters to reduce data transferred.");
                    } while (await reader.NextResultAsync(cancellationToken));
                    if (reader.RecordsAffected >= 0) messages?.Report($"{reader.RecordsAffected:N0} row(s) affected.");
                }
                catch (SqlException error) when (!cancellationToken.IsCancellationRequested)
                {
                    foreach (SqlError detail in error.Errors)
                        messages?.Report($"SQL {detail.Number}, line {batch.StartLine + Math.Max(0, detail.LineNumber - 1)}: {detail.Message}");
                    throw;
                }
            }
            var transactions = await GetTransactionCountAsync(cancellationToken);
            return new QueryExecution(results, transactions, stopwatch.Elapsed);
        }
        finally { current.InfoMessage -= handler; }
    }

    public async Task<int> GetTransactionCountAsync(CancellationToken cancellationToken)
    {
        await using var command = GetConnection().CreateCommand();
        command.CommandText = "SELECT @@TRANCOUNT;";
        command.CommandTimeout = 5;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken cancellationToken)
    {
        await using var command = GetConnection().CreateCommand();
        command.CommandText = """
            SELECT s.name, o.name,
                   CASE WHEN o.type = 'U' THEN 'Table' WHEN o.type = 'V' THEN 'View'
                        WHEN o.type IN ('P', 'PC') THEN 'Procedure' ELSE 'Function' END, o.object_id
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;
        command.CommandTimeout = 15;
        var objects = new List<DatabaseObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            objects.Add(new DatabaseObject(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return objects;
    }

    public async Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(DatabaseObject item, CancellationToken cancellationToken)
    {
        if (!item.HasColumns) throw new ArgumentException("Only tables and views have browsable columns.");
        await using var command = CreateMetadataCommand(item, """
            SELECT c.column_id, c.name, t.name, c.max_length, c.precision, c.scale,
                   c.is_nullable, COALESCE(pk.key_ordinal, 0), c.is_identity, c.is_computed,
                   c.is_hidden, t.is_user_defined, ts.name, c.generated_always_type, c.system_type_id
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            JOIN sys.columns AS c ON c.object_id = o.object_id
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            JOIN sys.schemas AS ts ON ts.schema_id = t.schema_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id, ic.key_ordinal
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1 AND ic.key_ordinal > 0
            ) AS pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE o.object_id = @ObjectId AND s.name = @Schema AND o.name = @Name
            ORDER BY c.column_id;
            """);
        var columns = new List<DatabaseColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetBoolean(11)
                ? $"{ObjectSql.Quote(reader.GetString(12))}.{ObjectSql.Quote(reader.GetString(2))}"
                : ObjectSql.FormatType(reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5));
            columns.Add(new(reader.GetInt32(0), reader.GetString(1), type, reader.GetBoolean(6),
                reader.GetInt32(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
                reader.GetByte(13) != 0, reader.GetByte(14) == 189));
        }
        if (columns.Count == 0) throw new InvalidOperationException("Columns are unavailable. Refresh the object list; the object may have changed or metadata permission may be missing.");
        return columns;
    }

    public async Task<string> GetDefinitionAsync(DatabaseObject item, CancellationToken cancellationToken)
    {
        if (!item.HasDefinition) throw new ArgumentException("This object has no SQL module definition.");
        await using var command = CreateMetadataCommand(item, """
            SELECT m.definition
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
            WHERE o.object_id = @ObjectId AND s.name = @Schema AND o.name = @Name;
            """);
        return await command.ExecuteScalarAsync(cancellationToken) is string { Length: > 0 } definition
            ? definition : throw new InvalidOperationException("Definition unavailable. The object may have changed, be encrypted, or require VIEW DEFINITION permission. CLR objects have no SQL definition.");
    }

    private SqlCommand CreateMetadataCommand(DatabaseObject item, string sql)
    {
        var command = GetConnection().CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        command.Parameters.Add("@ObjectId", SqlDbType.Int).Value = item.ObjectId;
        command.Parameters.Add("@Schema", SqlDbType.NVarChar, 128).Value = item.Schema;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 128).Value = item.Name;
        return command;
    }

    private SqlConnection GetConnection() => IsConnected ? connection! : throw new InvalidOperationException("Connect to a database first. A lost connection must be reopened explicitly.");

    public async ValueTask DisposeAsync()
    {
        if (connection is { } current)
        {
            connection = null;
            // Physical session closure rolls back outstanding transactions. Pooling is disabled.
            await current.DisposeAsync();
        }
    }
}
