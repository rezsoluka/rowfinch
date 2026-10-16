using System.Net;
using System.Text;
using System.Text.Json;
using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class ClaudeAssistantTests
{
    private const string Key = "sk-ant-api-test-secret-never-log";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KeyringCredentialsReloadAndSessionOnlyKeysNeverEnterSettingsOrVault()
    {
        using var fixture = new Fixture();
        using var client = fixture.Client();
        await client.ConfigureAsync(Key, "wrkspc_test", true, Token);
        var originalAccount = client.Account!.ClientId;
        Assert.Equal(1, fixture.Vault.Stored);
        var settingsText = await File.ReadAllTextAsync(fixture.Settings.FilePath, Token);
        Assert.DoesNotContain(Key, settingsText); Assert.DoesNotContain("wrkspc_test", settingsText);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Settings.FilePath));
        using var reloaded = fixture.Client(); await reloaded.InitializeAsync(Token);
        Assert.Equal(originalAccount, reloaded.Account!.ClientId);
        await reloaded.ConfigureAsync(Key, "wrkspc_test", false, Token);
        Assert.Equal(originalAccount, reloaded.Account!.ClientId);
        Assert.Empty(fixture.Vault.Values);
        using var noPersistence = fixture.Client(); await noPersistence.InitializeAsync(Token); Assert.Null(noPersistence.Account);
        await reloaded.SignOutAsync(Token); Assert.Null(reloaded.Account);
        await client.ConfigureAsync(Key, null, false, Token);
        Assert.Empty(fixture.Vault.Values);
        Assert.All(fixture.Handler.Requests, request =>
        { Assert.Equal("api.anthropic.com", request.Host); Assert.Equal("Bearer " + Key, request.Authorization); Assert.Equal("2023-06-01", request.Version); });
    }

    [Fact]
    public async Task FailedKeyringStorageKeepsPreviousCredentialAndSessionOnlySetupCanRecover()
    {
        using var fixture = new Fixture(); using var client = fixture.Client();
        await client.ConfigureAsync(Key, null, true, Token);
        var previous = client.Account;
        fixture.Vault.FailStore = true;
        await Assert.ThrowsAsync<CredentialStoreException>(() => client.ConfigureAsync("another-key", null, true, Token));
        Assert.Equal(previous, client.Account); Assert.Single(fixture.Vault.Values);
        await client.ConfigureAsync("another-key", null, false, Token);
        Assert.NotEqual(previous, client.Account); Assert.Empty(fixture.Vault.Values);
        var sessionAccount = client.Account;
        using var restart = fixture.Client();
        await restart.ConfigureAsync("another-key", null, false, Token);
        Assert.Equal(sessionAccount, restart.Account);
        await restart.SignOutAsync(Token); Assert.Null(restart.Account);
    }

    [Fact]
    public async Task TooManyCallsAreRejectedBeforeAnyToolInThatRoundRuns()
    {
        using var fixture = new Fixture(); using var client = fixture.Client(); await client.ConfigureAsync(Key, null, false, Token);
        fixture.Handler.Answers.Enqueue(Stream("tool_use", Enumerable.Range(0, 9).Select(index => Tool("call_" + index, "query", new { sql = "SELECT 1;", reason = "test" })).ToArray()));
        var session = new Database(); var approvals = 0; var chat = new AssistantConversation();
        var tools = new AssistantDatabaseTools(session, true, (_, _) => { approvals++; return Task.FromResult(true); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskConversationAsync("claude-test", "Check", chat, tools, null, Token));
        Assert.Equal(0, approvals); Assert.Equal(0, session.Queries); Assert.Equal(0, chat.CompletedExchanges);
    }

    [Fact]
    public async Task ModelsAreDiscoveredWithPaginationAndHeadersErrorsNeverEchoResponseBodies()
    {
        using var fixture = new Fixture();
        fixture.Handler.ModelPages = true;
        using var client = fixture.Client(); await client.ConfigureAsync(Key, "wrkspc_test", false, Token);
        Assert.Equal(new[] { "claude-test", "claude-other" }, (await client.GetModelsAsync(Token)).Select(model => model.Id));
        Assert.Contains(fixture.Handler.Requests, request => request.Path.Contains("after_id=claude-test", StringComparison.Ordinal));
        Assert.All(fixture.Handler.Requests, request => Assert.Equal("wrkspc_test", request.Workspace));
        var account = client.Account;
        fixture.Handler.Status = HttpStatusCode.Unauthorized;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConfigureAsync("different-secret", null, false, Token));
        Assert.DoesNotContain(Key, error.ToString()); Assert.DoesNotContain("sensitive-server-message", error.ToString());
        Assert.Equal(account, client.Account);
        fixture.Handler.Status = HttpStatusCode.OK;
        await client.ConfigureAsync("different-secret", null, false, Token);
        Assert.NotEqual(account!.ClientId, client.Account!.ClientId);
    }

    [Fact]
    public async Task NativeToolsRequireApprovalAndSavedHistoryReplaysWithoutExecutingOldCalls()
    {
        using var fixture = new Fixture(); using var client = fixture.Client();
        await client.ConfigureAsync(Key, null, false, Token);
        fixture.Handler.Answers.Enqueue(Stream("tool_use", Text("Inspecting."), Tool("schema_1", "describe_object", new { schema = "dbo", name = "Orders" })));
        fixture.Handler.Answers.Enqueue(Stream("tool_use", Tool("query_1", "query", new { sql = "SELECT 1;", reason = "Count orders" })));
        fixture.Handler.Answers.Enqueue(Stream("end_turn", Thinking(), Text("One order.\n```sql\nSELECT 1;\n```")));
        var chat = new AssistantConversation(); var session = new Database(); var approvals = 0;
        var tools = new AssistantDatabaseTools(session, true, (request, _) => { Assert.Equal("SELECT 1;", request.Sql); approvals++; return Task.FromResult(true); });
        var progress = new CollectedProgress();
        var answer = await client.AskConversationAsync("claude-test", "Count orders with my business context", chat, tools, progress, Token);
        Assert.Equal(1, approvals); Assert.Equal(1, session.Queries); Assert.Equal(1, session.Descriptions);
        Assert.Contains("One order", answer); Assert.DoesNotContain("opaque-thought", progress.Text);
        Assert.Equal("SELECT 1;", chat.LatestQuerySql()); Assert.DoesNotContain("opaque-thought", chat.Preview());
        var saved = SavedAssistantChat.Deserialize(chat.Save([new("Count orders", answer)], session.Target, "claude-test", "next draft").Serialize());
        Assert.Equal("claude", saved.Provider); Assert.Contains("signed-thinking", JsonSerializer.Serialize(saved.History));
        var restored = new AssistantConversation(); restored.Restore(saved);
        Assert.Throws<InvalidOperationException>(() => restored.Begin(client.Account!.ClientId)); // Wrong provider, even with same account.
        fixture.Handler.Answers.Enqueue(Stream("end_turn", Text("Following up.")));
        await client.AskConversationAsync("claude-test", "Explain the total", restored, null, null, Token);
        Assert.Equal(1, session.Queries); Assert.Equal(2, restored.CompletedExchanges);
        var last = fixture.Handler.Requests.Last(request => request.Body is not null);
        Assert.Contains("tool_result", last.Body); Assert.Contains("signed-thinking", last.Body); using (var requestJson = JsonDocument.Parse(last.Body!)) Assert.False(requestJson.RootElement.TryGetProperty("tools", out _));
        Assert.DoesNotContain(Key, Encoding.UTF8.GetString(saved.Serialize()));
        Assert.All(fixture.Handler.Requests.Where(request => request.Body is not null), request =>
        { using var json = JsonDocument.Parse(request.Body!); Assert.False(json.RootElement.TryGetProperty("input", out _)); Assert.True(json.RootElement.GetProperty("stream").GetBoolean()); Assert.Equal(4096, json.RootElement.GetProperty("max_tokens").GetInt32()); });
    }

    [Fact]
    public async Task PolicyRejectionReturnsFeedbackAndDecliningBlocksFurtherQueriesInTheQuestion()
    {
        using var fixture = new Fixture(); using var client = fixture.Client(); await client.ConfigureAsync(Key, null, false, Token);
        fixture.Handler.Answers.Enqueue(Stream("tool_use", Tool("bad", "query", new { sql = "SELECT 1 INTO dbo.No;", reason = "bad" })));
        fixture.Handler.Answers.Enqueue(Stream("tool_use", Tool("valid", "query", new { sql = "SELECT 1;", reason = "valid" })));
        fixture.Handler.Answers.Enqueue(Stream("tool_use", Tool("retry", "query", new { sql = "SELECT 2;", reason = "retry" })));
        fixture.Handler.Answers.Enqueue(Stream("end_turn", Text("The query was declined.")));
        var session = new Database(); var approvals = 0;
        var tools = new AssistantDatabaseTools(session, false, (request, _) => { ReadOnlySqlPolicy.Validate(request.Sql); approvals++; return Task.FromResult(false); });
        await client.AskConversationAsync("claude-test", "Check data", new(), tools, null, Token);
        Assert.Equal(1, approvals); Assert.Equal(0, session.Queries);
        Assert.Contains(fixture.Handler.Requests, request => request.Body?.Contains("sql_policy_rejected", StringComparison.Ordinal) == true);
        Assert.Contains(fixture.Handler.Requests, request => request.Body?.Contains("Further queries are disabled", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("missing_stop")]
    [InlineData("duplicate_id")]
    [InlineData("unknown_tool")]
    [InlineData("bad_json")]
    [InlineData("late_delta")]
    public async Task PartialMalformedOrUnexpectedToolsNeverExecuteOrCommit(string failure)
    {
        using var fixture = new Fixture(); using var client = fixture.Client(); await client.ConfigureAsync(Key, null, false, Token);
        var tool = Tool("query_1", failure == "unknown_tool" ? "shell" : "query", new { sql = "SELECT 1;", reason = "test" });
        var stream = Stream(failure == "max_tokens" ? "max_tokens" : "tool_use", failure == "duplicate_id" ? [tool, tool] : [tool]);
        if (failure == "missing_stop") stream = stream[..stream.LastIndexOf("event: message_stop", StringComparison.Ordinal)];
        if (failure == "bad_json") stream = stream.Replace("SELECT 1;", "SELECT \\");
        if (failure == "late_delta") stream = stream.Replace("event: message_delta", Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "{}" } }) + "event: message_delta");
        fixture.Handler.Answers.Enqueue(stream);
        var chat = new AssistantConversation(); var session = new Database(); var approvals = 0;
        var tools = new AssistantDatabaseTools(session, true, (_, _) => { approvals++; return Task.FromResult(true); });
        await Assert.ThrowsAnyAsync<Exception>(() => client.AskConversationAsync("claude-test", "Check", chat, tools, null, Token));
        Assert.Equal(0, approvals); Assert.Equal(0, session.Queries); Assert.Equal(0, chat.CompletedExchanges);
    }

    [Fact]
    public async Task CancellationOrClearingDuringApprovalDiscardsTheTurnAndRunsNothing()
    {
        using var fixture = new Fixture(); using var client = fixture.Client(); await client.ConfigureAsync(Key, null, false, Token);
        fixture.Handler.Answers.Enqueue(Stream("tool_use", Tool("query_1", "query", new { sql = "SELECT 1;", reason = "test" })));
        var chat = new AssistantConversation(); var session = new Database();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var tools = new AssistantDatabaseTools(session, false, (_, _) => { chat.Clear(); cancellation.Cancel(); return Task.FromResult(true); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AskConversationAsync("claude-test", "Check", chat, tools, null, cancellation.Token));
        Assert.Equal(0, chat.CompletedExchanges); Assert.Equal(0, session.Queries);
    }

    [Fact]
    public void LegacyChatFilesRemainReadableButClaudeFilesRejectOtherProvidersAndPrivilegedContent()
    {
        var legacy = new AssistantConversation();
        using (var turn = legacy.Begin("account")) turn.Commit([new { role = "user", content = "q" }, new { role = "assistant", content = "a" }]);
        var saved = legacy.Save([new("q", "a")], null, "model", "") with { Version = 1 };
        var oldJson = Encoding.UTF8.GetString(saved.Serialize()).Replace(",\n  \"provider\": \"chatgpt\"", "");
        Assert.DoesNotContain("provider", oldJson);
        Assert.Equal("chatgpt", SavedAssistantChat.Deserialize(Encoding.UTF8.GetBytes(oldJson)).Provider);
        var chat = new AssistantConversation();
        using (var turn = chat.Begin("account", providerId: "claude")) turn.Commit([new { role = "user", content = "q" }, new { role = "assistant", content = new[] { Text("a") } }]);
        var claude = chat.Save([new("q", "a")], null, "model", "");
        Assert.Throws<InvalidDataException>(() => (claude with { Provider = "chatgpt" }).Validate());
        Assert.Throws<InvalidDataException>(() => (claude with { Version = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (claude with { Provider = "unknown" }).Validate());
        var raw = Encoding.UTF8.GetString(claude.Serialize());
        Assert.Throws<InvalidDataException>(() => SavedAssistantChat.Deserialize(Encoding.UTF8.GetBytes(raw.Replace("\"role\": \"user\"", "\"role\": \"system\""))));
    }

    private static object Text(string text) => new { type = "text", text };
    private static object Thinking() => new { type = "thinking", thinking = "opaque-thought", signature = "signed-thinking" };
    private static object Tool(string id, string name, object input) => new { type = "tool_use", id, name, input };
    private static string Event(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Stream(string stop, params object[] blocks)
    {
        var result = new StringBuilder(Event("message_start", new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } }));
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = JsonSerializer.SerializeToElement(blocks[i]);
            object start = block; object? delta = null;
            if (block.GetProperty("type").GetString() == "text") { start = Text(""); delta = new { type = "text_delta", text = block.GetProperty("text").GetString() }; }
            else if (block.GetProperty("type").GetString() == "tool_use") { start = Tool(block.GetProperty("id").GetString()!, block.GetProperty("name").GetString()!, new { }); delta = new { type = "input_json_delta", partial_json = block.GetProperty("input").GetRawText() }; }
            result.Append(Event("content_block_start", new { type = "content_block_start", index = i, content_block = start }));
            if (delta is not null) result.Append(Event("content_block_delta", new { type = "content_block_delta", index = i, delta }));
            result.Append(Event("content_block_stop", new { type = "content_block_stop", index = i }));
        }
        result.Append(Event("message_delta", new { type = "message_delta", delta = new { stop_reason = stop } }));
        return result.Append(Event("message_stop", new { type = "message_stop" })).ToString();
    }
    private sealed class CollectedProgress : IProgress<string> { public string Text { get; private set; } = ""; public void Report(string value) => Text += value; }
    private sealed class Database : IAssistantDatabaseSession
    {
        public string Target => "Orders · example.test · reader"; public bool CanQuery => true;
        public int Queries, Descriptions;
        public Task<string> QueryAsync(string sql, CancellationToken token) { token.ThrowIfCancellationRequested(); Queries++; return Task.FromResult("{\"rows\":[[1]]}"); }
        public Task<string> DescribeObjectAsync(string schema, string name, CancellationToken token) { Descriptions++; return Task.FromResult("{\"columns\":[\"Id\"]}"); }
        public Task<string> ListObjectsAsync(string filter, CancellationToken token) => Task.FromResult("[]");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Vault : ICredentialStore
    {
        public Dictionary<CredentialIdentity, string> Values { get; } = []; public int Stored; public bool FailStore;
        public Task<string?> GetAsync(CredentialIdentity identity, CancellationToken token) => Task.FromResult(Values.GetValueOrDefault(identity));
        public Task StoreAsync(CredentialIdentity identity, string label, string password, CancellationToken token) { if (FailStore) throw new CredentialStoreException(CredentialStoreFailure.LockedOrDenied); Stored++; Values[identity] = password; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken token) { foreach (var key in Values.Keys.Where(key => key.ProfileId == id).ToArray()) Values.Remove(key); return Task.CompletedTask; }
    }
    private sealed record RequestData(string Host, string Path, string? Authorization, string? Version, string? Workspace, string? Body);
    private sealed class Handler : HttpMessageHandler
    {
        public List<RequestData> Requests { get; } = []; public Queue<string> Answers { get; } = [];
        public HttpStatusCode Status = HttpStatusCode.OK; public bool ModelPages;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.RequestUri!.Host, request.RequestUri.PathAndQuery, request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("anthropic-version").Single(), request.Headers.TryGetValues("anthropic-workspace-id", out var values) ? values.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("sensitive-server-message " + Key) };
            if (request.Method == HttpMethod.Get)
            {
                var next = request.RequestUri.Query.Contains("after_id", StringComparison.Ordinal);
                return new(Status) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { id = next ? "claude-other" : "claude-test", display_name = next ? "Other" : "Test Claude" } }, has_more = ModelPages && !next, last_id = next ? "claude-other" : "claude-test" })) };
            }
            return new(Status) { Content = new StringContent(Answers.Dequeue(), Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rowfinch-claude-tests-" + Guid.NewGuid().ToString("N"));
        public LocalSettingsStore Settings { get; } public Vault Vault { get; } = new(); public Handler Handler { get; } = new();
        private readonly HttpClient http;
        public Fixture() { Settings = new(Path.Combine(directory, "settings.json")); http = new(Handler); }
        public ClaudeAssistantClient Client() => new(Settings, Vault, http);
        public void Dispose() { http.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
