using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Rowfinch.Desktop.ViewModels;

namespace Rowfinch.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly WorkspaceViewModel workspace;
    private readonly Dictionary<QueryTabViewModel, QueryTabView> views = [];
    private bool allowClose;
    private bool closeWindowPending;
    private bool filePickerOpen;
    private static readonly FilePickerFileType SqlFiles = new("SQL scripts") { Patterns = ["*.sql"] };

    public MainWindow() : this(new WorkspaceViewModel()) { }

    public MainWindow(WorkspaceViewModel workspace, bool startInDemo = false)
    {
        InitializeComponent();
        this.workspace = workspace;
        DataContext = workspace;
        workspace.PropertyChanged += OnWorkspaceChanged;
        workspace.Tabs.CollectionChanged += OnTabsChanged;
        ShowSelectedTab();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            await workspace.InitializeAsync();
            if (startInDemo && workspace.SelectedTab is { } tab) await tab.StartDemoAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            workspace.PropertyChanged -= OnWorkspaceChanged;
            workspace.Tabs.CollectionChanged -= OnTabsChanged;
            foreach (var view in views.Values) DetachView(view);
            views.Clear();
        };
    }

    public QueryTabView? ActiveQueryView => QueryHost.Content as QueryTabView;

    private QueryTabView GetView(QueryTabViewModel tab)
    {
        if (views.TryGetValue(tab, out var existing)) return existing;
        var view = new QueryTabView(tab);
        view.OpenSqlRequested += OnOpenSqlRequested;
        views.Add(tab, view);
        return view;
    }

    private void ShowSelectedTab()
    {
        QueryHost.Content = workspace.SelectedTab is { } tab ? GetView(tab) : null;
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(workspace.SelectedTab)) ShowSelectedTab();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var tab in views.Keys.Where(tab => !workspace.Tabs.Contains(tab)).ToArray())
        {
            DetachView(views[tab]);
            views.Remove(tab);
        }
    }

    private void DetachView(QueryTabView view)
    { view.OpenSqlRequested -= OnOpenSqlRequested; view.Detach(); }

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (!workspace.CanManageTabs || closeWindowPending || filePickerOpen) return;
        if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (args.Key == Key.T)
            { args.Handled = true; workspace.NewTab(); return; }
            if (args.Key == Key.W && workspace.SelectedTab is { } tab)
            { args.Handled = true; await CloseTabAsync(tab); return; }
            if (args.Key == Key.Tab)
            { args.Handled = true; workspace.SelectNextTab(args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); return; }
        }
        if (ActiveQueryView is { } view) await view.HandleShortcutAsync(args);
    }

    private async void OnCloseTab(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Button { Tag: QueryTabViewModel tab }) await CloseTabAsync(tab);
    }

    private Task<bool> CloseTabAsync(QueryTabViewModel tab) => workspace.TryCloseTabAsync(tab, ConfirmTabCloseAsync);

    private async Task<bool> ConfirmTabCloseAsync(QueryTabViewModel tab)
    {
        var choice = await new CloseTabDialog(tab).ShowDialog<CloseTabChoice>(this);
        return choice switch
        {
            CloseTabChoice.Discard => true,
            CloseTabChoice.Save => await GetView(tab).SaveSqlAsync(this),
            _ => false
        };
    }

    private async void OnOpenSqlRequested(object? sender, EventArgs args) => await OpenSqlAsync();

    private async Task OpenSqlAsync()
    {
        if (filePickerOpen || !workspace.CanManageTabs) return;
        filePickerOpen = true;
        // Capture connection settings from the tab that invoked the picker.
        var source = workspace.SelectedTab;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open SQL scripts in new tabs", AllowMultiple = true,
                FileTypeFilter = [SqlFiles, FilePickerFileTypes.All]
            });
            foreach (var file in files)
            {
                await using var input = await file.OpenReadAsync();
                using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync();
                if (!workspace.CanManageTabs) return;
                workspace.NewTab(text, file.Name, source);
            }
        }
        catch (Exception error) { workspace.SelectedTab?.ReportError(error); }
        finally { filePickerOpen = false; }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        if (closeWindowPending || filePickerOpen) return;
        closeWindowPending = true;
        try
        {
            if (!await workspace.TryCloseAllAsync(ConfirmTabCloseAsync)) return;
            allowClose = true;
            Close();
        }
        catch (Exception error) { workspace.SelectedTab?.ReportError(error); }
        finally { closeWindowPending = false; }
    }
}
