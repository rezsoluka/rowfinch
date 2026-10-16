using Rowfinch.Core;
using Xunit;

namespace Rowfinch.Tests;

public sealed class AssistantConversationTests
{
    [Fact]
    public void LatestQueryComesOnlyFromCommittedSupportedDatabaseCalls()
    {
        var chat = new AssistantConversation();
        using (var failed = chat.Begin("account")) { Assert.Empty(chat.LatestQuerySql()); }
        using (var turn = chat.Begin("account")) turn.Commit([
            new { role = "user", content = "Orders" },
            new { type = "function_call", name = "query", arguments = "{\"sql\":\"SELECT 1;\"}" },
            new { type = "function_call", name = "describe_object", arguments = "{\"sql\":\"do not open metadata\"}" },
            new { type = "function_call", name = "rowfinch.query", arguments = "{\"sql\":\"SELECT 2;\"}" },
            new { role = "assistant", content = "Two orders found, without a SQL code block." }
        ]);
        Assert.Equal("SELECT 2;", chat.LatestQuerySql());
        chat.Clear(); Assert.Empty(chat.LatestQuerySql());
    }

    [Fact]
    public void ClearingInvalidatesPendingTurnsWithoutDamagingANewerChat()
    {
        var chat = new AssistantConversation();
        using var old = chat.Begin("account");
        chat.Clear();
        using var current = chat.Begin("account");
        Assert.Throws<OperationCanceledException>(() => old.Commit([new { role = "user", content = "old secret" }]));
        old.Dispose();
        current.Commit([new { role = "user", content = "new message" }, new { role = "assistant", content = "new answer" }]);
        Assert.Equal(1, chat.CompletedExchanges);
        Assert.DoesNotContain("old secret", chat.Preview());
        Assert.Contains("new answer", chat.Preview());
    }

    [Fact]
    public void AccountConnectionAndPermissionChangesRequireAClearedConversation()
    {
        var chat = new AssistantConversation(); var source = new object();
        using (var first = chat.Begin("account-a", source, true, true)) first.Commit([new { role = "user", content = "private" }]);
        Assert.Throws<InvalidOperationException>(() => chat.Begin("account-b", source, true, true));
        Assert.Throws<InvalidOperationException>(() => chat.Begin("account-a", new object(), true, true));
        Assert.Throws<InvalidOperationException>(() => chat.Begin("account-a", source, false, true));
        Assert.Throws<InvalidOperationException>(() => chat.Begin("account-a", source, true, false));
        chat.Clear();
        using var second = chat.Begin("account-b", new object()); Assert.Empty(second.Input);
    }

    [Fact]
    public void FailedTurnsAreAtomicAndHistoryLimitsDoNotSilentlyDiscardOlderMessages()
    {
        var chat = new AssistantConversation();
        using (var failed = chat.Begin("account")) { Assert.Empty(failed.Input); }
        Assert.Equal(0, chat.CompletedExchanges);
        var transcript = new List<object>();
        for (var i = 0; i < AssistantConversation.MaximumExchanges; i++)
        {
            using var turn = chat.Begin("account");
            transcript.Add(new { role = "user", content = "question" + i });
            transcript.Add(new { role = "assistant", content = "answer" + i });
            turn.Commit(transcript);
        }
        Assert.False(chat.CanContinue);
        Assert.Contains("question0", chat.Preview());
        Assert.Throws<InvalidOperationException>(() => chat.Begin("account"));
        chat.Clear(); Assert.True(chat.CanContinue);
    }
}
