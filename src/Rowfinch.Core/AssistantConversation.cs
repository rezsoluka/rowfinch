using System.Text;
using System.Text.Json;

namespace Rowfinch.Core;

/// <summary>One in-memory chat. Completed turns commit atomically; clearing invalidates pending turns.</summary>
public sealed class AssistantConversation
{
    public const int MaximumExchanges = 20;
    public const int MaximumInputLength = 1_000_000;
    private readonly object sync = new();
    private JsonElement[] history = [];
    private string? account;
    private object? database;
    private bool schema, queries;
    private bool restored;
    private Turn? active;
    private int exchanges, length, revision;
    public int Revision { get { lock (sync) return revision; } }
    public int CompletedExchanges { get { lock (sync) return exchanges; } }
    public bool CanContinue { get { lock (sync) return exchanges < MaximumExchanges && length < MaximumInputLength; } }

    public Turn Begin(string accountId, object? databaseScope = null, bool allowSchema = false, bool allowQueries = false)
    {
        lock (sync)
        {
            if (active is not null) throw new InvalidOperationException("Wait for the current answer or cancel it before sending another message.");
            if (!CanContinue) throw new InvalidOperationException("This chat reached its history limit. Choose New chat to continue.");
            if (exchanges > 0 && (account != accountId || !ReferenceEquals(database, databaseScope) || schema != allowSchema || queries != allowQueries))
                throw new InvalidOperationException("The ChatGPT account or database access changed. Choose New chat before sending more context.");
            account = accountId; database = databaseScope; schema = allowSchema; queries = allowQueries; restored = false;
            return active = new Turn(this, revision, history.ToArray());
        }
    }

    public SavedAssistantChat Save(IEnumerable<SavedChatMessage> messages, string? target, string? model, string draft)
    {
        lock (sync)
        {
            if (active is not null) throw new InvalidOperationException("Wait for the answer before saving the chat.");
            var saved = new SavedAssistantChat(SavedAssistantChat.CurrentVersion, DateTimeOffset.UtcNow,
                account ?? "", target, model, draft, messages.ToArray(), history.Select(item => item.Clone()).ToArray());
            saved.Validate();
            return saved;
        }
    }

    public void Restore(SavedAssistantChat saved)
    {
        saved.Validate();
        lock (sync)
        {
            Clear();
            history = saved.History.Select(item => item.Clone()).ToArray();
            account = saved.AccountId; exchanges = saved.Messages.Length;
            length = JsonSerializer.Serialize(history).Length;
            restored = true;
        }
    }

    // Rebinding requires an explicit UI action after opening a new, independently validated session.
    // The file itself can neither create a database session nor grant capabilities.
    public void BindRestoredDatabase(string accountId, object scope, bool allowSchema, bool allowQueries)
    {
        lock (sync)
        {
            if (!restored || active is not null || account != accountId)
                throw new InvalidOperationException("Open the chat with its original ChatGPT account before enabling database access.");
            database = scope; schema = allowSchema; queries = allowQueries; restored = false;
        }
    }

    public void Clear()
    {
        lock (sync) { history = []; restored = false; account = null; database = null; schema = queries = false; exchanges = length = 0; active = null; revision++; }
    }

    public string Preview()
    {
        lock (sync)
        {
            if (history.Length == 0) return "No earlier messages or shared data in this chat.";
            var text = new StringBuilder();
            var names = new Dictionary<string, string>();
            foreach (var item in history)
            {
                var type = String(item, "type");
                if (String(item, "role") is { } role)
                {
                    text.AppendLine(role == "user" ? "YOU" : "ASSISTANT");
                    if (item.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind == JsonValueKind.String) text.AppendLine(content.GetString());
                        else if (content.ValueKind == JsonValueKind.Array)
                            foreach (var part in content.EnumerateArray()) text.AppendLine(String(part, "text") ?? String(part, "refusal"));
                    }
                    text.AppendLine();
                }
                else if (type == "function_call")
                {
                    var name = String(item, "name") ?? "database request";
                    if (String(item, "call_id") is { } id) names[id] = name;
                    text.AppendLine("DATABASE REQUEST · " + name).AppendLine(String(item, "arguments")).AppendLine();
                }
                else if (type == "function_call_output")
                    text.AppendLine("DATABASE RESULT · " + names.GetValueOrDefault(String(item, "call_id") ?? "", "database request"))
                        .AppendLine(String(item, "output")).AppendLine();
                // Opaque model state is retained for replay, never decoded or shown as chat text.
            }
            return text.ToString();
        }
    }

    private static string? String(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    public sealed class Turn : IDisposable
    {
        private readonly AssistantConversation owner;
        private readonly int revision;
        private bool committed;
        internal Turn(AssistantConversation owner, int revision, IReadOnlyList<JsonElement> input) { this.owner = owner; this.revision = revision; Input = input; }
        public IReadOnlyList<JsonElement> Input { get; }
        public void EnsureCurrent()
        {
            lock (owner.sync)
                if (committed || owner.revision != revision || !ReferenceEquals(owner.active, this)) throw new OperationCanceledException("The chat was cleared. This answer was discarded.");
        }
        public void Commit(IEnumerable<object> input)
        {
            var items = input.Select(item => JsonSerializer.SerializeToElement(item)).ToArray();
            var size = JsonSerializer.Serialize(items).Length;
            if (size > 4_000_000) throw new InvalidOperationException("The completed chat exceeded the history limit. Choose New chat to continue.");
            lock (owner.sync)
            {
                EnsureCurrent(); owner.history = items; owner.length = size; owner.exchanges++; committed = true;
            }
        }
        public void Dispose() { lock (owner.sync) { if (ReferenceEquals(owner.active, this)) owner.active = null; } }
    }
}
