using System.Text;
using System.Text.Json;

namespace Rowfinch.Infrastructure;

public sealed record AssistantTurn(string Text, IReadOnlyList<JsonElement> Output);

public static class AssistantResponseStream
{
    public static async Task<string> ReadAsync(Stream stream, IProgress<string>? progress, CancellationToken token)
        => (await ReadTurnAsync(stream, progress, token)).Text;

    public static async Task<AssistantTurn> ReadTurnAsync(Stream stream, IProgress<string>? progress, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        var state = new TurnReader(progress);
        string? eventName = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(token);
            token.ThrowIfCancellationRequested();
            if (line is not null && line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..].TrimStart());
                if (data.Length > 1_000_000) throw new InvalidOperationException("The assistant response was too large. Ask a narrower question.");
                continue;
            }
            if (line is not null && line.StartsWith("event:", StringComparison.Ordinal)) { eventName = line[6..].Trim(); continue; }
            if (line is { Length: > 0 }) continue;
            if (data.Length > 0)
            {
                var payload = data.ToString().Trim(); data.Clear();
                if (payload != "[DONE]" && state.Accept(payload, eventName) is { } turn) return turn;
            }
            eventName = null;
            if (line is null) break;
        }
        throw new InvalidOperationException("The assistant connection ended before completion. Any displayed response is partial.");
    }

    private sealed class TurnReader(IProgress<string>? progress)
    {
        private sealed class TextPart
        {
            public StringBuilder Deltas { get; } = new();
            public string? Final { get; set; }
            public int Length => Math.Max(Final?.Length ?? 0, Deltas.Length);
            public override string ToString() => Final ?? Deltas.ToString();
        }
        private readonly SortedDictionary<(int Item, int Part), TextPart> text = [];
        private readonly SortedDictionary<int, JsonElement> finished = [];
        private readonly StringBuilder unindexedDeltas = new();
        private int textEvents, argumentEvents;

        public AssistantTurn? Accept(string payload, string? eventName)
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Unsupported assistant streaming event format.");
            var type = String(root, "type") ?? eventName;
            switch (type)
            {
                case "response.output_text.delta":
                case "response.refusal.delta":
                    var delta = String(root, "delta") ?? "";
                    textEvents++;
                    if (root.TryGetProperty("output_index", out _)) Part(Index(root, "output_index"), Index(root, "content_index")).Deltas.Append(delta);
                    else unindexedDeltas.Append(delta);
                    CheckTextLimit(); progress?.Report(delta);
                    break;
                case "response.output_text.done":
                    SetText(Index(root, "output_index"), Index(root, "content_index"), String(root, "text"));
                    break;
                case "response.refusal.done":
                    SetText(Index(root, "output_index"), Index(root, "content_index"), String(root, "refusal"));
                    break;
                case "response.content_part.done":
                    if (root.TryGetProperty("part", out var part)) ReadPart(Index(root, "output_index"), Index(root, "content_index"), part);
                    break;
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                    {
                        finished[Index(root, "output_index")] = item.Clone(); CheckOutputLimit(finished.Values);
                    }
                    break;
                case "response.function_call_arguments.delta":
                case "response.function_call_arguments.done":
                    argumentEvents++;
                    break;
                case "response.completed":
                    return Complete(root);
                case "response.failed":
                case "response.incomplete":
                case "error":
                    var error = root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object && response.TryGetProperty("error", out var nested) ? nested : root;
                    throw new InvalidOperationException(ErrorMessage(String(error, "code")));
            }
            return null;
        }

        private AssistantTurn Complete(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object || String(response, "status") != "completed")
                throw new InvalidOperationException("The assistant response did not complete. Review any partial text before retrying.");
            var terminal = response.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [];
            CheckOutputLimit(terminal);
            var output = new SortedDictionary<int, JsonElement>(finished);
            for (var index = 0; index < terminal.Length; index++)
            {
                var item = terminal[index];
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Unsupported assistant output format.");
                if (output.TryGetValue(index, out var done))
                {
                    // Done events carry finalized content (including opaque reasoning). A
                    // terminal response may only summarize it; never execute conflicting calls.
                    foreach (var field in new[] { "id", "type", "call_id", "name", "namespace", "arguments" })
                        if (String(done, field) is { Length: > 0 } before && String(item, field) is { Length: > 0 } after && before != after)
                            throw new InvalidOperationException("The assistant returned inconsistent output items. No further database requests were executed.");
                    if (String(item, "type") == "message" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(part => !string.IsNullOrEmpty(String(part, "text")) || !string.IsNullOrEmpty(String(part, "refusal"))))
                        output[index] = item.Clone();
                }
                else output[index] = item.Clone();
            }
            CheckOutputLimit(output.Values);
            foreach (var (index, item) in output)
            {
                if (String(item, "type") == "function_call" && String(item, "status") is { } status && status != "completed")
                    throw new InvalidOperationException("The assistant database request did not complete. No further database requests were executed.");
                if (String(item, "type") != "message" || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                var partIndex = 0;
                foreach (var part in content.EnumerateArray()) ReadPart(index, partIndex++, part);
            }
            CheckTextLimit();
            var answer = text.Count == 0 ? unindexedDeltas.ToString() : string.Join("\n\n", text.GroupBy(pair => pair.Key.Item).Select(group => string.Concat(group.Select(pair => pair.Value.ToString()))));
            if (string.IsNullOrWhiteSpace(answer) && !output.Values.Any(item => String(item, "type") == "function_call"))
            {
                // Structural counters only: never expose questions, SQL, row values,
                // response identifiers or encrypted reasoning in error diagnostics.
                var known = new[] { "message", "function_call", "reasoning", "custom_tool_call", "compaction" };
                var kinds = string.Join(", ", output.Values.Select(item => String(item, "type") is { } kind && known.Contains(kind) ? kind : "other").Distinct());
                throw new InvalidOperationException($"ChatGPT completed without answer text or a database request. Stream summary: text events: {textEvents}, finalized items: {finished.Count}, terminal items: {terminal.Length}, function argument events: {argumentEvents}; output types: {(kinds.Length == 0 ? "none" : kinds)}. Retry the question; if it repeats, share this summary.");
            }
            return new(answer, output.Values.ToArray());
        }

        private void ReadPart(int item, int index, JsonElement part)
        {
            var kind = String(part, "type");
            if (kind == "output_text") SetText(item, index, String(part, "text"));
            else if (kind == "refusal") SetText(item, index, String(part, "refusal"));
        }
        private TextPart Part(int item, int index)
        {
            if (!text.TryGetValue((item, index), out var part)) text[(item, index)] = part = new();
            return part;
        }
        private void SetText(int item, int index, string? value)
        {
            if (value is null) return;
            Part(item, index).Final = value; CheckTextLimit();
        }
        private void CheckTextLimit()
        {
            if (unindexedDeltas.Length > 200_000 || text.Values.Sum(part => part.Length) > 200_000)
                throw new InvalidOperationException("The assistant response exceeded the preview limit. Ask a narrower question.");
        }
        private static void CheckOutputLimit(IEnumerable<JsonElement> output)
        {
            if (output.Count() > 256 || output.Sum(item => item.GetRawText().Length) > 1_000_000)
                throw new InvalidOperationException("The assistant output exceeded the context limit.");
        }
        private static int Index(JsonElement value, string property)
        {
            if (!value.TryGetProperty(property, out var index)) return 0;
            if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number) || number < 0 || number >= 256) throw new InvalidOperationException("Unsupported assistant output index.");
            return number;
        }
        private static string? String(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    }

    public static string ErrorMessage(string? code, int status = 0) => code switch
    {
        "subscription_sharing_usage_limit_exceeded" => "ChatGPT usage limit reached. Open Manage usage to review your plan and Rowfinch's limit.",
        "subscription_sharing_usage_unavailable" => "ChatGPT plan usage is unavailable for this account or workspace. Review Manage usage and try signing in again.",
        "context_length_exceeded" => "This chat is too long for the selected model. Choose New chat or share less context.",
        "invalid_grant" => "ChatGPT authorization expired or was revoked. Continue with ChatGPT again.",
        "access_denied" or "insufficient_scope" => "ChatGPT plan access was not granted. Sign in again and review the requested permissions.",
        _ when status == 401 => "ChatGPT sign-in expired or was revoked. Continue with ChatGPT again.",
        _ when status == 429 => "ChatGPT usage is temporarily limited. Open Manage usage or try again later.",
        _ when status == 403 => "Your ChatGPT account cannot use this request. Check plan access and the selected model.",
        _ => "ChatGPT could not complete this request. Try again, or reconnect and choose an available model."
    };
}
