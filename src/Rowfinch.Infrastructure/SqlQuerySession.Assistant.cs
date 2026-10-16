using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

public sealed partial class SqlQuerySession
{
    // Effective permissions include role/group/ownership grants and column-level grants.
    // Unknown/new permissions fail closed. This is an additional check, never a replacement
    // for an administrator provisioning a dedicated SELECT-only database identity.
    public const string AssistantPermissionCheck = """
        SELECT TOP (1) permission_name
        FROM sys.fn_my_permissions(NULL, 'DATABASE')
        WHERE permission_name IS NULL OR permission_name NOT IN ('CONNECT', 'SELECT', 'VIEW DEFINITION', 'VIEW DATABASE STATE',
          'VIEW DATABASE PERFORMANCE STATE', 'VIEW DATABASE SECURITY STATE', 'SHOWPLAN', 'UNMASK',
          'VIEW SECURITY DEFINITION', 'VIEW PERFORMANCE DEFINITION', 'VIEW CHANGE TRACKING',
          'VIEW ANY COLUMN MASTER KEY DEFINITION', 'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION')
        UNION ALL
        SELECT TOP (1) p.permission_name
        FROM sys.schemas AS s
        CROSS APPLY sys.fn_my_permissions(QUOTENAME(s.name), 'SCHEMA') AS p
        WHERE p.permission_name IS NULL OR p.permission_name NOT IN ('SELECT', 'VIEW DEFINITION', 'VIEW SECURITY DEFINITION',
          'VIEW PERFORMANCE DEFINITION', 'VIEW CHANGE TRACKING', 'UNMASK')
        UNION ALL
        SELECT TOP (1) p.permission_name
        FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        CROSS APPLY sys.fn_my_permissions(QUOTENAME(s.name) + '.' + QUOTENAME(o.name), 'OBJECT') AS p
        WHERE o.is_ms_shipped = 0 AND (p.permission_name IS NULL OR p.permission_name NOT IN ('SELECT', 'VIEW DEFINITION',
          'VIEW SECURITY DEFINITION', 'VIEW PERFORMANCE DEFINITION', 'VIEW CHANGE TRACKING', 'UNMASK'));
        """;

    public async Task ValidateReadOnlyAccessAsync(CancellationToken token)
    {
        var current = GetConnection();
        await using var command = current.CreateCommand();
        command.CommandTimeout = AssistantDatabaseTools.TimeoutSeconds;
        command.CommandText = "SELECT USER_NAME(), CONVERT(int, SERVERPROPERTY('EngineEdition')), HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONNECT');";
        int engine;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.GetString(0).Equals("dbo", StringComparison.OrdinalIgnoreCase) ||
                reader.IsDBNull(2) || reader.GetInt32(2) != 1) DenyAssistantAccess();
            engine = reader.GetInt32(1);
        }
        command.CommandText = AssistantPermissionCheck;
        if (await command.ExecuteScalarAsync(token) is not null) DenyAssistantAccess();
        // Azure SQL Database has no instance-level SERVER permission surface here.
        if (engine != 5)
        {
            command.CommandText = """
                SELECT TOP (1) permission_name FROM sys.fn_my_permissions(NULL, 'SERVER')
                WHERE permission_name IS NULL OR permission_name NOT IN ('CONNECT SQL', 'VIEW ANY DATABASE', 'VIEW ANY DEFINITION',
                  'VIEW SERVER STATE', 'VIEW SERVER PERFORMANCE STATE', 'VIEW SERVER SECURITY STATE',
                  'VIEW ANY SECURITY DEFINITION', 'VIEW ANY PERFORMANCE DEFINITION', 'SELECT ALL USER SECURABLES');
                """;
            if (await command.ExecuteScalarAsync(token) is not null) DenyAssistantAccess();
        }
    }

    private static void DenyAssistantAccess() => throw new InvalidOperationException(
        "Assistant queries require a dedicated read-only database identity. This account has permissions outside the allowed read-only set (for example elevated, write or execute permissions). Connect another query tab using a SELECT-only account, then choose that connection in Database access. Schema-only access can use your current account.");

    public async Task<QueryResultSet> ExecuteReadOnlyAsync(string sql, CancellationToken token)
    {
        ReadOnlySqlPolicy.Validate(sql);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(AssistantDatabaseTools.TimeoutSeconds));
        try { return await ExecuteReadOnlyCoreAsync(sql, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("The assistant query exceeded its 15 second timeout. Narrow the query and try again."); }
        finally
        {
            // ROWCOUNT is connection state, so it must not cap subsequent metadata reads.
            // A session whose limits cannot be reset is closed rather than reused.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (IsConnected)
                {
                    await using var reset = GetConnection().CreateCommand();
                    reset.CommandText = "SET ROWCOUNT 0;"; reset.CommandTimeout = 2;
                    await reset.ExecuteNonQueryAsync(cleanup.Token);
                }
            }
            catch (Exception) { await DisposeAsync(); }
        }
    }

    private async Task<QueryResultSet> ExecuteReadOnlyCoreAsync(string sql, CancellationToken queryToken)
    {
        await ValidateReadOnlyAccessAsync(queryToken); // Recheck after approval, before every query.
        await using var command = GetConnection().CreateCommand();
        command.CommandTimeout = AssistantDatabaseTools.TimeoutSeconds;
        command.CommandText = $"SET ROWCOUNT {AssistantDatabaseTools.RowLimit + 1}; SET LOCK_TIMEOUT 5000;";
        await command.ExecuteNonQueryAsync(queryToken);
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, queryToken);
        if (reader.FieldCount > 100) throw new InvalidOperationException("Assistant queries support up to 100 columns. Select fewer columns.");
        var columns = ResultMetadata.ReadColumns(reader);
        var rows = new List<object?[]>();
        var truncated = false;
        var length = 0;
        while (await reader.ReadAsync(queryToken))
        {
            if (rows.Count >= AssistantDatabaseTools.RowLimit) { truncated = true; break; }
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                if (await reader.IsDBNullAsync(i, queryToken)) continue;
                if (reader.GetFieldType(i) == typeof(string))
                {
                    using var text = reader.GetTextReader(i);
                    var buffer = new char[2001];
                    var count = await text.ReadBlockAsync(buffer.AsMemory(), queryToken);
                    row[i] = new string(buffer, 0, Math.Min(count, 2000)) + (count > 2000 ? " [truncated]" : "");
                    truncated |= count > 2000;
                }
                else if (reader.GetFieldType(i) == typeof(byte[]))
                {
                    using var binary = reader.GetStream(i);
                    var buffer = new byte[1501];
                    var count = await binary.ReadAtLeastAsync(buffer, 1501, throwOnEndOfStream: false, queryToken);
                    row[i] = Convert.ToBase64String(buffer, 0, Math.Min(count, 1500)) + (count > 1500 ? " [truncated Base64]" : "");
                    truncated |= count > 1500;
                }
                else row[i] = reader.GetValue(i);
            }
            var rowLength = JsonSerializer.Serialize(row).Length;
            if (length + rowLength > 16_000) { truncated = true; break; }
            length += rowLength; rows.Add(row);
        }
        if (truncated) command.Cancel(); // Avoid draining unused rows/LOB values on reader disposal.
        return new("Assistant read-only result", columns, rows, truncated);
    }
}
