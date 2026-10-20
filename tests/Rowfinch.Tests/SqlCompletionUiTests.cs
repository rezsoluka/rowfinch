using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Rowfinch.Desktop.Views;
using Xunit;

namespace Rowfinch.Tests;

public sealed class SqlCompletionUiTests
{
    [AvaloniaFact]
    public async Task SqlCommandsAndAutomaticPhraseContinuationsWorkWithoutADatabase()
    {
        await using var tab = new QueryTabViewModel(new Profiles());
        var view = new QueryTabView(tab);
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        var editor = view.FindControl<TextEditor>("SqlEditor")!;
        editor.Text = "";
        window.UpdateLayout();
        editor.TextArea.Focus();
        window.KeyTextInput("CRE");
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        var popup = Popup(view)!;
        popup.CompletionList.SelectedItem = popup.CompletionList.CompletionData.Single(i => i.Text == "CREATE TABLE");
        Key(window, PhysicalKey.Tab);
        Assert.Equal("CREATE TABLE", editor.Text);
        editor.Undo();
        Assert.Equal("CRE", editor.Text);

        editor.Text = "SELECT * FROM dbo.Customers ORDER";
        editor.CaretOffset = editor.Text.Length;
        window.KeyTextInput(" ");
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        popup = Popup(view)!;
        Assert.Equal("BY", popup.CompletionList.SelectedItem.Text);
        Key(window, PhysicalKey.Enter);
        Assert.Equal("SELECT * FROM dbo.Customers ORDER BY", editor.Text);
        Assert.False(tab.IsConnected);
        Assert.Empty(tab.Results);
        view.Detach();
        window.Close();
    }

    private static CompletionWindow? Popup(QueryTabView view) =>
        (CompletionWindow?)typeof(QueryTabView).GetField("completionWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view);

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void Key(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    [AvaloniaFact]
    public async Task CtrlSpaceInsertsAQualifiedObjectWithTabAndSupportsUndo()
    {
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace);
        window.Show();
        var tab = workspace.SelectedTab!;
        await tab.StartDemoAsync();
        var view = window.ActiveQueryView!;
        var editor = view.FindControl<TextEditor>("SqlEditor")!;
        editor.Text = "SELECT * FROM Cus";
        editor.CaretOffset = editor.Text.Length;
        window.UpdateLayout(); editor.TextArea.Focus(); Assert.True(editor.IsKeyboardFocusWithin);
        var result = tab.SelectedResult;
        Key(window, PhysicalKey.Space, RawInputModifiers.Control);
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        var popup = Popup(view)!;
        Assert.Equal("Customers", Assert.Single(popup.CompletionList.CompletionData).Text);
        Key(window, PhysicalKey.Tab);
        Assert.Equal("SELECT * FROM [dbo].[Customers]", editor.Text);
        Assert.Equal(editor.Text, tab.Sql);
        Assert.Null(Popup(view));
        editor.Undo();
        Assert.Equal("SELECT * FROM Cus", editor.Text);
        Assert.Same(result, tab.SelectedResult);
        tab.MarkSaved("Completion.sql");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TypingAnAliasDotLoadsColumnsAndEnterInsertsWithoutRunning()
    {
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace);
        window.Show();
        var tab = workspace.SelectedTab!;
        await tab.StartDemoAsync();
        var result = tab.SelectedResult;
        var view = window.ActiveQueryView!;
        var editor = view.FindControl<TextEditor>("SqlEditor")!;
        editor.Text = "SELECT c FROM dbo.Customers c";
        editor.CaretOffset = 8;
        window.UpdateLayout(); editor.TextArea.Focus(); Assert.True(editor.IsKeyboardFocusWithin);
        window.KeyTextInput(".");
        Assert.Equal("SELECT c. FROM dbo.Customers c", editor.Text);
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        var popup = Popup(view)!;
        Assert.Equal(new[] { "CustomerId", "Email", "Name" }, popup.CompletionList.CompletionData.Select(i => i.Text));
        popup.CompletionList.SelectedItem = popup.CompletionList.CompletionData.Single(i => i.Text == "Name");
        if (Environment.GetEnvironmentVariable("ROWFINCH_COMPLETION_SCREENSHOT") is { Length: > 0 } path)
        {
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Key(window, PhysicalKey.Enter);
        Assert.Equal("SELECT c.[Name] FROM dbo.Customers c", editor.Text);
        Assert.Same(result, tab.SelectedResult);
        Assert.False(tab.IsBusy);
        editor.CaretOffset = 9;
        Key(window, PhysicalKey.Space, RawInputModifiers.Control);
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        Key(window, PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitUntil(() => !tab.IsBusy);
        Assert.Null(Popup(view));
        Assert.Equal("SELECT c.[Name] FROM dbo.Customers c", editor.Text);
        Assert.NotSame(result, tab.SelectedResult);
        tab.MarkSaved("Completion.sql");
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscapeDismissesSuggestionsAndTypingInsideCommentsDoesNotOpenThem()
    {
        await using var tab = new QueryTabViewModel(new Profiles());
        await tab.StartDemoAsync();
        var view = new QueryTabView(tab);
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        var editor = view.FindControl<TextEditor>("SqlEditor")!;
        editor.Text = "SELECT * FROM "; editor.CaretOffset = editor.Text.Length; window.UpdateLayout(); editor.TextArea.Focus(); Assert.True(editor.IsKeyboardFocusWithin);
        window.KeyTextInput("Cu");
        Assert.EndsWith("Cu", editor.Text);
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        Key(window, PhysicalKey.Escape);
        Assert.Null(Popup(view));
        Assert.Equal("SELECT * FROM Cu", editor.Text);
        editor.Text = "-- FROM "; editor.CaretOffset = editor.Text.Length;
        window.KeyTextInput("Cu");
        await Task.Delay(250);
        Assert.Null(Popup(view));
        Key(window, PhysicalKey.Space, RawInputModifiers.Control);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Null(Popup(view));
        view.Detach();
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisconnectAndTabSwitchCloseThePopupAndKeepMetadataInItsSourceTab()
    {
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace);
        window.Show();
        var tab = workspace.SelectedTab!;
        await tab.StartDemoAsync();
        var view = window.ActiveQueryView!;
        var editor = view.FindControl<TextEditor>("SqlEditor")!;
        editor.Text = "SELECT * FROM Cu"; editor.CaretOffset = editor.Text.Length; window.UpdateLayout(); editor.TextArea.Focus(); Assert.True(editor.IsKeyboardFocusWithin);
        Key(window, PhysicalKey.Space, RawInputModifiers.Control);
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        workspace.NewTab();
        Assert.Null(Popup(view));
        workspace.SelectedTab = tab;
        window.UpdateLayout(); editor.TextArea.Focus(); Assert.True(editor.IsKeyboardFocusWithin);
        Key(window, PhysicalKey.Space, RawInputModifiers.Control);
        await WaitUntil(() => Popup(view)?.IsOpen == true);
        await tab.DisconnectAsync();
        Assert.Null(Popup(view));
        tab.MarkSaved("Completion.sql");
        window.Close();
    }

    private sealed class Profiles : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
