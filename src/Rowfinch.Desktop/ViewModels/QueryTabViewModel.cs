using System.Collections.ObjectModel;
using Rowfinch.Core;
using Rowfinch.Infrastructure;

namespace Rowfinch.Desktop.ViewModels;

public sealed class QueryTabViewModel : ObservableModel, IAsyncDisposable
{
    private readonly ConnectionCatalog catalog;
    private readonly Func<IQuerySession> sessionFactory;
    private readonly ICredentialStore credentialStore;
    private Guid draftProfileId = Guid.NewGuid();
    private bool rememberPassword;
    private string keyringMessage = "Passwords are session-only unless you choose to remember them.";
    private IQuerySession? session;
    // Keep credentials private and in memory while connected tabs use this target.
    // This class has no generated ToString that could expose its password.
    private sealed class LiveConnection(ConnectionProfile profile, string? password)
    {
        public ConnectionProfile Profile { get; } = profile;
        public string? Password { get; } = profile.Authentication == AuthenticationKind.SqlPassword ? password : null;
    }
    private LiveConnection? liveConnection;
    private LiveConnection? pendingConnection;
    private CancellationTokenSource? operation;
    private TaskCompletionSource? idleCompletion;
    private bool disposed;
    private bool isClosing;
    private readonly List<ObjectBrowserNode> allObjects = [];
    private ConnectionProfile? selectedProfile;
    private QueryResultSet? selectedResult;
    private string profileName = "My Azure database";
    private string server = "";
    private string database = "";
    private string userName = "";
    private string password = "";
    private int authenticationIndex;
    private string objectFilter = "";
    private int sidebarTabIndex;
    private bool connectionDetailsExpanded = true;
    private bool showTables, showViews, showProcedures, showFunctions;
    private string sql = """
        -- Welcome to Rowfinch.
        -- Connect to your database, or choose Try demo to explore the workspace.
        -- Ctrl+Enter runs the selection or the entire script. F5 runs it too.

        SELECT TOP (100)
            s.name AS [Schema],
            t.name AS [Table],
            t.create_date AS [Created]
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        ORDER BY s.name, t.name;
        """;
    private string status = "Ready to connect";
    private string messages = "Connect to an Azure SQL database to begin.\nRemembered passwords are stored only in your desktop keyring.";
    private string connectionLabel = "Not connected";
    private string documentName = "Query 1.sql";
    private bool isBusy;
    private bool isDemo;
    private bool isDirty;
    private int? transactionCount = 0;
    private int rowLimit = 1000;
    private int timeoutSeconds = 60;
    private int outputTabIndex;

    public QueryTabViewModel(IProfileStore? profileStore = null, Func<IQuerySession>? sessionFactory = null, ConnectionCatalog? catalog = null, ICredentialStore? credentialStore = null)
    {
        this.catalog = catalog ?? new ConnectionCatalog(profileStore ?? new JsonProfileStore());
        this.sessionFactory = sessionFactory ?? (() => new SqlQuerySession());
        this.credentialStore = credentialStore ?? new LibSecretCredentialStore();
        this.catalog.PasswordForgotten += OnPasswordForgotten;
        ConnectCommand = new AsyncCommand(_ => ConnectAsync(), () => CanEditConnection);
        DemoCommand = new AsyncCommand(_ => StartDemoAsync(), () => CanEditConnection);
        DisconnectCommand = new AsyncCommand(_ => DisconnectAsync(), () => CanExecute);
        SaveProfileCommand = new AsyncCommand(_ => SaveProfileAsync(), () => !IsBusy && !IsClosing);
        ForgetPasswordCommand = new AsyncCommand(_ => ForgetPasswordAsync(), () => CanForgetPassword);
        NewProfileCommand = new ActionCommand(_ => NewProfile(), () => CanEditConnection);
        RefreshObjectsCommand = new AsyncCommand(_ => RefreshObjectsAsync(), () => CanExecute);
        CancelCommand = new ActionCommand(_ => Cancel(), () => IsBusy);
        CommitCommand = new AsyncCommand(_ => ExecuteAsync("COMMIT TRANSACTION;"), () => CanExecute && !IsDemo && transactionCount > 0);
        RollbackCommand = new AsyncCommand(_ => ExecuteAsync("ROLLBACK TRANSACTION;"), () => CanExecute && !IsDemo && transactionCount != 0);
    }

