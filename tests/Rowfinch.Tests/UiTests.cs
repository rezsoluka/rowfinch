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
using System.Reflection;
using Rowfinch.Core;
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
    public async Task SidebarFiltersAndColumnControlsDriveSelectedColumnUpdateFromTheClickedObject()
    {
        var profiles = Enumerable.Range(1, 20).Select(i => new ConnectionProfile
            { Name = $"Development {i}", Server = "example.database.windows.net", Database = $"database{i}", UserName = "reader" }).ToArray();
        await using var workspace = new WorkspaceViewModel(new UiProfiles(profiles));
        var window = new MainWindow(workspace);
        window.Show();
        var tab = workspace.SelectedTab!;
        var view = window.ActiveQueryView!;
        var sidebar = view.FindControl<TabControl>("SidebarTabs")!;
        Assert.Equal(Dock.Left, sidebar.TabStripPlacement);
        Assert.Equal(0, sidebar.SelectedIndex);
        Assert.False(tab.ConnectionDetailsExpanded);
        tab.ConnectionDetailsExpanded = false;
        window.UpdateLayout();
        Assert.Equal(20, workspace.Profiles.Count);
        Assert.True(view.FindControl<ListBox>("ConnectionList")!.Bounds.Height > 500);
        if (Environment.GetEnvironmentVariable("ROWFINCH_CONNECTIONS_SCREENSHOT") is { Length: > 0 } path)
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Click(window, Assert.IsType<ListBoxItem>(view.FindControl<ListBox>("ConnectionList")!.ContainerFromIndex(0)));
        Assert.True(tab.ConnectionDetailsExpanded);
        Assert.NotNull(tab.SelectedProfile);
        await tab.StartDemoAsync();
        window.UpdateLayout();
        Assert.Equal(1, sidebar.SelectedIndex);
        var connectionsTab = view.FindControl<TabItem>("ConnectionsSidebarTab")!;
        var objectsTab = view.FindControl<TabItem>("ObjectsSidebarTab")!;
        Assert.Equal(40, connectionsTab.Bounds.Width);
        Assert.Equal(40, objectsTab.Bounds.Width);
        Assert.True(connectionsTab.Bounds.Height > connectionsTab.Bounds.Width);
        Click(window, connectionsTab);
        Assert.Equal(0, tab.SidebarTabIndex);
        Click(window, objectsTab);
        Assert.Equal(1, tab.SidebarTabIndex);
        Assert.True(view.FindControl<TreeView>("ObjectTree")!.Bounds.Height > 450);
        Click(window, view.FindControl<CheckBox>("TablesTypeFilter")!);
        Assert.True(tab.ShowTables);
        Assert.False(view.FindControl<RadioButton>("AllTypesFilter")!.IsChecked);
        Assert.Equal(2, tab.Objects.Count);
        Click(window, view.FindControl<CheckBox>("ViewsTypeFilter")!);
        Assert.Equal(3, tab.Objects.Count);
        view.FindControl<TextBox>("ObjectFilterInput")!.Text = "monthly";
        Assert.Equal("View", Assert.Single(tab.Objects).Object!.Kind);
        Click(window, view.FindControl<RadioButton>("AllTypesFilter")!);
        Assert.True(tab.AllObjectTypes);
        view.FindControl<TextBox>("ObjectFilterInput")!.Text = "";
        Assert.Equal(5, tab.Objects.Count);
        var customers = tab.Objects[0];
        var tree = view.FindControl<TreeView>("ObjectTree")!;
        window.UpdateLayout();
        Assert.IsType<TreeViewItem>(tree.ContainerFromItem(customers)).IsExpanded = true;
        await customers.LoadTask;
        window.UpdateLayout();
        var unselect = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Unselect all") && b.DataContext == customers);
        Click(window, unselect);
        Assert.Empty(customers.SelectedColumns);
        var select = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Select all") && b.DataContext == customers);
        Click(window, select);
        Assert.Equal(3, customers.SelectedColumns.Count);
        foreach (var column in customers.Children.Take(2))
        {
            var checkbox = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(), c => c.DataContext == column);
            Click(window, checkbox);
            Assert.False(column.IsSelected);
        }
        Assert.Equal("Email", Assert.Single(customers.SelectedColumns).Name);
        tree.SelectedItem = tab.Objects[1]; // The context action must use the clicked column's owner.
        var label = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Email");
        var target = Assert.IsType<Grid>(label.Parent!.Parent);
        target.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        var update = Assert.Single(target.ContextMenu!.ItemsSource!.Cast<Control>().OfType<MenuItem>(), a => Equals(a.Header, "Generate UPDATE in new tab"));
        var sourceSql = tab.Sql;
        var sourceResult = tab.SelectedResult;
        update.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal("dbo.Customers.update.sql", workspace.SelectedTab!.DocumentName);
        Assert.Contains("SET\n    [Email] = <value_1>\nWHERE\n    [CustomerId] = <key_1>;", workspace.SelectedTab.Sql);
        Assert.Equal(sourceSql, tab.Sql);
        Assert.Same(sourceResult, tab.SelectedResult);
        target.ContextMenu.Close();
        workspace.SelectedTab.MarkSaved(workspace.SelectedTab.DocumentName);
        workspace.SelectedTab.SidebarTabIndex = 0;
        workspace.SelectedTab.ShowFunctions = true;
        workspace.SelectedTab = tab;
        Assert.Same(view, window.ActiveQueryView);
        Assert.Equal(1, tab.SidebarTabIndex);
        Assert.True(tab.AllObjectTypes);
        Assert.Equal("Email", Assert.Single(customers.SelectedColumns).Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ConnectionDetailsStayWithinTheSidebarAtMinimumWindowSize()
    {
        await using var workspace = new WorkspaceViewModel(new UiProfiles([]));
        var window = new MainWindow(workspace) { Width = 1050, Height = 780 };
        window.Show();
        window.UpdateLayout();
        var view = window.ActiveQueryView!;
        var details = view.FindControl<Expander>("ConnectionDetails")!;
        Assert.True(details.IsExpanded);
        Assert.True(view.FindControl<ListBox>("ConnectionList")!.Bounds.Height >= 60);
        var bottom = details.TranslatePoint(new Point(0, details.Bounds.Height), view)!.Value.Y;
        Assert.True(bottom <= view.Bounds.Height - 34, $"Connection form bottom {bottom} exceeds sidebar {view.Bounds.Height - 34}.");
        var connect = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Connect"));
        Assert.True(connect.TranslatePoint(new Point(0, connect.Bounds.Height), view)!.Value.Y <= view.Bounds.Height - 34);
        if (Environment.GetEnvironmentVariable("ROWFINCH_SMALL_SCREENSHOT") is { Length: > 0 } path)
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        window.Close();
    }

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        window.UpdateLayout();
    }

    private sealed class UiProfiles(IReadOnlyList<ConnectionProfile> profiles) : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(profiles);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> saved, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

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
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var versionText = window.FindControl<TextBlock>("AppVersionText")!;
        Assert.Equal($"v{version.Split('+')[0]}", versionText.Text);
        Assert.Equal($"Rowfinch {version}", ToolTip.GetTip(Assert.IsType<Border>(versionText.Parent)));
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
