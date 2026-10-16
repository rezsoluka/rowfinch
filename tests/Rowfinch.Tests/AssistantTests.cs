using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class AssistantTests
{
    [Fact]
    public void ContextIsOptInAndResultSamplesAreBoundedWithoutLosingDuplicateColumns()
    {
        Assert.Equal("Explain joins", AssistantContext.Build("Explain joins"));
        var result = new QueryResultSet("sample", [new("Name", "nvarchar"), new("Name", "nvarchar")],
            Enumerable.Range(0, 10).Select(i => new object?[] { $"row{i}", new string('x', 3000) }).ToArray(), false);
        var text = AssistantContext.Build("Explain", "SELECT 1;", "[dbo].[Customers]", result, connectionNotes: "PO is the order header; POitems contains lines.");
        Assert.Contains("POitems contains lines", text);
        Assert.DoesNotContain("Connection notes", AssistantContext.Build("Explain"));
        Assert.Contains("SELECT 1;", text);
        Assert.Contains("[dbo].[Customers]", text);
        Assert.Contains("Name_2", text);
        Assert.Contains("row4", text);
        Assert.DoesNotContain("row5", text);
        Assert.Contains("[truncated]", text);
        Assert.Throws<ArgumentException>(() => AssistantContext.Build("Explain", new string('x', 60_001)));
    }

    [Fact]
    public async Task NativeSignInValidatesIdentityAndUsesIssuedClientPkceAndKeyringOnly()
    {
        using var fixture = new AuthFixture();
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        Assert.Equal("oaiapp_test", client.Account!.ClientId);
        Assert.Equal("person@example.test", client.Account.DisplayName);
        Assert.True(client.Account.CanUsePlan);
        Assert.Equal("dynamic_agent_client", fixture.Authorization!["client_id"]);
        Assert.Equal("Rowfinch", fixture.Authorization["agent_name_hint"]);
        Assert.StartsWith("urn:uuid:", fixture.Authorization["ext_agent_host_id"]);
        Assert.StartsWith("http://127.0.0.1:", fixture.Authorization["redirect_uri"]);
        Assert.EndsWith("/auth/callback", fixture.Authorization["redirect_uri"]);
        Assert.Equal("oaiapp_test", fixture.TokenForms[0]["client_id"]);
        Assert.Equal(fixture.Authorization["redirect_uri"], fixture.TokenForms[0]["redirect_uri"]);
        Assert.Equal(fixture.Authorization["code_challenge"], Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(fixture.TokenForms[0]["code_verifier"]))));
        Assert.Contains("access-secret", fixture.Vault.Json);
        foreach (var file in Directory.GetFiles(fixture.Directory))
        {
            var contents = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("access-secret", contents);
            Assert.DoesNotContain("refresh-secret", contents);
        }
        using var restored = fixture.CreateClient();
        await restored.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(client.Account, restored.Account);
        var models = await restored.GetModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("available-model", Assert.Single(models).Id);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("subject")]
    [InlineData("future-iat")]
    public async Task InvalidOpenIdTokensCannotAuthorizeRequests(string problem)
    {
        using var fixture = new AuthFixture { InvalidIdentity = problem };
        using var client = fixture.CreateClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken));
        Assert.Null(client.Account);
        Assert.DoesNotContain("access-secret", fixture.Vault.Json ?? "");
        Assert.Equal(0, fixture.ResponseCalls);
    }

    [Fact]
    public async Task CallbackScopeDoesNotOverrideMissingPlanPermissionInTheTokenResponse()
    {
        using var fixture = new AuthFixture { GrantedScope = "openid profile email" };
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        Assert.False(client.Account!.CanUsePlan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskAsync("available-model", "Explain SQL", null, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.ResponseCalls);
    }

    [Fact]
    public async Task WrongCallbackStateIsRejectedAndReturningSignInReusesHostAndIssuedClient()
    {
        using var fixture = new AuthFixture { SendWrongStateFirst = true };
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var host = fixture.Authorization!["ext_agent_host_id"];
        await client.SignInAsync(client.Account!.ClientId, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        Assert.Equal("oaiapp_test", fixture.Authorization!["client_id"]);
        Assert.Equal(host, fixture.Authorization["ext_agent_host_id"]);
        Assert.False(fixture.Authorization.ContainsKey("agent_name_hint"));
        Assert.Single(client.SavedAccounts);
    }

    [Fact]
    public async Task ReturningSignInRejectsAChangedClientOrAccountWithoutReplacingTheActiveSession()
    {
        using var fixture = new AuthFixture();
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var account = client.Account;
        fixture.ReturnedClient = "oaiapp_other";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SignInAsync(account!.ClientId, fixture.BrowserAsync, TestContext.Current.CancellationToken));
        Assert.Equal(account, client.Account);
        fixture.ReturnedClient = "oaiapp_test"; fixture.Subject = "other-user";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SignInAsync(account!.ClientId, fixture.BrowserAsync, TestContext.Current.CancellationToken));
        Assert.Equal(account, client.Account);
    }

    [Fact]
    public async Task ExpiringTokensRefreshOnceAcrossClientsAndRotateCredentialsTogether()
    {
        using var fixture = new AuthFixture { InitialExpiry = 1 };
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        await first.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        await second.InitializeAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(first.GetModelsAsync(TestContext.Current.CancellationToken), second.GetModelsAsync(TestContext.Current.CancellationToken));
        var refresh = Assert.Single(fixture.TokenForms, form => form["grant_type"] == "refresh_token");
        Assert.Equal("refresh-secret", refresh["refresh_token"]);
        Assert.Equal("oaiapp_test", refresh["client_id"]);
        Assert.False(refresh.ContainsKey("scope"));
        Assert.Contains("rotated-access", fixture.Vault.Json);
        Assert.Contains("rotated-refresh", fixture.Vault.Json);
    }

    [Fact]
    public async Task AnotherProcessesAccountChangeCannotSendContextToTheNewAccount()
    {
        using var fixture = new AuthFixture();
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        await first.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        fixture.ReturnedClient = "oaiapp_other"; fixture.Subject = "other-user";
        await second.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => first.AskAsync("available-model", "Private first-account context", null, TestContext.Current.CancellationToken));
        Assert.Contains("account changed", error.Message);
        Assert.Equal(0, fixture.ResponseCalls);
    }

    [Fact]
    public async Task ResponsesUseOnlyTheReviewedRequestAndSupportedSubscriptionFields()
    {
        using var fixture = new AuthFixture();
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var answer = await client.AskAsync("available-model", "Explain SELECT 42;", null, TestContext.Current.CancellationToken);
        Assert.Equal("```sql\nSELECT 42;\n```", answer);
        using var json = JsonDocument.Parse(fixture.RequestBody!);
        Assert.False(json.RootElement.GetProperty("store").GetBoolean());
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("Explain SELECT 42;", json.RootElement.GetProperty("input")[0].GetProperty("content").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("input").GetArrayLength());
        Assert.DoesNotContain("tools", json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("refresh-secret", fixture.RequestBody);
        Assert.Equal("access-secret", fixture.LastAuthorization);
    }

    [Fact]
    public async Task CancellingAPendingAnswerStopsTheRequest()
    {
        using var fixture = new AuthFixture { BlockResponse = true };
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var request = client.AskAsync("available-model", "Explain", null, cancellation.Token);
        await fixture.ResponseStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.NotNull(client.Account);
    }

    [Fact]
    public async Task SignOutRevokesTheRenewableSessionClearsTokensAndRetainsRegistration()
    {
        using var fixture = new AuthFixture();
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var message = await client.SignOutAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.RevokeCalls);
        Assert.Contains("ended", message);
        Assert.Null(client.Account);
        Assert.DoesNotContain("access-secret", fixture.Vault.Json);
        Assert.DoesNotContain("refresh-secret", fixture.Vault.Json);
        Assert.Contains("oaiapp_test", fixture.Vault.Json);
        Assert.Single(client.SavedAccounts);
    }

    [Fact]
    public async Task FailedRevocationStillClearsLocalTokensAndReportsItAccurately()
    {
        using var fixture = new AuthFixture { FailRevocation = true };
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var message = await client.SignOutAsync(TestContext.Current.CancellationToken);
        Assert.Contains("not confirmed", message);
        Assert.Null(client.Account);
        Assert.DoesNotContain("refresh-secret", fixture.Vault.Json);
    }

    [Fact]
    public async Task DisposingDuringSignOutDoesNotEraseOtherSavedAccounts()
    {
        using var fixture = new AuthFixture { BlockRevocation = true };
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        fixture.ReturnedClient = "oaiapp_other"; fixture.Subject = "other-user";
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var signOut = client.SignOutAsync(cancellation.Token);
        await fixture.RevocationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        client.Dispose(); cancellation.Cancel();
        Assert.Contains("not confirmed", await signOut);
        using var saved = JsonDocument.Parse(fixture.Vault.Json!);
        var sessions = saved.RootElement.GetProperty("Sessions").EnumerateArray().ToArray();
        Assert.Equal("access-secret", Assert.Single(sessions, session => session.GetProperty("ClientId").GetString() == "oaiapp_test").GetProperty("AccessToken").GetString());
        Assert.Equal(JsonValueKind.Null, Assert.Single(sessions, session => session.GetProperty("ClientId").GetString() == "oaiapp_other").GetProperty("AccessToken").ValueKind);
    }

    [Theory]
    [InlineData("response.failed", "subscription_sharing_usage_limit_exceeded", "usage limit")]
    [InlineData("response.failed", "subscription_sharing_usage_unavailable", "unavailable")]
    [InlineData("response.incomplete", null, "could not complete")]
    public async Task FailedOrIncompleteStreamsNeverReturnAPartialAnswerAsSuccess(string kind, string? code, string message)
    {
        var payload = "data: " + JsonSerializer.Serialize(new { type = kind, response = new { error = new { code } } }) + "\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" + payload));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantResponseStream.ReadAsync(stream, null, TestContext.Current.CancellationToken));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADisconnectedStreamCannotBeMistakenForCompletion()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantResponseStream.ReadAsync(stream, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseCallsUseNamespacesAndCarryReasoningAndResultsWithinOneQuestion()
    {
        using var fixture = new AuthFixture();
        fixture.ResponseStreams.Enqueue(ToolResponse("list_objects", "{\"filter\":\"Customers\"}"));
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        await using var database = new ToolDatabase();
        var answer = await client.AskWithDatabaseAsync("available-model", "Find customers", new(database, true, (_, _) => Task.FromResult(false)), null, TestContext.Current.CancellationToken);
        Assert.Contains("SELECT 42", answer);
        Assert.Equal(1, database.SchemaCalls);
        Assert.Equal(0, database.QueryCalls);
        Assert.Equal(2, fixture.RequestBodies.Count);
        using var first = JsonDocument.Parse(fixture.RequestBodies[0]);
        var definition = first.RootElement.GetProperty("tools")[0];
        Assert.Equal("namespace", definition.GetProperty("type").GetString());
        Assert.Equal("rowfinch", definition.GetProperty("name").GetString());
        Assert.DoesNotContain(definition.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "query");
        using var second = JsonDocument.Parse(fixture.RequestBodies[1]);
        var input = second.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Contains(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning" && item.GetProperty("encrypted_content").GetString() == "opaque-reasoning");
        Assert.Contains(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output" && item.GetProperty("output").GetString()!.Contains("Customers"));
        Assert.DoesNotContain("previous_response_id", fixture.RequestBodies[1]);
        await client.AskAsync("available-model", "A new question", null, TestContext.Current.CancellationToken);
        using var third = JsonDocument.Parse(fixture.RequestBodies[2]);
        Assert.Equal(1, third.RootElement.GetProperty("input").GetArrayLength());
        Assert.False(third.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task StreamedToolItemsContinueToAFinalizedAnswerWithoutAnyTextDeltas()
    {
        using var fixture = new AuthFixture();
        static string Event(object payload) => "data: " + JsonSerializer.Serialize(payload) + "\n\n";
        var call = new { type = "function_call", id = "f1", call_id = "call1", @namespace = "rowfinch", name = "list_objects", arguments = "{\"filter\":\"POItems\"}", status = "completed" };
        var reasoning = new { type = "reasoning", id = "r1", summary = Array.Empty<object>(), encrypted_content = "opaque-reasoning", content = (object?)null };
        fixture.ResponseStreams.Enqueue(Event(new { type = "response.output_item.done", output_index = 0, item = reasoning }) +
            Event(new { type = "response.output_item.done", output_index = 1, item = call }) +
            Event(new { type = "response.completed", response = new { status = "completed" } }));
        var message = new { type = "message", id = "m1", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "```sql\nSELECT COUNT(*) FROM dbo.POItems WHERE config_id = 'SH155FX';\n```" } } };
        fixture.ResponseStreams.Enqueue(Event(new { type = "response.output_item.done", output_index = 0, item = message }) +
            Event(new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>() } }));
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        await using var database = new ToolDatabase();
        var answer = await client.AskWithDatabaseAsync("available-model", "Count matching POItems", new(database, true, (_, _) => Task.FromResult(false)), null, TestContext.Current.CancellationToken);
        Assert.Contains("COUNT(*)", answer);
        Assert.Contains("SH155FX", answer);
        Assert.Equal(1, database.SchemaCalls); Assert.Equal(0, database.QueryCalls);
        Assert.Equal(2, fixture.RequestBodies.Count);
        Assert.Contains("opaque-reasoning", fixture.RequestBodies[1]);
        Assert.Contains("function_call_output", fixture.RequestBodies[1]);
    }

    [Fact]
    public async Task DatabaseToolLoopsStopAndIncompleteStreamsCannotExecuteCalls()
    {
        using var fixture = new AuthFixture();
        for (var i = 0; i < 10; i++) fixture.ResponseStreams.Enqueue(ToolResponse("list_objects", "{\"filter\":\"\"}"));
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        await using var database = new ToolDatabase();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskWithDatabaseAsync("available-model", "Inspect", new(database, true, (_, _) => Task.FromResult(false)), null, TestContext.Current.CancellationToken));
        Assert.Equal(AssistantDatabaseTools.MaximumCalls, database.SchemaCalls);
        fixture.ResponseStreams.Clear();
        fixture.ResponseStreams.Enqueue(ToolResponse("list_objects", "{\"filter\":\"\"}").Replace("response.completed", "response.incomplete"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskWithDatabaseAsync("available-model", "Inspect", new(database, true, (_, _) => Task.FromResult(false)), null, TestContext.Current.CancellationToken));
        Assert.Equal(AssistantDatabaseTools.MaximumCalls, database.SchemaCalls);
    }

    [Fact]
    public async Task CredentialLockIsReleasedForApprovalAndAccountChangePreventsSharingResults()
    {
        using var fixture = new AuthFixture();
        fixture.ResponseStreams.Enqueue(ToolResponse("query", "{\"sql\":\"SELECT 1;\",\"reason\":\"Test\"}"));
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        await using var database = new ToolDatabase { CanQuery = true };
        var tools = new AssistantDatabaseTools(database, false, async (_, token) => { await client.SignOutAsync(token); return true; });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskWithDatabaseAsync("available-model", "Count", tools, null, TestContext.Current.CancellationToken));
        Assert.Contains("account changed", error.Message);
        Assert.Single(fixture.RequestBodies);
        Assert.DoesNotContain("private-result", fixture.RequestBodies[0]);
    }

    [Fact]
    public async Task FollowUpsReplayMessagesReasoningAndApprovedResultsWithoutRerunningQueries()
    {
        using var fixture = new AuthFixture();
        fixture.ResponseStreams.Enqueue(ToolResponse("query", "{\"sql\":\"SELECT 1;\",\"reason\":\"Read orders\"}"));
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        await using var database = new ToolDatabase { CanQuery = true };
        var conversation = new AssistantConversation(); var approvals = 0;
        AssistantDatabaseTools Tools() => new(database, true, (_, _) => { approvals++; return Task.FromResult(true); });
        await client.AskConversationAsync("available-model", "Find orders", conversation, Tools(), null, TestContext.Current.CancellationToken);
        Assert.Equal(1, conversation.CompletedExchanges);
        Assert.Contains("private-result", conversation.Preview()); Assert.DoesNotContain("opaque-reasoning", conversation.Preview());
        // Save/reopen across window lifetimes: opaque reasoning and tool results survive,
        // but access must be explicitly rebound to a validated new session by the UI.
        var saved = SavedAssistantChat.Deserialize(conversation.Save([new("Find orders", "SELECT 42")], database.Target, "available-model", "").Serialize());
        conversation = new AssistantConversation(); conversation.Restore(saved);
        conversation.BindRestoredDatabase(client.Account!.ClientId, database, true, true);
        await client.AskConversationAsync("available-model", "Which customer placed those orders?", conversation, Tools(), null, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(fixture.RequestBodies.Last());
        var input = body.RootElement.GetProperty("input").GetRawText();
        Assert.Contains("Find orders", input); Assert.Contains("private-result", input);
        Assert.Contains("opaque-reasoning", input); Assert.Contains("SELECT 42", input);
        Assert.Equal(1, approvals); Assert.Equal(1, database.QueryCalls);
        Assert.Equal(2, conversation.CompletedExchanges);
        // Fresh queries still require fresh approvals, including on later messages.
        fixture.ResponseStreams.Enqueue(ToolResponse("query", "{\"sql\":\"SELECT 2;\",\"reason\":\"Refresh data\"}"));
        await client.AskConversationAsync("available-model", "Refresh the counts", conversation, Tools(), null, TestContext.Current.CancellationToken);
        Assert.Equal(2, approvals); Assert.Equal(2, database.QueryCalls);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean()); Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("previous_response_id", out _));
        conversation.Clear();
        await client.AskConversationAsync("available-model", "New chat", conversation, null, null, TestContext.Current.CancellationToken);
        using var fresh = JsonDocument.Parse(fixture.RequestBodies.Last());
        Assert.Equal(1, fresh.RootElement.GetProperty("input").GetArrayLength());
        Assert.DoesNotContain("private-result", fixture.RequestBodies.Last());
    }

    [Fact]
    public async Task FailedOrClearedTurnsCannotPolluteTheNextFollowUp()
    {
        using var fixture = new AuthFixture(); using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var conversation = new AssistantConversation();
        await client.AskConversationAsync("available-model", "Completed question", conversation, null, null, TestContext.Current.CancellationToken);
        fixture.ResponseStreams.Enqueue("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial-private-answer\"}\n\ndata: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\"}}\n\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskConversationAsync("available-model", "Failed question", conversation, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(1, conversation.CompletedExchanges);
        await client.AskConversationAsync("available-model", "Continue the completed question", conversation, null, null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Failed question", fixture.RequestBodies.Last()); Assert.DoesNotContain("partial-private-answer", fixture.RequestBodies.Last());
        await using var database = new ToolDatabase { CanQuery = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskConversationAsync("available-model", "Changed database", conversation, new(database, true, (_, _) => Task.FromResult(false)), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AChatCannotBeReplayedIntoADifferentSignedInAccount()
    {
        using var fixture = new AuthFixture(); using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var chat = new AssistantConversation();
        await client.AskConversationAsync("available-model", "Private account-a question", chat, null, null, TestContext.Current.CancellationToken);
        fixture.ReturnedClient = "account-b"; fixture.Subject = "other-person";
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        var count = fixture.ResponseCalls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskConversationAsync("available-model", "Follow up", chat, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(count, fixture.ResponseCalls);
        chat.Clear();
        await client.AskConversationAsync("available-model", "New account-b question", chat, null, null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("account-a", fixture.RequestBodies.Last());
    }

    [Fact]
    public async Task PolicyRejectionIsSentBackToTheModelAndARevisedWeekQueryNeedsFreshApproval()
    {
        using var fixture = new AuthFixture();
        fixture.ResponseStreams.Enqueue(ToolResponse("query", JsonSerializer.Serialize(new { sql = "DECLARE @week date = GETDATE(); SELECT @week;", reason = "Week boundary" })));
        fixture.ResponseStreams.Enqueue(ToolResponse("query", JsonSerializer.Serialize(new { sql = "SELECT DATEADD(week, DATEDIFF(week, 0, GETDATE()), 0);", reason = "Week boundary as one SELECT" })));
        using var client = fixture.CreateClient();
        await client.SignInAsync(null, fixture.BrowserAsync, TestContext.Current.CancellationToken);
        await using var database = new ToolDatabase { CanQuery = true };
        var approvals = 0;
        var tools = new AssistantDatabaseTools(database, true, (request, _) =>
        { ReadOnlySqlPolicy.Validate(request.Sql); approvals++; return Task.FromResult(true); });
        var chat = new AssistantConversation();
        var answer = await client.AskConversationAsync("available-model", "Total units and sales this week", chat, tools, null, TestContext.Current.CancellationToken);
        Assert.Contains("SELECT 42", answer);
        Assert.Equal(1, approvals); Assert.Equal(1, database.QueryCalls); Assert.Equal(3, fixture.RequestBodies.Count);
        using var correction = JsonDocument.Parse(fixture.RequestBodies[1]);
        var feedback = correction.RootElement.GetProperty("input").EnumerateArray().Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output").GetProperty("output").GetString()!;
        using var rejected = JsonDocument.Parse(feedback);
        Assert.Equal("sql_policy_rejected", rejected.RootElement.GetProperty("code").GetString());
        Assert.False(rejected.RootElement.GetProperty("executed").GetBoolean());
        Assert.Equal(1, chat.CompletedExchanges);
        var restored = SavedAssistantChat.Deserialize(chat.Save([new("Total units and sales this week", answer)], database.Target, "available-model", "").Serialize());
        Assert.Contains("sql_policy_rejected", JsonSerializer.Serialize(restored.History));
    }

    private static string ToolResponse(string name, string arguments) => "data: " + JsonSerializer.Serialize(new
    {
        type = "response.completed", response = new { status = "completed", output = new object[]
        {
            new { type = "reasoning", id = "r1", summary = Array.Empty<object>(), encrypted_content = "opaque-reasoning", content = (object?)null },
            new { type = "function_call", id = "f1", call_id = "call" + Guid.NewGuid().ToString("N"), @namespace = "rowfinch", name, arguments }
        } }
    }) + "\n\n";

    private sealed class ToolDatabase : IAssistantDatabaseSession
    {
        public string Target => "Private database";
        public bool CanQuery { get; set; }
        public int SchemaCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public Task<string> ListObjectsAsync(string filter, CancellationToken token) { SchemaCalls++; return Task.FromResult("{\"objects\":[\"Customers\"]}"); }
        public Task<string> DescribeObjectAsync(string schema, string name, CancellationToken token) => Task.FromResult("[]");
        public Task<string> QueryAsync(string sql, CancellationToken token) { QueryCalls++; return Task.FromResult("private-result"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Vault : ICredentialStore
    {
        public string? Json { get; private set; }
        public Task<string?> GetAsync(CredentialIdentity identity, CancellationToken token) => Task.FromResult(Json);
        public Task StoreAsync(CredentialIdentity identity, string label, string password, CancellationToken token) { Json = password; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken token) { Json = null; return Task.CompletedTask; }
    }

    private sealed class AuthFixture : HttpMessageHandler
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "rowfinch-auth-" + Guid.NewGuid().ToString("N"));
        public Vault Vault { get; } = new();
        public Dictionary<string, string>? Authorization { get; private set; }
        public List<Dictionary<string, string>> TokenForms { get; } = [];
        public string? InvalidIdentity { get; init; }
        public string GrantedScope { get; init; } = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        public string ReturnedClient { get; set; } = "oaiapp_test";
        public string Subject { get; set; } = "test-subject";
        public bool SendWrongStateFirst { get; init; }
        public bool FailRevocation { get; init; }
        public bool BlockRevocation { get; init; }
        public bool BlockResponse { get; init; }
        public int InitialExpiry { get; init; } = 3600;
        public Queue<string> ResponseStreams { get; } = new();
        public List<string> RequestBodies { get; } = [];
        public int ResponseCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public string? RequestBody { get; private set; }
        public string? LastAuthorization { get; private set; }
        public TaskCompletionSource ResponseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RevocationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RSA rsa = RSA.Create(2048);
        public ChatGptAssistantClient CreateClient() => new(new LocalSettingsStore(Path.Combine(Directory, "settings.json")), Vault, new HttpClient(this, disposeHandler: false));

        public async Task BrowserAsync(Uri uri)
        {
            Authorization = LoopbackOAuthCallback.ParseQuery(uri.Query);
            using var browser = new HttpClient();
            var callback = Authorization["redirect_uri"] + "?code=test-code&scope=chatgpt.tokens.use.direct&client_id=" + ReturnedClient + "&state=";
            if (SendWrongStateFirst)
            { using var rejected = await browser.GetAsync(callback + "wrong-state", TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); }
            using var response = await browser.GetAsync(callback + Uri.EscapeDataString(Authorization["state"]), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("openid-configuration")) return Json(new { issuer = "https://auth.openai.com", jwks_uri = "https://auth.openai.com/.well-known/jwks.json", revocation_endpoint = "https://auth.openai.com/revoke" });
            if (path.EndsWith("jwks.json"))
            {
                var key = rsa.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", kid = "test-key", alg = "RS256", use = "sig", n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } } });
            }
            if (path.EndsWith("/oauth/token"))
            {
                var form = LoopbackOAuthCallback.ParseQuery(await request.Content!.ReadAsStringAsync(token)); TokenForms.Add(form);
                if (form["grant_type"] == "refresh_token") return Json(new { access_token = "rotated-access", refresh_token = "rotated-refresh", token_type = "Bearer", expires_in = 3600 });
                using var other = RSA.Create(2048);
                var identity = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = InvalidIdentity == "issuer" ? "https://untrusted.example" : "https://auth.openai.com",
                    Audience = InvalidIdentity == "audience" ? "wrong-client" : ReturnedClient,
                    IssuedAt = InvalidIdentity == "future-iat" ? DateTime.UtcNow.AddMinutes(1) : DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
                    Expires = InvalidIdentity == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(10),
                    Claims = new Dictionary<string, object> { ["sub"] = InvalidIdentity == "subject" ? "" : Subject,
                        ["email"] = "person@example.test", ["nonce"] = InvalidIdentity == "nonce" ? "wrong-nonce" : Authorization!["nonce"] },
                    SigningCredentials = new SigningCredentials(new RsaSecurityKey(InvalidIdentity == "signature" ? other : rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)
                });
                return Json(new { id_token = identity, access_token = "access-secret", refresh_token = "refresh-secret", token_type = "Bearer", expires_in = InitialExpiry, scope = GrantedScope });
            }
            if (path == "/revoke")
            {
                RevokeCalls++;
                RevocationStarted.TrySetResult();
                if (BlockRevocation) await Task.Delay(Timeout.Infinite, token);
                var form = LoopbackOAuthCallback.ParseQuery(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("refresh-secret", form["token"]); Assert.Equal("refresh_token", form["token_type_hint"]);
                return new HttpResponseMessage(FailRevocation ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            }
            LastAuthorization = request.Headers.Authorization?.Parameter;
            if (path == "/v1/models") return Json(new { models = new[] { new { slug = "available-model", display_name = "Available model", visibility = "list" }, new { slug = "hidden-model", display_name = "Hidden", visibility = "hide" } } });
            if (path == "/v1/responses")
            {
                ResponseCalls++; RequestBody = await request.Content!.ReadAsStringAsync(token); RequestBodies.Add(RequestBody); ResponseStarted.TrySetResult();
                if (BlockResponse) await Task.Delay(Timeout.Infinite, token);
                var text = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = "```sql\nSELECT 42;\n```" }) + "\n\n" +
                    "data: " + JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", output = new[] { new { type = "message", id = "msg_final", role = "assistant", phase = "final_answer", status = "completed", content = new[] { new { type = "output_text", text = "```sql\nSELECT 42;\n```", annotations = Array.Empty<object>() } } } } } }) + "\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponseStreams.Count > 0 ? ResponseStreams.Dequeue() : text, Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException("Unexpected test HTTP endpoint.");
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        protected override void Dispose(bool disposing)
        { if (disposing) { rsa.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); } base.Dispose(disposing); }
    }
}