    public ObservableCollection<ConnectionProfile> Profiles => catalog.Profiles;
    public ObservableCollection<ObjectBrowserNode> Objects { get; } = [];
    public ObservableCollection<QueryResultSet> Results { get; } = [];
    public string[] AuthenticationOptions { get; } = ["SQL user and password", "Microsoft Entra — browser sign-in"];
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DemoCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand SaveProfileCommand { get; }
    public AsyncCommand ForgetPasswordCommand { get; }
    public AsyncCommand RefreshObjectsCommand { get; }
    public AsyncCommand CommitCommand { get; }
    public AsyncCommand RollbackCommand { get; }
    public ActionCommand NewProfileCommand { get; }
    public ActionCommand CancelCommand { get; }
    public bool IsConnected => session?.IsConnected == true;
    public Task ConnectionReady { get; private set; } = Task.CompletedTask;
    public bool CanExecute => IsConnected && !IsBusy && !IsClosing;
    public bool CanEditConnection => !IsBusy && !IsConnected && !IsClosing;
    public bool IsClosing
    {
        get => isClosing;
        internal set { if (Set(ref isClosing, value)) RefreshState(); }
    }
    public int OutputTabIndex { get => outputTabIndex; set => Set(ref outputTabIndex, value); }
    public string SessionLabel => IsBusy ? "Working…" : IsDemo ? "Demo" : IsConnected ? Database : "Disconnected";
    public bool RequiresCloseConfirmation => IsDirty || IsBusy || IsConnected && TransactionCount != 0;
    public IReadOnlyList<string> CloseConcerns
    {
        get
        {
            var concerns = new List<string>();
            if (IsDirty) concerns.Add("This tab contains unsaved SQL.");
            if (IsBusy) concerns.Add("Its running operation will be cancelled.");
            if (IsConnected && (IsBusy || TransactionCount != 0))
                concerns.Add("Its open or unverified transaction will be rolled back when the session closes.");
            return concerns;
        }
    }
    public bool IsSqlAuthentication => AuthenticationIndex == 0;
    public bool CanForgetPassword => !IsBusy && !IsClosing && selectedProfile is not null;
    public string PasswordPlaceholder => RememberPassword ? "Password, or leave blank to use keyring" : "Password (session only)";
    public string KeyringMessage { get => keyringMessage; private set => Set(ref keyringMessage, value); }
    public bool RememberPassword
    {
        get => rememberPassword;
        set
        {
            if (!Set(ref rememberPassword, value)) return;
            Notify(nameof(PasswordPlaceholder));
            KeyringMessage = value
                ? "After a successful connection, save this profile and its password in your desktop keyring."
                : "Use a session-only password. Forget saved password removes an existing keyring entry.";
        }
    }
    public bool IsDemo { get => isDemo; private set => Set(ref isDemo, value); }
    public bool IsBusy
    {
        get => isBusy;
        private set { Set(ref isBusy, value); RefreshState(); }
    }
    public string ProfileName { get => profileName; set => Set(ref profileName, value); }
    public string Server { get => server; set => Set(ref server, value); }
    public string Database { get => database; set => Set(ref database, value); }
    public string UserName { get => userName; set => Set(ref userName, value); }
    public string Password { get => password; set => Set(ref password, value); }
    public int AuthenticationIndex
    {
        get => authenticationIndex;
        set
        {
            if (!Set(ref authenticationIndex, value)) return;
            if (value != 0) { RememberPassword = false; Password = ""; }
            Notify(nameof(IsSqlAuthentication));
        }
    }
    public ConnectionProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!CanEditConnection || !Set(ref selectedProfile, value)) return;
            RefreshCredentialActions();
            if (value is null) return;
            ConnectionDetailsExpanded = true;
            ProfileName = value.Name; Server = value.Server; Database = value.Database;
            UserName = value.UserName; AuthenticationIndex = (int)value.Authentication; Password = "";
            RememberPassword = value.RememberPassword && IsSqlAuthentication;
            KeyringMessage = RememberPassword ? "Leave the password blank to request it from your desktop keyring when connecting." : "Passwords are session-only unless you choose to remember them.";
        }
    }
    public string Sql
    {
        get => sql;
        set { if (Set(ref sql, value)) { IsDirty = true; } }
    }
    public string DocumentName { get => documentName; private set { Set(ref documentName, value); Notify(nameof(DocumentLabel)); } }
    public string DocumentLabel => DocumentName + (IsDirty ? " •" : "");
    public bool IsDirty { get => isDirty; private set { Set(ref isDirty, value); Notify(nameof(DocumentLabel)); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Messages { get => messages; private set => Set(ref messages, value); }
    public string ConnectionLabel { get => connectionLabel; private set => Set(ref connectionLabel, value); }
    public string TransactionLabel => transactionCount switch { null => "Transaction state unknown", 0 => "No open transaction", _ => $"Open transactions: {transactionCount}" };
    public int? TransactionCount => transactionCount;
    public int RowLimit { get => rowLimit; set => Set(ref rowLimit, Math.Clamp(value, 1, 100_000)); }
    public int TimeoutSeconds { get => timeoutSeconds; set => Set(ref timeoutSeconds, Math.Clamp(value, 1, 3600)); }
    public string ObjectFilter { get => objectFilter; set { Set(ref objectFilter, value); FilterObjects(); } }
    public int SidebarTabIndex { get => sidebarTabIndex; set => Set(ref sidebarTabIndex, Math.Clamp(value, 0, 1)); }
    public bool ConnectionDetailsExpanded { get => connectionDetailsExpanded; set => Set(ref connectionDetailsExpanded, value); }
    public bool ShowTables { get => showTables; set => SetObjectType(ref showTables, value, nameof(ShowTables)); }
    public bool ShowViews { get => showViews; set => SetObjectType(ref showViews, value, nameof(ShowViews)); }
    public bool ShowProcedures { get => showProcedures; set => SetObjectType(ref showProcedures, value, nameof(ShowProcedures)); }
    public bool ShowFunctions { get => showFunctions; set => SetObjectType(ref showFunctions, value, nameof(ShowFunctions)); }
    public bool AllObjectTypes
    {
        get => !showTables && !showViews && !showProcedures && !showFunctions;
        set
        {
            if (!value || AllObjectTypes) return;
            showTables = showViews = showProcedures = showFunctions = false;
            Notify(nameof(ShowTables)); Notify(nameof(ShowViews)); Notify(nameof(ShowProcedures)); Notify(nameof(ShowFunctions));
            Notify(nameof(AllObjectTypes)); FilterObjects();
        }
    }
    public string ObjectCountLabel => IsConnected ? $"{Objects.Count:N0} of {allObjects.Count:N0} objects" : "Connect to browse database objects.";

    private void SetObjectType(ref bool field, bool value, string property)
    {
        if (!Set(ref field, value, property)) return;
        Notify(nameof(AllObjectTypes)); FilterObjects();
    }

    public QueryResultSet? SelectedResult
    {
        get => selectedResult;
        set { Set(ref selectedResult, value); Notify(nameof(ResultSummary)); Notify(nameof(CanExportResult)); }
    }
    public bool CanExportResult => !IsBusy && !IsClosing && SelectedResult is not null;
    public string ResultSummary => SelectedResult is { } result
        ? $"{result.Rows.Count:N0} rows · {result.Columns.Count} columns" + (result.IsTruncated ? " · display limited" : "")
        : "Results appear here after you run a query";

    public async Task<IAssistantDatabaseSession> OpenAssistantDatabaseAsync(bool allowQueries, CancellationToken token)
    {
        if (!IsConnected || IsClosing || disposed) throw new InvalidOperationException("Choose a connected database tab first.");
        var active = liveConnection;
        IQuerySession candidate = IsDemo ? new DemoQuerySession() : sessionFactory();
        var target = IsDemo ? "Demo · no database" : active is not null
            ? $"{active.Profile.Database} · {active.Profile.Server} · {active.Profile.UserName}"
            : throw new InvalidOperationException("Reconnect the database before enabling assistant access.");
        var assistant = new AssistantDatabaseSession(candidate, target);
        try
        {
            if (!IsDemo) await candidate.OpenAsync(active!.Profile, active.Password, token);
            if (allowQueries) await assistant.EnableQueriesAsync(token);
            token.ThrowIfCancellationRequested();
            if (!IsConnected || IsClosing || disposed || !IsDemo && !ReferenceEquals(active, liveConnection))
                throw new InvalidOperationException("The source connection changed. Enable access again for the intended database.");
            return assistant;
        }
        catch { await assistant.DisposeAsync(); throw; }
    }

    public Task ConnectAsync() => ConnectAsync(null);

    private Task ConnectAsync(LiveConnection? inherited) => RunOperationAsync(async token =>
    {
        if (IsConnected) return;
        var profile = inherited is null ? CurrentProfile() : inherited.Profile with { RememberPassword = RememberPassword };
        profile.Validate();
        var credentialVersion = catalog.GetCredentialVersion(profile.Id);
        string? connectionPassword = inherited is null ? Password : inherited.Password;
        if (inherited is null && profile.Authentication == AuthenticationKind.SqlPassword && profile.RememberPassword && string.IsNullOrEmpty(connectionPassword))
        {
            try { connectionPassword = await credentialStore.GetAsync(CredentialIdentity.FromProfile(profile), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                KeyringMessage = SafeKeyringMessage(error);
                throw new InvalidOperationException(KeyringMessage + " Enter your password to connect without retrieving it.");
            }
            if (string.IsNullOrEmpty(connectionPassword))
            {
                KeyringMessage = "No saved password matches these connection settings. Enter your password to connect.";
                throw new InvalidOperationException(KeyringMessage);
            }
        }
        if (session is not null) { await session.DisposeAsync(); session = null; }
        liveConnection = null;
        allObjects.Clear(); Objects.Clear(); Notify(nameof(ObjectCountLabel));
        var candidate = sessionFactory();
        try { await candidate.OpenAsync(profile, connectionPassword, token); token.ThrowIfCancellationRequested(); }
        catch { await candidate.DisposeAsync(); throw; }
        session = candidate;
        liveConnection = inherited ?? new LiveConnection(profile, connectionPassword);
        IsDemo = false;
        transactionCount = 0;
        Password = "";
        ConnectionLabel = $"{profile.Database} · {profile.Server}";
        AppendMessage($"Connected to {profile.Database}. Encryption and certificate validation are enabled.");
        Status = "Connected";
        if (inherited is null && profile.Authentication == AuthenticationKind.SqlPassword && profile.RememberPassword)
        {
            try
            {
                await catalog.WithCredentialLockAsync(async () =>
                {
                    if (credentialVersion != catalog.GetCredentialVersion(profile.Id))
                    {
                        RememberPassword = false;
                        KeyringMessage = "Password was not saved because this profile was forgotten in another tab.";
                        return;
                    }
                    await SaveProfileSnapshotAsync(profile, token);
                    await credentialStore.StoreAsync(CredentialIdentity.FromProfile(profile), $"Rowfinch — {profile.Name}", connectionPassword!, token);
                    KeyringMessage = "Password saved in your desktop keyring.";
                    AppendMessage(KeyringMessage);
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { KeyringMessage = "Password saving was cancelled; storage was not confirmed. The database connection is open."; throw; }
            catch (Exception error)
            {
                KeyringMessage = "Connected, but password storage could not be confirmed. " + SafeKeyringMessage(error);
                AppendMessage(KeyringMessage);
                Status = "Connected · password storage failed";
            }
        }
        else if (inherited is not null && profile.Authentication == AuthenticationKind.SqlPassword)
            KeyringMessage = "Connected using the active connection's session credentials.";
        else if (profile.Authentication == AuthenticationKind.SqlPassword)
            KeyringMessage = "Connected with a session-only password.";
        ConnectionDetailsExpanded = false; SidebarTabIndex = 1;
        try { await LoadObjectsAsync(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        { AppendMessage($"Connected, but object discovery failed: {error.Message}"); }
    }, "Connecting…");

    public Task StartDemoAsync() => StartDemoAsync(true);

    private Task StartDemoAsync(bool loadSampleResults) => RunOperationAsync(async token =>
    {
        if (IsConnected) return;
        session = new DemoQuerySession();
        liveConnection = null;
        IsDemo = true;
        ConnectionLabel = "Demo workspace · no database connection";
        transactionCount = 0;
        await LoadObjectsAsync(token);
        if (loadSampleResults)
        {
            var execution = await session.ExecuteAsync(Sql, RowLimit, TimeoutSeconds, null, token);
            SetResults(execution.Results);
        }
        AppendMessage("Demo mode: results are sample data. SQL text is not executed.");
        Status = "Demo ready";
        ConnectionDetailsExpanded = false; SidebarTabIndex = 1;
    }, "Opening demo…");

    public Task DisconnectAsync() => RunOperationAsync(async _ =>
    {
        if (session is not null) await session.DisposeAsync();
        session = null;
        liveConnection = null;
        ConnectionReady = Task.CompletedTask;
        IsDemo = false;
        allObjects.Clear(); Objects.Clear();
        ConnectionDetailsExpanded = true; SidebarTabIndex = 0; Notify(nameof(ObjectCountLabel));
        ConnectionLabel = "Not connected";
        transactionCount = 0;
        Status = "Disconnected";
        AppendMessage("Disconnected. Any uncommitted transaction in that session was rolled back.");
    }, "Disconnecting…");

    public Task ExecuteAsync(string? selection = null) => RunOperationAsync(async token =>
    {
        if (session is null || !session.IsConnected) throw new InvalidOperationException("Connect to a database first, or choose Try demo.");
        var query = string.IsNullOrWhiteSpace(selection) ? Sql : selection;
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter SQL to execute.");
        // Invalid SQLCMD directives and GO counts must fail before any SQL is sent.
        SqlBatchParser.Split(query);
        Results.Clear(); SelectedResult = null;
        Messages = "";
        // Progress<T> captures Avalonia's UI synchronization context.
        var progress = new Progress<string>(AppendMessage);
        try
        {
            var execution = await session.ExecuteAsync(query, RowLimit, TimeoutSeconds, progress, token);
            SetResults(execution.Results);
            transactionCount = execution.TransactionCount;
            Status = $"Completed in {execution.Elapsed.TotalSeconds:0.00}s";
            AppendMessage(Status + (IsDemo ? " (demo data)." : "."));
        }
        finally
        {
            // Cancellation/errors can leave a transaction open; never infer a rollback.
            transactionCount = null;
            if (session.IsConnected)
            {
                using var stateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { transactionCount = await session.GetTransactionCountAsync(stateTimeout.Token); }
                catch (Exception error) { AppendMessage($"Could not refresh transaction state: {error.Message}"); }
            }
            else ConnectionLabel = "Connection lost · reconnect to continue";
        }
    }, "Running query…");

    public Task RefreshObjectsAsync() => RunOperationAsync(LoadObjectsAsync, "Refreshing objects…", "Objects refreshed");

    public Task SaveProfileAsync() => RunOperationAsync(async token =>
    {
        var profile = CurrentProfile();
        profile.Validate();
        await SaveProfileSnapshotAsync(profile, token);
        Status = "Connection saved";
        AppendMessage("Connection settings saved. Passwords and access tokens are never included in the profile file.");
        if (profile.RememberPassword) KeyringMessage = "Preference saved. A password is stored only after a successful connection.";
    }, "Saving connection…");

    public void NewProfile()
    {
        if (!CanEditConnection) return;
        SidebarTabIndex = 0; ConnectionDetailsExpanded = true;
        selectedProfile = null; Notify(nameof(SelectedProfile));
        draftProfileId = Guid.NewGuid(); RememberPassword = false; RefreshCredentialActions();
        ProfileName = "New connection"; Server = ""; Database = ""; UserName = ""; Password = ""; AuthenticationIndex = 0;
    }

    public void SetDocument(string text, string name, bool isSaved = true)
    { Sql = text; DocumentName = name; IsDirty = !isSaved; }
    public void MarkSaved(string name) { DocumentName = name; IsDirty = false; }
    public void Cancel()
    {
        if (operation is not { } current) return;
        // Cancellation may complete the operation synchronously; its final status wins.
        Status = "Cancelling…";
        current.Cancel();
    }
    public void ReportError(Exception error) { Status = "Action failed"; AppendMessage(error.Message); OutputTabIndex = 1; }

    public void CopyConnectionSettingsFrom(QueryTabViewModel source)
    {
        if (!CanEditConnection) return;
        var inherited = source.IsConnected ? source.liveConnection : source.pendingConnection;
        var profile = inherited is { } active
            ? active.Profile with { RememberPassword = source.RememberPassword } : source.CurrentProfile();
        selectedProfile = source.selectedProfile;
        if (source.IsConnected || inherited is not null) draftProfileId = profile.Id;
        Notify(nameof(SelectedProfile));
        ProfileName = profile.Name; Server = profile.Server; Database = profile.Database;
        UserName = profile.UserName; AuthenticationIndex = (int)profile.Authentication;
        Password = "";
        RememberPassword = profile.RememberPassword && IsSqlAuthentication;
        RefreshCredentialActions();
    }

    internal void InitializeConnectionFrom(QueryTabViewModel source)
    {
        if (!CanEditConnection) return;
        CopyConnectionSettingsFrom(source);
        if (source.IsClosing || source.disposed) return;
        if (source.IsConnected && source.IsDemo) ConnectionReady = StartDemoAsync(false);
        else if ((source.IsConnected ? source.liveConnection : source.pendingConnection) is { } active)
            ConnectionReady = ConnectInheritedAsync(active);
    }

    private async Task ConnectInheritedAsync(LiveConnection inherited)
    {
        pendingConnection = inherited;
        try { await ConnectAsync(inherited); }
        finally { pendingConnection = null; }
    }

    public Task ForgetPasswordAsync() => RunOperationAsync(token => catalog.WithCredentialLockAsync(async () =>
    {
        if (selectedProfile is not { } profile) throw new InvalidOperationException("Select a saved connection profile first.");
        try { await credentialStore.DeleteAsync(profile.Id, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            KeyringMessage = "Password removal was not confirmed. " + SafeKeyringMessage(error);
            throw new InvalidOperationException(KeyringMessage);
        }
        catalog.NotifyPasswordForgotten(profile.Id);
        try { await SaveProfileSnapshotAsync(profile with { RememberPassword = false }, token); }
        catch (Exception)
        { AppendMessage("The saved password was removed, but its profile preference could not be updated on disk."); }
        KeyringMessage = "Saved password removed from the desktop keyring. Existing database sessions remain connected.";
        AppendMessage(KeyringMessage);
        Status = "Password forgotten";
    }, token), "Removing saved password…");

    private async Task SaveProfileSnapshotAsync(ConnectionProfile profile, CancellationToken token)
    {
        await catalog.SaveAsync(profile, token);
        selectedProfile = profile;
        Notify(nameof(SelectedProfile));
        RefreshCredentialActions();
    }

    private void OnPasswordForgotten(Guid profileId)
    {
        if (selectedProfile?.Id != profileId) return;
        RememberPassword = false; Password = "";
        KeyringMessage = "The saved password for this profile was removed from the keyring.";
    }

    private static string SafeKeyringMessage(Exception error) => error is CredentialStoreException keyring
        ? CredentialStoreException.MessageFor(keyring.Failure)
        : CredentialStoreException.MessageFor(CredentialStoreFailure.Failed);

    private void RefreshCredentialActions()
    { Notify(nameof(CanForgetPassword)); ForgetPasswordCommand.Refresh(); }

    public async Task RunOperationAsync(Func<CancellationToken, Task> action, string busyStatus, string? successStatus = null)
    {
        if (IsBusy || IsClosing || disposed) return;
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idleCompletion = completion;
        operation = cancellation;
        IsBusy = true;
        Status = busyStatus;
        try
        {
            await action(cancellation.Token);
            if (successStatus is not null) Status = successStatus;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Status = "Cancelled"; AppendMessage("Operation cancelled. Check transaction state before continuing."); OutputTabIndex = 1; }
        catch (Exception error)
        {
            if (cancellation.IsCancellationRequested) { Status = "Cancelled"; AppendMessage("Operation cancelled."); OutputTabIndex = 1; }
            else ReportError(error);
        }
        finally
        {
            operation = null; IsBusy = false;
            idleCompletion = null; completion.TrySetResult();
        }
    }

    private ConnectionProfile CurrentProfile() => new()
    {
        Id = selectedProfile?.Id ?? draftProfileId, Name = string.IsNullOrWhiteSpace(ProfileName) ? Database.Trim() : ProfileName.Trim(),
        Server = Server.Trim(), Database = Database.Trim(), UserName = UserName.Trim(), Authentication = (AuthenticationKind)AuthenticationIndex,
        RememberPassword = IsSqlAuthentication && RememberPassword
    };

    public event Action<QueryTabViewModel, string, string>? DocumentRequested;

    private DatabaseObject RequireObject(ObjectBrowserNode node)
    {
        if (!allObjects.Contains(node) || node.Object is not { } item)
            throw new InvalidOperationException("This object list has changed. Choose the object again.");
        return item;
    }

    private IObjectMetadataSession Metadata => session as IObjectMetadataSession
        ?? throw new InvalidOperationException("Object metadata is unavailable for this session.");

    private async Task<IReadOnlyList<DatabaseColumn>?> LoadColumnsAsync(ObjectBrowserNode node)
    {
        IReadOnlyList<DatabaseColumn>? columns = null;
        await RunOperationAsync(async token =>
        {
            var item = RequireObject(node);
            var loaded = await Metadata.GetColumnsAsync(item, token);
            token.ThrowIfCancellationRequested();
            columns = loaded;
        }, "Loading columns…", "Columns loaded");
        return columns;
    }

    public Task RefreshObjectAsync(ObjectBrowserNode node) => CanExecute
        ? node.LoadColumnsAsync(refresh: true) : Task.CompletedTask;

    public async Task PreviewRowsAsync(ObjectBrowserNode node)
    {
        if (!CanExecute) return;
        try { await ExecuteAsync(ObjectSql.Preview(RequireObject(node))); }
        catch (Exception error) { ReportError(error); }
    }

    public Task GenerateSelectAsync(ObjectBrowserNode node) => GenerateScriptAsync(node, ObjectScriptKind.Select);
    public Task GenerateInsertAsync(ObjectBrowserNode node) => GenerateScriptAsync(node, ObjectScriptKind.Insert);
    public Task GenerateUpdateAsync(ObjectBrowserNode node) => GenerateScriptAsync(node, ObjectScriptKind.Update);

    private Task GenerateScriptAsync(ObjectBrowserNode node, ObjectScriptKind kind) => RunOperationAsync(async token =>
    {
        var item = RequireObject(node);
        if (!item.HasColumns) throw new ArgumentException("SQL templates are available for tables and views.");
        if (node.Columns is null)
        {
            var loaded = await Metadata.GetColumnsAsync(item, token);
            token.ThrowIfCancellationRequested();
            node.SetColumns(loaded);
        }
        var selected = node.SelectedColumns;
        var text = kind switch
        {
            ObjectScriptKind.Select => ObjectSql.Select(item, selected),
            ObjectScriptKind.Insert => ObjectSql.Insert(item, selected),
            ObjectScriptKind.Update => ObjectSql.Update(item, node.Columns!, selected),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        token.ThrowIfCancellationRequested();
        DocumentRequested?.Invoke(this, text, $"{item.DisplayName}.{kind.ToString().ToLowerInvariant()}.sql");
    }, $"Generating {kind.ToString().ToUpperInvariant()}…", "SQL opened in a new tab");

    public Task OpenDefinitionAsync(ObjectBrowserNode node) => RunOperationAsync(async token =>
    {
        var item = RequireObject(node);
        if (!item.HasDefinition) throw new ArgumentException("This object has no SQL module definition.");
        var definition = await Metadata.GetDefinitionAsync(item, token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(definition)) throw new InvalidOperationException("Definition unavailable. Check VIEW DEFINITION permission.");
        DocumentRequested?.Invoke(this, definition, $"{item.DisplayName}.sql");
    }, "Loading definition…", "Definition opened in a new tab");

    private async Task LoadObjectsAsync(CancellationToken token)
    {
        if (session is null) return;
        var objects = await session.GetObjectsAsync(token);
        allObjects.Clear(); allObjects.AddRange(objects.Select(item => new ObjectBrowserNode(item, LoadColumnsAsync))); FilterObjects();
    }
    private void FilterObjects()
    {
        Objects.Clear();
        var filter = ObjectFilter.Trim();
        foreach (var item in allObjects.Where(o => o.Object!.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
            (AllObjectTypes || (o.Object.Kind switch
            {
                "Table" => ShowTables, "View" => ShowViews, "Procedure" => ShowProcedures, "Function" => ShowFunctions, _ => false
            })))) Objects.Add(item);
        Notify(nameof(ObjectCountLabel));
    }
    private void SetResults(IReadOnlyList<QueryResultSet> results)
    {
        Results.Clear();
        foreach (var result in results) Results.Add(result);
        SelectedResult = Results.FirstOrDefault();
        OutputTabIndex = 0;
    }
    private void AppendMessage(string text)
    {
        // PRINT-heavy scripts must not grow the messages pane indefinitely.
        var updated = string.IsNullOrEmpty(Messages) ? text : Messages + Environment.NewLine + text;
        Messages = updated.Length > 100_000 ? "[Earlier messages omitted]\n" + updated[^90_000..] : updated;
    }
    private void RefreshState()
    {
        Notify(nameof(CanExportResult));
        Notify(nameof(IsConnected)); Notify(nameof(CanExecute)); Notify(nameof(CanEditConnection)); Notify(nameof(ObjectCountLabel));
        Notify(nameof(TransactionLabel)); Notify(nameof(TransactionCount)); Notify(nameof(SessionLabel));
        ConnectCommand.Refresh(); DemoCommand.Refresh(); DisconnectCommand.Refresh(); SaveProfileCommand.Refresh();
        NewProfileCommand.Refresh(); RefreshObjectsCommand.Refresh(); CancelCommand.Refresh(); CommitCommand.Refresh(); RollbackCommand.Refresh();
        RefreshCredentialActions();
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        Password = "";
        catalog.PasswordForgotten -= OnPasswordForgotten;
        operation?.Cancel();
        if (idleCompletion is { } completion) await completion.Task;
        if (session is not null) await session.DisposeAsync();
        session = null;
        liveConnection = null;
        ConnectionReady = Task.CompletedTask;
    }
}
