using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rowfinch.Core;

public sealed record SavedChatMessage(string Question, string Answer);

/// <summary>Explicitly saved data, never credentials or a grant of database access.</summary>
public sealed record SavedAssistantChat(int Version, DateTimeOffset SavedAt, string AccountId,
    string? DatabaseTarget, string? ModelId, string Draft, SavedChatMessage[] Messages, JsonElement[] History, string Provider = "chatgpt")
{
    public const int CurrentVersion = 2;
    public const int MaximumFileBytes = 16_000_000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 32
    };

    public byte[] Serialize()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaximumFileBytes) throw Invalid("The chat file exceeds the 16 MB size limit.");
        return bytes;
    }

    public static SavedAssistantChat Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw Invalid("The chat file exceeds the 16 MB size limit.");
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            CheckDuplicateFields(document.RootElement);
            var chat = JsonSerializer.Deserialize<SavedAssistantChat>(bytes, Options) ?? throw Invalid();
            chat.Validate();
            return chat;
        }
        catch (JsonException) { throw Invalid(); }
    }

    public void Validate()
    {
        if (Version is not (1 or CurrentVersion) || Provider is not ("chatgpt" or "claude") || (Version == 1 && Provider != "chatgpt")) throw new InvalidDataException("This chat file uses an unsupported version.");
        if (string.IsNullOrWhiteSpace(AccountId) || AccountId.Length > 2000 || DatabaseTarget?.Length > 4000 || ModelId?.Length > 1000 ||
            Draft is null || Draft.Length > AssistantContext.MaximumRequestLength || Messages is null ||
            Messages.Length is < 1 or > AssistantConversation.MaximumExchanges || History is null || History.Length > 2000 ||
            Messages.Any(m => m is null || string.IsNullOrWhiteSpace(m.Question) || m.Question.Length > AssistantContext.MaximumRequestLength || m.Answer is null || m.Answer.Length > 2_000_000) ||
            JsonSerializer.Serialize(History).Length > 4_000_000) throw Invalid();
        if (Provider == "claude") { ValidateClaude(); return; }
        var users = 0;
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var answered = false;
        foreach (var item in History)
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid();
            var type = Text(item, "type");
            if (Text(item, "role") is { } role)
            {
                Fields(item, "type", "id", "status", "role", "content", "phase");
                if (Text(item, "phase") is { } phase && (role != "assistant" || phase is not ("commentary" or "final_answer"))) throw Invalid("Unsupported message phase.");
                if (role is not ("user" or "assistant") || type is not (null or "message")) throw Invalid();
                if (role == "user")
                {
                    if (pending.Count != 0 || (users > 0 && !answered)) throw Invalid();
                    users++; answered = false;
                    if (users > Messages.Length) throw Invalid();
                }
                else { if (users == 0) throw Invalid(); answered = true; }
                if (!item.TryGetProperty("content", out var content)) throw Invalid();
                if (content.ValueKind == JsonValueKind.String) { }
                else if (content.ValueKind == JsonValueKind.Array && role == "assistant")
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        Fields(part, "type", "text", "refusal", "annotations", "logprobs");
                        var partType = Text(part, "type");
                        if (partType == "output_text") Required(part, "text");
                        else if (partType == "refusal") Required(part, "refusal");
                        else throw Invalid();
                    }
                }
                else throw Invalid();
            }
            else if (type == "reasoning")
            {
                if (users == 0) throw Invalid();
                Fields(item, "type", "id", "status", "summary", "encrypted_content", "content");
                if (item.TryGetProperty("content", out var reasoning) && reasoning.ValueKind != JsonValueKind.Null)
                {
                    if (reasoning.ValueKind != JsonValueKind.Array) throw Invalid("Unsupported reasoning content.");
                    foreach (var part in reasoning.EnumerateArray())
                    {
                        Fields(part, "type", "text"); Required(part, "text");
                        if (Text(part, "type") != "reasoning_text") throw Invalid("Unsupported reasoning content.");
                    }
                }
                if (item.TryGetProperty("summary", out var summary))
                {
                    if (summary.ValueKind != JsonValueKind.Array) throw Invalid();
                    foreach (var part in summary.EnumerateArray())
                    {
                        Fields(part, "type", "text"); Required(part, "text");
                        if (Text(part, "type") is not (null or "summary_text")) throw Invalid();
                    }
                }
            }
            else if (type == "function_call")
            {
                Fields(item, "type", "id", "status", "name", "namespace", "call_id", "arguments");
                var name = Required(item, "name");
                if (name.StartsWith("rowfinch.", StringComparison.Ordinal)) name = name[9..];
                if (users == 0 || Text(item, "namespace") is not (null or "rowfinch") || name is not ("list_objects" or "describe_object" or "query") || DatabaseTarget is null) throw Invalid();
                Required(item, "arguments");
                var id = Required(item, "call_id");
                if (!calls.Add(id)) throw Invalid();
                pending.Add(id);
            }
            else if (type == "function_call_output")
            {
                Fields(item, "type", "call_id", "output"); Required(item, "output");
                if (!pending.Remove(Required(item, "call_id"))) throw Invalid();
            }
            else throw Invalid();
        }
        if (users != Messages.Length || pending.Count != 0 || !answered) throw Invalid();
    }

    private void ValidateClaude()
    {
        var exchanges = 0;
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        var answered = false;
        foreach (var message in History)
        {
            Fields(message, "role", "content");
            var role = Required(message, "role");
            if (role is not ("user" or "assistant") || role == previous || (previous is null && role != "user")) throw Invalid();
            var content = message.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
            {
                if (role != "user" || pending.Count != 0 || string.IsNullOrWhiteSpace(content.GetString())) throw Invalid();
                exchanges++; answered = false;
            }
            else if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
            {
                var hasText = false;
                foreach (var block in content.EnumerateArray())
                {
                    var type = Required(block, "type");
                    if (role == "user")
                    {
                        Fields(block, "type", "tool_use_id", "content");
                        if (type != "tool_result" || !pending.Remove(Required(block, "tool_use_id"))) throw Invalid();
                        Required(block, "content");
                    }
                    else if (type == "text")
                    {
                        Fields(block, "type", "text");
                        hasText |= !string.IsNullOrWhiteSpace(Required(block, "text"));
                    }
                    else if (type == "tool_use")
                    {
                        Fields(block, "type", "id", "name", "input");
                        if (DatabaseTarget is null || Required(block, "name") is not ("query" or "list_objects" or "describe_object") ||
                            !seen.Add(Required(block, "id")) || !block.TryGetProperty("input", out var args) || args.ValueKind != JsonValueKind.Object) throw Invalid();
                        pending.Add(Required(block, "id"));
                    }
                    else if (type == "thinking")
                    {
                        Fields(block, "type", "thinking", "signature"); Required(block, "thinking"); Required(block, "signature");
                    }
                    else if (type == "redacted_thinking") { Fields(block, "type", "data"); Required(block, "data"); }
                    else throw Invalid();
                }
                if (role == "user" && pending.Count != 0) throw Invalid();
                if (role == "assistant") answered = hasText && pending.Count == 0;
            }
            else throw Invalid();
            previous = role;
        }
        if (exchanges != Messages.Length || previous != "assistant" || pending.Count != 0 || !answered) throw Invalid();
    }

    private static void CheckDuplicateFields(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in item.EnumerateObject())
            {
                if (!seen.Add(field.Name)) throw Invalid("Duplicate JSON fields.");
                CheckDuplicateFields(field.Value);
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
            foreach (var value in item.EnumerateArray()) CheckDuplicateFields(value);
    }

    private static void Fields(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in item.EnumerateObject())
        {
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw Invalid("Unsupported or duplicate response-item fields. Update Rowfinch if this persists.");
            if (field.Name is "type" or "id" or "status" or "role" or "name" or "namespace" or "call_id" or "arguments" or "output" or "text" or "refusal" or "encrypted_content" or "phase")
                if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Invalid();
        }
    }
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement item, string name) => Text(item, name) ?? throw Invalid();
    private static InvalidDataException Invalid(string? reason = null) => new("The chat data could not be validated. " + (reason ?? "The format is unsupported, incomplete, or exceeds the chat limits."));
}
