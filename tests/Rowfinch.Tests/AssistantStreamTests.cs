using System.Text;
using System.Text.Json;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class AssistantStreamTests
{
    private const string Answer = "```sql\nSELECT COUNT(*) FROM dbo.POItems WHERE config_id = 'SH155FX';\n```";
    private static object Message(string text = Answer) => new { type = "message", id = "msg_1", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text } } };
    private static object Function => new { type = "function_call", id = "fc_1", call_id = "call_1", @namespace = "rowfinch", name = "list_objects", arguments = "{\"filter\":\"POItems\"}", status = "completed" };
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Complete(params object[] output) => Event(new { type = "response.completed", response = new { status = "completed", output } });
    private static Task<AssistantTurn> Read(string events, IProgress<string>? progress = null) => AssistantResponseStream.ReadTurnAsync(new MemoryStream(Encoding.UTF8.GetBytes(events)), progress, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TextInTheCompletedResponseIsAnAnswerEvenWithoutDeltaEvents()
    {
        var turn = await Read(Complete(Message()));
        Assert.Equal(Answer, turn.Text);
    }

    [Fact]
    public async Task FinalizedMessageSurvivesAnEmptyTerminalOutputArray()
    {
        var turn = await Read(Event(new { type = "response.output_item.done", output_index = 0, item = Message() }) + Complete());
        Assert.Equal(Answer, turn.Text);
        Assert.Equal("msg_1", Assert.Single(turn.Output).GetProperty("id").GetString());
    }

    [Fact]
    public async Task FinalizedFunctionCallsAndOpaqueReasoningSurviveASummaryOnlyCompletion()
    {
        var turn = await Read(Event(new { type = "response.output_item.done", output_index = 0,
            item = new { type = "reasoning", id = "rs_1", encrypted_content = "opaque", summary = Array.Empty<object>() } }) +
            Event(new { type = "response.output_item.done", output_index = 1, item = Function }) + Complete());
        Assert.Empty(turn.Text);
        Assert.Equal(2, turn.Output.Count);
        Assert.Equal("opaque", turn.Output[0].GetProperty("encrypted_content").GetString());
        Assert.Equal("call_1", turn.Output[1].GetProperty("call_id").GetString());
    }

    [Theory]
    [InlineData("response.output_text.done")]
    [InlineData("response.content_part.done")]
    public async Task FinalizedTextPartsAreDisplayedWithoutIncrementalText(string type)
    {
        var payload = type == "response.output_text.done"
            ? Event(new { type, output_index = 0, content_index = 0, text = Answer })
            : Event(new { type, output_index = 0, content_index = 0, part = new { type = "output_text", text = Answer } });
        Assert.Equal(Answer, (await Read(payload + Complete())).Text);
    }

    [Fact]
    public async Task DeltasDoneEventsAndTerminalMessagesDoNotDuplicateTheAnswer()
    {
        var progress = new Deltas();
        var turn = await Read(Event(new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta = Answer[..10] }) +
            Event(new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta = Answer[10..] }) +
            Event(new { type = "response.output_text.done", output_index = 0, content_index = 0, text = Answer }) +
            Event(new { type = "response.output_item.done", output_index = 0, item = Message() }) + Complete(Message()), progress);
        Assert.Equal(Answer, turn.Text);
        Assert.Equal(Answer, string.Concat(progress.Values));
        Assert.Single(turn.Output);
    }

    [Fact]
    public async Task RefusalsAndMultipleContentPartsArePreserved()
    {
        Assert.Equal("Cannot help with that.", (await Read(Event(new { type = "response.refusal.done", output_index = 0, content_index = 0, refusal = "Cannot help with that." }) + Complete())).Text);
        var turn = await Read(Event(new { type = "response.output_text.done", output_index = 1, content_index = 0, text = "Second" }) +
            Event(new { type = "response.output_text.done", output_index = 0, content_index = 0, text = "First" }) + Complete());
        Assert.Equal("First\n\nSecond", turn.Text);
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("disconnect")]
    public async Task FinalizedToolsAreNeverReleasedBeforeSuccessfulResponseCompletion(string outcome)
    {
        var input = Event(new { type = "response.output_item.done", output_index = 0, item = Function });
        if (outcome != "disconnect") input += Event(new { type = outcome, response = new { status = "incomplete" } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(input));
    }

    [Fact]
    public async Task TerminalCompletionAtEndOfFileWithoutAnEmptyLineIsConsumed()
        => Assert.Equal(Answer, (await Read(Complete(Message()).TrimEnd('\n'))).Text);

    [Fact]
    public async Task EmptyAnswersGiveStructuralDiagnosticsWithoutLeakingPayloads()
    {
        var input = Event(new { type = "response.output_item.done", output_index = 0,
            item = new { type = "reasoning", id = "private-response-id", encrypted_content = "never-log-this-secret", summary = new[] { new { text = "private schema or question" } } } }) + Complete();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(input));
        Assert.Contains("finalized items: 1", error.Message);
        Assert.DoesNotContain("never-log", error.Message);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task FinalizedAnswerSizeLimitsApplyWithoutDeltaEvents()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(Complete(Message(new string('x', 200_001)))));
        Assert.Contains("preview limit", error.Message);
    }

    [Fact]
    public async Task ConflictingFinalizedAndTerminalToolCallsAreRejected()
    {
        var conflicting = new { type = "function_call", id = "fc_1", call_id = "call_1", @namespace = "rowfinch", name = "list_objects", arguments = "{\"filter\":\"different\"}", status = "completed" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(Event(new { type = "response.output_item.done", output_index = 0, item = Function }) + Complete(conflicting)));
    }

    [Fact]
    public async Task EventHeadersCanIdentifyAnEventWhenItsPayloadOmitsType()
    {
        var input = "event: response.output_item.done\n" + Event(new { output_index = 0, item = Message() }) +
            "event: response.completed\n" + Event(new { response = new { status = "completed" } });
        Assert.Equal(Answer, (await Read(input)).Text);
    }

    private sealed class Deltas : IProgress<string>
    {
        public List<string> Values { get; } = [];
        public void Report(string value) => Values.Add(value);
    }
}
