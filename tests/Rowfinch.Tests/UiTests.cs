using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Rowfinch.Desktop;
using Rowfinch.Desktop.ViewModels;
using Rowfinch.Desktop.Views;
using Rowfinch.Infrastructure;
using AvaloniaEdit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Rowfinch.Tests.TestAppBuilder))]

namespace Rowfinch.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task KeyboardShortcutsAndCloseDialogPreserveUnsavedTabWhenCancelled()
    {
        await using var workspace = new WorkspaceViewModel(new JsonProfileStore(Path.Combine(Path.GetTempPath(), $"rowfinch-ui-{Guid.NewGuid():N}.json")));
        var window = new MainWindow(workspace);
        window.Show();
        window.FindControl<ListBox>("QueryTabs")!.Focus();
        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.T, RawInputModifiers.Control);
        Assert.Equal(2, workspace.Tabs.Count);
        var second = workspace.SelectedTab!;
        var editor = window.ActiveQueryView!.FindControl<TextEditor>("SqlEditor")!;
        editor.Document.Insert(0, "SELECT unsaved;");
        editor.Focus();
        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.W, RawInputModifiers.Control);
        var dialog = Assert.Single(window.OwnedWindows.OfType<CloseTabDialog>());
        var cancel = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Cancel"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(second, workspace.Tabs);
        Assert.Same(second, workspace.SelectedTab);
        Assert.True(second.IsDirty);
        Assert.False(second.IsClosing);
        editor.Focus();
        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.W, RawInputModifiers.Control);
        dialog = Assert.Single(window.OwnedWindows.OfType<CloseTabDialog>());
        var discard = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Close without saving"));
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Single(workspace.Tabs);
        Assert.DoesNotContain(second, workspace.Tabs);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SwitchingTabsPreservesEditorSelectionUndoResultsAndMessages()
    {
        await using var workspace = new WorkspaceViewModel(new JsonProfileStore(Path.Combine(Path.GetTempPath(), $"rowfinch-ui-{Guid.NewGuid():N}.json")));
        var window = new MainWindow(workspace);
        window.Show();
        var first = workspace.SelectedTab!;
        await first.StartDemoAsync();
        var firstView = window.ActiveQueryView!;
        var firstEditor = firstView.FindControl<TextEditor>("SqlEditor")!;
        firstEditor.Document.Insert(firstEditor.Document.TextLength, "\nSELECT 1;");
        firstEditor.Select(0, 6);
        first.OutputTabIndex = 1;
        var firstResult = first.SelectedResult;
        var firstMessages = first.Messages;
        var second = workspace.NewTab();
        var secondView = window.ActiveQueryView!;
        var secondEditor = secondView.FindControl<TextEditor>("SqlEditor")!;
        Assert.Equal("", secondEditor.Text);
        secondEditor.Document.Insert(0, "SELECT 2;");
        await second.StartDemoAsync();
        Assert.True(second.IsDirty);
        workspace.SelectedTab = first;
        window.UpdateLayout();
        Assert.Same(firstView, window.ActiveQueryView);
        Assert.EndsWith("SELECT 1;", firstEditor.Text);
        Assert.Equal(6, firstEditor.SelectionLength);
        Assert.True(firstEditor.Document.UndoStack.CanUndo);
        Assert.True(first.IsDirty);
        Assert.Equal(1, first.OutputTabIndex);
        Assert.Same(firstResult, first.SelectedResult);
        Assert.Equal(firstMessages, first.Messages);
        Assert.Equal(5, firstView.FindControl<TableView>("ResultsTable")!.Columns.Count);
        workspace.SelectedTab = second;
        Assert.Same(secondView, window.ActiveQueryView);
        Assert.Equal("SELECT 2;", secondEditor.Text);
        Assert.Equal(0, second.OutputTabIndex);
        first.MarkSaved(first.DocumentName);
        second.MarkSaved(second.DocumentName);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ObjectTreeExpandsLazilyAndContextMenuUsesTheClickedObject()
    {
        await using var workspace = new WorkspaceViewModel(new JsonProfileStore(Path.Combine(Path.GetTempPath(), $"rowfinch-ui-{Guid.NewGuid():N}.json")));
        var window = new MainWindow(workspace);
        window.Show();
        var tab = workspace.SelectedTab!;
        await tab.StartDemoAsync();
        window.UpdateLayout();
        var view = window.ActiveQueryView!;
        var tree = view.FindControl<TreeView>("ObjectTree")!;
        var customers = tab.Objects[0];
        Assert.Null(customers.Columns);
        var container = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(customers));
        container.IsExpanded = true;
        await customers.LoadTask;
        window.UpdateLayout();
        Assert.True(customers.IsExpanded);
        Assert.Equal(3, customers.Children.Count);
        Assert.Contains("PK 1", customers.Children[0].Detail);
        Assert.Equal("CustomerId", customers.Children[0].Label);
        tree.SelectedItem = customers;
        var procedure = Assert.Single(tab.Objects, n => n.Object?.Kind == "Procedure");
        var label = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == procedure.Label);
        var target = Assert.IsType<Grid>(label.Parent!.Parent);
        target.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        Assert.NotNull(target.ContextMenu);
        var actions = target.ContextMenu.ItemsSource!.Cast<Control>().OfType<MenuItem>().ToArray();
        Assert.DoesNotContain(actions, a => Equals(a.Header, "Select top 100"));
        var open = Assert.Single(actions, a => Equals(a.Header, "Open definition in new tab"));
        open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Contains("CREATE PROCEDURE [dbo].[GetCustomers]", workspace.SelectedTab!.Sql);
        Assert.Equal("dbo.GetCustomers.sql", workspace.SelectedTab.DocumentName);
        target.ContextMenu.Close();
        workspace.SelectedTab.MarkSaved(workspace.SelectedTab.DocumentName);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WorkspaceRendersDemoResultsWithDynamicColumns()
    {
        await using var workspace = new WorkspaceViewModel(new JsonProfileStore(Path.Combine(Path.GetTempPath(), $"rowfinch-ui-{Guid.NewGuid():N}.json")));
        var window = new MainWindow(workspace);
        window.Show();
        var tab = workspace.SelectedTab!;
        await tab.StartDemoAsync();
        window.UpdateLayout();
        var table = window.ActiveQueryView!.FindControl<TableView>("ResultsTable")!;
        Assert.True(tab.IsDemo);
        Assert.True(tab.CanExecute);
        Assert.Equal(5, table.Columns.Count);
        Assert.Equal(4, table.Items.Count);
        Assert.True(window.Bounds.Width >= 1050);
        workspace.NewTab();
        workspace.SelectedTab = tab;
        window.UpdateLayout();
        Assert.Equal(2, window.FindControl<ListBox>("QueryTabs")!.Items.Count);
        tab.Objects[0].IsExpanded = true;
        await tab.Objects[0].LoadTask;
        window.UpdateLayout();
        window.ActiveQueryView!.FindControl<TreeView>("ObjectTree")!.BringIntoView();
        window.UpdateLayout();
        if (Environment.GetEnvironmentVariable("ROWFINCH_SCREENSHOT") is { Length: > 0 } screenshotPath)
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(screenshotPath, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        window.Close();
    }
}
