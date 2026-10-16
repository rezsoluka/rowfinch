using System.Text.Json;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

/// <summary>Owns an independent connection. Never wraps a tab's live SQL session.</summary>
public sealed class AssistantDatabaseSession(IQuerySession session, string target) : IAssistantDatabaseSession
{
    public string Target { get; } = target;
    public bool CanQuery { get; private set; }
    public async Task EnableQueriesAsync(CancellationToken token)
    {
        if (session is not IAssistantReadOnlySession readOnly) throw new InvalidOperationException("This connection does not support assistant read-only queries.");
        await readOnly.ValidateReadOnlyAccessAsync(token);
        CanQuery = true;
    }
    public async Task<string> ListObjectsAsync(string filter, CancellationToken token)
    {
        var objects = (await session.GetObjectsAsync(token)).Where(item => item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        return BoundedJson(new { objects = objects.Take(100).Select(item => new { schema = item.Schema, name = item.Name, kind = item.Kind }), truncated = objects.Length > 100, note = "Only metadata visible to this database identity is listed. Use filter to narrow results." });
    }
    public async Task<string> DescribeObjectAsync(string schema, string name, CancellationToken token)
    {
        var item = (await session.GetObjectsAsync(token)).SingleOrDefault(item => item.Schema == schema && item.Name == name);
        if (item is null || !item.HasColumns) return "{\"status\":\"unavailable\",\"message\":\"Choose a visible table or view from list_objects.\"}";
        if (session is not IObjectMetadataSession metadata) throw new InvalidOperationException("Column metadata is unavailable for this connection.");
        var columns = await metadata.GetColumnsAsync(item, token);
        return BoundedJson(new { schema, name, kind = item.Kind, columns = columns.Where(c => !c.IsHidden).Take(100).Select(c => new { name = c.Name, type = c.TypeName, nullable = c.IsNullable, primary_key_ordinal = c.PrimaryKeyOrdinal, identity = c.IsIdentity, computed = c.IsComputed }), truncated = columns.Count(c => !c.IsHidden) > 100 });
    }
    public async Task<string> QueryAsync(string sql, CancellationToken token)
    {
        if (!CanQuery || session is not IAssistantReadOnlySession readOnly) throw new InvalidOperationException("Assistant query access is not enabled.");
        var result = await readOnly.ExecuteReadOnlyAsync(sql, token);
        return BoundedJson(new { status = "success", columns = result.Columns.Select(c => new { name = c.Name, type = c.TypeName }), rows = result.Rows, truncated = result.IsTruncated,
            limits = "100 rows, 100 columns, 15 seconds, 2000 text characters / 1500 binary bytes per cell, bounded total output. Truncation may include cells or rows." });
    }
    private static string BoundedJson(object value)
    {
        var result = JsonSerializer.Serialize(value);
        return result.Length <= AssistantDatabaseTools.MaximumOutputLength ? result : "{\"status\":\"too_large\",\"message\":\"Metadata or result exceeds the output limit. Narrow the filter or select fewer columns.\"}";
    }
    public ValueTask DisposeAsync() { CanQuery = false; return session.DisposeAsync(); }
}
