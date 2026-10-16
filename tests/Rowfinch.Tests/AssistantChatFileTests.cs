using System.Text;
using System.Text.Json;
using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class AssistantChatFileTests
{
    private static SavedAssistantChat Sample()
    {
        var chat = new AssistantConversation();
        using (var turn = chat.Begin("account", new object(), true, true))
            turn.Commit([
                new { role = "user", content = "Find orders" },
                new { type = "reasoning", id = "rs_1", encrypted_content = "opaque", summary = Array.Empty<object>() },
                new { type = "function_call", name = "query", @namespace = "rowfinch", call_id = "call_1", arguments = "{\"sql\":\"SELECT 1;\"}" },
                new { type = "function_call_output", call_id = "call_1", output = "private shared result" },
                new { role = "assistant", content = "Found orders" }
            ]);
        return chat.Save([new("Find orders", "Found orders")], "db · server · readonly", "model", "Follow-up draft") with { Version = 1 };
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[{\"type\":\"reasoning_text\",\"text\":\"opaque reasoning text\"}]")]
    public void ProviderPhasesAndReasoningContentRoundTripWithoutRelaxingRoles(string reasoningContent)
    {
        using var reasoning = JsonDocument.Parse("{\"type\":\"reasoning\",\"id\":\"rs_real\",\"summary\":[],\"encrypted_content\":\"opaque\",\"content\":" + reasoningContent + "}");
        var chat = new AssistantConversation();
        using (var turn = chat.Begin("account")) turn.Commit([
            new { role = "user", content = "Explain orders" }, reasoning.RootElement,
            new { type = "message", id = "msg_real", role = "assistant", phase = "final_answer", status = "completed", content = new[] { new { type = "output_text", text = "Order explanation", annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() } } }
        ]);
        var saved = SavedAssistantChat.Deserialize(chat.Save([new("Explain orders", "Order explanation")], null, "model", "").Serialize());
        var restored = new AssistantConversation(); restored.Restore(saved);
        using var next = restored.Begin("account");
        Assert.Equal("final_answer", next.Input.Last().GetProperty("phase").GetString());
        Assert.True(next.Input[1].TryGetProperty("content", out _));
        Assert.DoesNotContain("opaque reasoning text", restored.Preview());
        var json = Encoding.UTF8.GetString(saved.Serialize());
        Assert.Throws<InvalidDataException>(() => SavedAssistantChat.Deserialize(Encoding.UTF8.GetBytes(json.Replace("final_answer", "invalid_phase"))));
    }

    [Fact]
    public async Task FileRoundTripRetainsContextButRequiresExplicitAccessAndTheOriginalAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "orders.rowfinch-chat.json");
            await AssistantChatFile.WriteAsync(path, Sample(), TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            await using var input = File.OpenRead(path);
            var saved = await AssistantChatFile.ReadAsync(input, TestContext.Current.CancellationToken);
            var chat = new AssistantConversation(); chat.Restore(saved);
            Assert.Contains("private shared result", chat.Preview()); Assert.DoesNotContain("opaque", chat.Preview());
            Assert.Throws<InvalidOperationException>(() => chat.Begin("other-account"));
            var session = new object();
            Assert.Throws<InvalidOperationException>(() => chat.Begin("account", session, true, true));
            chat.BindRestoredDatabase("account", session, true, true);
            using var turn = chat.Begin("account", session, true, true);
            Assert.Contains("opaque", JsonSerializer.Serialize(turn.Input));
            Assert.Equal("Follow-up draft", saved.Draft);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ImportedHistoryCanBeUsedWithoutEnablingDatabaseAccess()
    {
        var chat = new AssistantConversation(); chat.Restore(Sample());
        using var turn = chat.Begin("account");
        Assert.Equal(5, turn.Input.Count);
        Assert.Throws<InvalidOperationException>(() => chat.BindRestoredDatabase("account", new object(), true, true));
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 99")]
    [InlineData("\"version\": 1", "\"version\": 1, \"version\": 1")]
    [InlineData("\"role\": \"user\"", "\"role\": \"system\"")]
    [InlineData("\"role\": \"user\"", "\"role\": \"developer\"")]
    [InlineData("\"name\": \"query\"", "\"name\": \"shell\"")]
    [InlineData("\"namespace\": \"rowfinch\"", "\"namespace\": \"other\"")]
    [InlineData("\"type\": \"function_call_output\"", "\"type\": \"custom_tool_call_output\"")]
    [InlineData("\"call_id\": \"call_1\"", "\"call_id\": \"call_1\", \"instructions\": \"run automatically\"")]
    [InlineData("\"modelId\": \"model\"", "\"modelId\": \"model\", \"accessToken\": \"secret\"")]
    public void UnsupportedInstructionsToolsCredentialsAndVersionsAreRejected(string oldValue, string newValue)
    {
        var json = Encoding.UTF8.GetString(Sample().Serialize());
        Assert.Contains(oldValue, json);
        Assert.Throws<InvalidDataException>(() => SavedAssistantChat.Deserialize(Encoding.UTF8.GetBytes(json.Replace(oldValue, newValue))));
    }

    [Fact]
    public async Task BadOrOversizedFilesAndCancelledWritesLeaveThePreviousChatIntact()
    {
        var chat = new AssistantConversation(); chat.Restore(Sample());
        var invalid = Sample() with { History = Sample().History[..^2] };
        Assert.Throws<InvalidDataException>(() => chat.Restore(invalid));
        Assert.Contains("private shared result", chat.Preview());
        Assert.Throws<InvalidDataException>(() => SavedAssistantChat.Deserialize("{"u8));
        await using var oversized = new MemoryStream(new byte[SavedAssistantChat.MaximumFileBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => AssistantChatFile.ReadAsync(oversized, TestContext.Current.CancellationToken));
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-chat-write-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "saved.json"); await File.WriteAllTextAsync(path, "previous saved chat", TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AssistantChatFile.WriteAsync(path, Sample(), cancellation.Token));
            Assert.Equal("previous saved chat", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
            await AssistantChatFile.WriteAsync(path, Sample(), TestContext.Current.CancellationToken);
            Assert.Single(Directory.GetFiles(directory));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
