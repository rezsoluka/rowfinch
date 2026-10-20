using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Rowfinch.Core;

namespace Rowfinch.Desktop.Views;

public partial class QueryTabView
{
    private CompletionWindow? completionWindow;
    private CancellationTokenSource? completionDelay;
    private bool completionDetached;
    private bool insertingCompletion;

    private void InitializeCompletion()
    {
        SqlEditor.TextArea.TextEntered += OnCompletionTextEntered;
        SqlEditor.TextArea.TextEntering += OnCompletionTextEntering;
        SqlEditor.TextArea.AddHandler(KeyDownEvent, OnCompletionKeyDown, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => CloseCompletion();
    }

    private async void OnCompletionKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Space && args.KeyModifiers == KeyModifiers.Control)
        {
            args.Handled = true;
            await ShowCompletionAsync(true);
        }
        else if (args.Key == Key.Escape && (completionWindow is not null || completionDelay is not null))
        {
            args.Handled = true;
            CloseCompletion();
        }
        else if (args.Key == Key.F5 || args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            CloseCompletion();
    }

    private void OnCompletionTextEntering(object? sender, TextInputEventArgs args)
    {
        if (args.Text is { Length: > 0 } text &&
            !(char.IsLetterOrDigit(text[0]) || text[0] is '_' or ']' or '"')) CloseCompletion();
    }

    private async void OnCompletionTextEntered(object? sender, TextInputEventArgs args)
    {
        if (insertingCompletion || completionWindow is not null || string.IsNullOrEmpty(args.Text)) return;
        var last = args.Text[^1];
        if (char.IsLetter(last) || last is '_' or '.' or '[' || last == ' ')
            await ShowCompletionAsync(false);
    }

    private async Task ShowCompletionAsync(bool explicitlyRequested)
    {
        CloseCompletion();
        if (completionDetached || !SqlEditor.IsKeyboardFocusWithin || !SqlEditor.TextArea.Selection.IsEmpty) return;
        var delay = completionDelay = new CancellationTokenSource();
        var token = delay.Token;
        var text = SqlEditor.Text;
        var offset = SqlEditor.CaretOffset;
        try
        {
            if (!explicitlyRequested) await Task.Delay(150, token);
            if (token.IsCancellationRequested) return;
            // Continue command phrases (ORDER -> BY, CREATE -> TABLE, etc.) without fetching metadata.
            if (!explicitlyRequested && offset > 0 && char.IsWhiteSpace(text[offset - 1]))
            {
                if (SqlCompletion.Analyze(text, offset, [])?.SuggestAfterSpace != true) return;
            }
            var context = await workspace.GetCompletionsAsync(text, offset);
            if (token.IsCancellationRequested || completionDetached || SqlEditor.Text != text ||
                SqlEditor.CaretOffset != offset || !SqlEditor.IsKeyboardFocusWithin || context is null || context.Items.Count == 0) return;
            var popup = new CompletionWindow(SqlEditor.TextArea)
            {
                StartOffset = context.StartOffset, EndOffset = context.EndOffset,
                CloseWhenCaretAtBeginning = false, MinWidth = 300, MaxWidth = 520
            };
            completionWindow = popup;
            foreach (var item in context.Items) popup.CompletionList.CompletionData.Add(new EditorCompletion(item, this));
            popup.Closed += (_, _) => { if (completionWindow == popup) completionWindow = null; };
            popup.Show();
            popup.CompletionList.SelectItem(context.Prefix);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (completionDelay == delay) completionDelay = null;
            delay.Dispose();
        }
    }

    private void CloseCompletion()
    {
        completionDelay?.Cancel();
        completionDelay = null;
        var popup = completionWindow;
        completionWindow = null;
        popup?.Hide();
    }

    private void DetachCompletion()
    {
        completionDetached = true;
        CloseCompletion();
        SqlEditor.TextArea.TextEntered -= OnCompletionTextEntered;
        SqlEditor.TextArea.TextEntering -= OnCompletionTextEntering;
        SqlEditor.TextArea.RemoveHandler(KeyDownEvent, OnCompletionKeyDown);
    }

    private sealed class EditorCompletion(SqlCompletionItem item, QueryTabView owner) : ICompletionData
    {
        public IImage Image => null!;
        public string Text => item.Text;
        public object Content => $"{item.Text}    {item.Description}";
        public object Description => new TextBlock { Text = item.Description, MaxWidth = 260, TextWrapping = TextWrapping.Wrap };
        public double Priority => item.IsCommandContinuation ? 2 : item.IsSql ? 0 : 1;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            owner.insertingCompletion = true;
            try { textArea.Document.Replace(completionSegment, item.InsertionText); }
            finally { owner.insertingCompletion = false; }
        }
    }
}
