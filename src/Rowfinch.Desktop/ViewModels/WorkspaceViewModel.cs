using System.Collections.ObjectModel;
using Rowfinch.Core;
using Rowfinch.Infrastructure;

namespace Rowfinch.Desktop.ViewModels;

/// <summary>Coordinates documents; SQL execution and connection state belong to each tab.</summary>
public sealed class WorkspaceViewModel : ObservableModel, IAsyncDisposable
{
    private readonly ConnectionCatalog catalog;
    private readonly Func<IQuerySession> sessionFactory;
    private readonly ICredentialStore credentialStore;
    private readonly HashSet<QueryTabViewModel> closingTabs = [];
    private QueryTabViewModel? selectedTab;
    private int nextDocumentNumber;
    private bool closingAll;
    private bool disposed;

    public WorkspaceViewModel(IProfileStore? profileStore = null, Func<IQuerySession>? sessionFactory = null, ICredentialStore? credentialStore = null)
    {
        catalog = new ConnectionCatalog(profileStore ?? new JsonProfileStore());
        this.sessionFactory = sessionFactory ?? (() => new SqlQuerySession());
        this.credentialStore = credentialStore ?? new LibSecretCredentialStore();
        NewTabCommand = new ActionCommand(_ => NewTab(), () => CanManageTabs);
        var first = CreateTab();
        first.SetDocument(first.Sql, $"Query {nextDocumentNumber}.sql");
    }

    public ObservableCollection<QueryTabViewModel> Tabs { get; } = [];
    public ObservableCollection<ConnectionProfile> Profiles => catalog.Profiles;
    public ActionCommand NewTabCommand { get; }
    public bool CanManageTabs => !closingAll && !disposed;
    public QueryTabViewModel? SelectedTab
    {
        get => selectedTab;
        set
        {
            if (value is not null && !Tabs.Contains(value)) return;
            if (value is null && Tabs.Count > 0) return;
            Set(ref selectedTab, value);
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            await catalog.InitializeAsync();
            if (SelectedTab is { IsConnected: false, SelectedProfile: null } tab && Profiles.Count > 0 && string.IsNullOrEmpty(tab.Server))
                tab.ConnectionDetailsExpanded = false;
        }
        catch (Exception error) { SelectedTab?.ReportError(error); }
    }

    public QueryTabViewModel NewTab(string text = "", string? documentName = null, QueryTabViewModel? connectionSource = null)
    {
        if (!CanManageTabs) throw new InvalidOperationException("The workspace is closing.");
        return CreateDocument(connectionSource ?? SelectedTab, text, documentName, isSaved: true);
    }

    private QueryTabViewModel CreateDocument(QueryTabViewModel? source, string text, string? documentName, bool isSaved)
    {
        var tab = CreateTab();
        tab.SetDocument(text, documentName ?? $"Query {nextDocumentNumber}.sql", isSaved);
        if (source is not null) tab.InitializeConnectionFrom(source);
        return tab;
    }

    private void OnDocumentRequested(QueryTabViewModel source, string text, string documentName)
    {
        if (!CanManageTabs || source.IsClosing || !Tabs.Contains(source)) return;
        CreateDocument(source, text, documentName, isSaved: false);
    }

    private QueryTabViewModel CreateTab()
    {
        nextDocumentNumber++;
        var tab = new QueryTabViewModel(sessionFactory: sessionFactory, catalog: catalog, credentialStore: credentialStore);
        tab.DocumentRequested += OnDocumentRequested;
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public void SelectNextTab(int direction)
    {
        if (Tabs.Count == 0 || SelectedTab is null) return;
        var index = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index + direction % Tabs.Count + Tabs.Count) % Tabs.Count];
    }

    public async Task<bool> TryCloseTabAsync(QueryTabViewModel tab, Func<QueryTabViewModel, Task<bool>> confirm)
    {
        if (!CanManageTabs || !Tabs.Contains(tab) || !closingTabs.Add(tab)) return false;
        tab.IsClosing = true;
        try
        {
            if (tab.RequiresCloseConfirmation && !await confirm(tab)) return false;
            await tab.DisposeAsync();
            var index = Tabs.IndexOf(tab);
            var wasSelected = SelectedTab == tab;
            tab.DocumentRequested -= OnDocumentRequested;
            Tabs.Remove(tab);
            if (Tabs.Count == 0) NewTab();
            else if (wasSelected) SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
            return true;
        }
        catch (Exception error) { tab.ReportError(error); return false; }
        finally { closingTabs.Remove(tab); tab.IsClosing = false; }
    }

    public async Task<bool> TryCloseAllAsync(Func<QueryTabViewModel, Task<bool>> confirm)
    {
        if (!CanManageTabs || closingTabs.Count > 0) return false;
        SetClosingAll(true);
        var tabs = Tabs.ToArray();
        foreach (var tab in tabs) tab.IsClosing = true;
        try
        {
            // Obtain every decision before cancelling any query or closing any session.
            foreach (var tab in tabs)
                if (tab.RequiresCloseConfirmation && !await confirm(tab)) return false;
            await Task.WhenAll(tabs.Select(tab => tab.DisposeAsync().AsTask()));
            foreach (var tab in tabs) tab.DocumentRequested -= OnDocumentRequested;
            Tabs.Clear(); SelectedTab = null;
            disposed = true;
            return true;
        }
        catch (Exception error) { SelectedTab?.ReportError(error); return false; }
        finally
        {
            foreach (var tab in tabs) tab.IsClosing = false;
            SetClosingAll(false);
        }
    }

    private void SetClosingAll(bool value)
    { closingAll = value; Notify(nameof(CanManageTabs)); NewTabCommand.Refresh(); }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        Notify(nameof(CanManageTabs)); NewTabCommand.Refresh();
        foreach (var tab in Tabs) tab.DocumentRequested -= OnDocumentRequested;
        await Task.WhenAll(Tabs.Select(tab => tab.DisposeAsync().AsTask()));
    }
}
