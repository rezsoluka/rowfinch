using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rowfinch.Infrastructure;

public sealed record ClaudeTurn(string Text, JsonElement[] Content, string StopReason);

/// <summary>Accumulates complete Claude blocks. Incomplete or conflicting tool input is never executed.</summary>
public static class ClaudeResponseStream
{
    public static async Task<ClaudeTurn> ReadAsync(Stream stream, IProgress<string>? progress, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var blocks = new List<JsonObject>();
        var data = new StringBuilder();
        var text = new StringBuilder();
        var arguments = new StringBuilder();
        bool started = false, open = false;
        string? reason = null;
        var total = 0;
        await foreach (var line in Lines(reader, token))
        {
            total += line.Length;
            if (total > 2_000_000) throw Error("The Claude response exceeded the context limit.");
            if (line.StartsWith("data:", StringComparison.Ordinal)) { data.AppendLine(line[5..].TrimStart()); continue; }
            if (line.Length != 0 || data.Length == 0) continue;
            using var json = JsonDocument.Parse(data.ToString()); data.Clear();
            var root = json.RootElement;
            var type = String(root, "type");
            if (type == "error") throw Error("Claude could not complete the response. Check API credits and model access, then try again.");
            if (type == "ping") continue;
            if (type == "message_start")
            {
                if (started || String(root.GetProperty("message"), "role") != "assistant") throw Error();
                started = true; continue;
            }
            if (!started) throw Error();
            if (type == "content_block_start")
            {
                if (open || blocks.Count >= 256 || root.GetProperty("index").GetInt32() != blocks.Count || reason is not null) throw Error();
                var block = root.GetProperty("content_block");
                // Keep only fields accepted by the Messages input schema, never response metadata.
                var result = new JsonObject { ["type"] = String(block, "type") };
                switch (String(block, "type"))
                {
                    case "text": result["text"] = String(block, "text") ?? ""; break;
                    case "tool_use":
                        result["id"] = Required(block, "id"); result["name"] = Required(block, "name");
                        result["input"] = JsonNode.Parse(block.GetProperty("input").GetRawText()); arguments.Clear(); break;
                    case "thinking": result["thinking"] = String(block, "thinking") ?? ""; result["signature"] = String(block, "signature") ?? ""; break;
                    case "redacted_thinking": result["data"] = Required(block, "data"); break;
                    default: throw Error("Claude returned an unsupported content type. No further database requests were executed.");
                }
                blocks.Add(result); open = true;
                if (String(block, "type") == "text") { var initial = String(block, "text") ?? ""; text.Append(initial); progress?.Report(initial); }
            }
            else if (type == "content_block_delta")
            {
                if (!open || root.GetProperty("index").GetInt32() != blocks.Count - 1) throw Error();
                var block = blocks[^1]; var delta = root.GetProperty("delta");
                var blockType = block["type"]!.GetValue<string>();
                var deltaType = String(delta, "type");
                if (deltaType == "text_delta" && blockType == "text")
                {
                    var value = Required(delta, "text"); block["text"] = block["text"]!.GetValue<string>() + value;
                    text.Append(value); progress?.Report(value);
                }
                else if (deltaType == "input_json_delta" && blockType == "tool_use") arguments.Append(Required(delta, "partial_json"));
                else if (deltaType == "thinking_delta" && blockType == "thinking") block["thinking"] = block["thinking"]!.GetValue<string>() + Required(delta, "thinking");
                else if (deltaType == "signature_delta" && blockType == "thinking") block["signature"] = block["signature"]!.GetValue<string>() + Required(delta, "signature");
                else throw Error();
                if (text.Length > 200_000 || arguments.Length > 20_000) throw Error("The Claude response exceeded the preview limit. Ask a narrower question.");
            }
            else if (type == "content_block_stop")
            {
                if (!open || root.GetProperty("index").GetInt32() != blocks.Count - 1) throw Error();
                var block = blocks[^1];
                if (block["type"]!.GetValue<string>() == "tool_use")
                {
                    if (arguments.Length > 0) block["input"] = JsonNode.Parse(arguments.ToString());
                    if (block["input"] is not JsonObject) throw Error();
                }
                open = false;
            }
            else if (type == "message_delta")
            {
                if (open || reason is not null) throw Error();
                reason = String(root.GetProperty("delta"), "stop_reason");
            }
            else if (type == "message_stop")
            {
                if (open || reason is not ("end_turn" or "tool_use" or "stop_sequence" or "refusal"))
                    throw Error("Claude did not finish this answer (possibly the output limit). Any displayed text is partial; ask a narrower question.");
                var output = blocks.Select(block => JsonSerializer.SerializeToElement(block)).ToArray();
                var hasTools = output.Any(block => String(block, "type") == "tool_use");
                if ((reason == "tool_use") != hasTools || (!hasTools && string.IsNullOrWhiteSpace(text.ToString()))) throw Error();
                return new(text.ToString(), output, reason);
            }
            // Unknown event types may be added by Anthropic; known block/delta types remain strict.
        }
        throw Error("The Claude connection ended before completion. Any displayed answer is partial.");
    }

    private static async IAsyncEnumerable<string> Lines(StreamReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new char[4096]; var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == '\n') { yield return line.ToString().TrimEnd('\r'); line.Clear(); }
                else { line.Append(buffer[index]); if (line.Length > 1_000_000) throw Error("The Claude streaming event exceeded the size limit."); }
            }
        if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
        yield return "";
    }
    internal static string? String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement item, string name) => String(item, name) ?? throw Error();
    private static InvalidOperationException Error(string? text = null) => new(text ?? "Claude returned an incomplete or inconsistent response. No further database requests were executed.");
}
