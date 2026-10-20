using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Rowfinch.Infrastructure;

namespace Rowfinch.Desktop.Views;

public partial class AssistantWindow : Window
{
    private IAssistantClient client;
    private readonly IAssistantClient chatGptClient;
    private readonly IAssistantClient claudeClient;
    private readonly bool ownsClaude;
    private bool updatingProvider;
    private readonly Dictionary<string, (SavedAssistantChat? Chat, string Draft)> providerChats = [];
    private bool IsClaude => client.ProviderId == "claude";
    private string ProviderName => IsClaude ? "Claude" : "ChatGPT";
    private string Recipient => IsClaude ? "Anthropic" : "OpenAI";
    private readonly WorkspaceViewModel workspace;
    private readonly QueryTabViewModel source;
    private readonly LocalSettingsStore settings;
    private CancellationTokenSource? operation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AssistantConnectionContextStore connectionNotesStore;
    private QueryTabViewModel? notesSource;
    private ConnectionProfile? notesConnection;
    private string? notesKey;
    private string savedNotes = "";
    private int notesRevision;
    private bool loadingNotes;
    private bool updatingNotes;
    private bool updatingAccounts;
    private bool ready;
    private string? sqlSnapshot;
    private string? schemaSnapshot;
    private QueryResultSet? rowsSnapshot;
    private string? requestError;
    private bool completedAnswer;
    private string requestedSql = "";
    private SavedAssistantChat? openedChat;
    private string? historyTarget;
    private static readonly FilePickerFileType ChatFiles = new("Rowfinch chat") { Patterns = ["*.rowfinch-chat.json"] };
    private readonly AssistantConversation conversation = new();
    private readonly ObservableCollection<AssistantChatEntry> messages = [];
    private AssistantChatEntry? latestEntry;
    private AssistantChatEntry? pendingEntry;
    private (bool Connected, string Label) sourceConnection;
    private string LatestAnswer => latestEntry?.Answer ?? "";
    private string LatestSql
    {
        get
        {
            var suggestion = completedAnswer ? SuggestedSql(LatestAnswer) : "";
            return !string.IsNullOrWhiteSpace(suggestion) ? suggestion : !string.IsNullOrWhiteSpace(requestedSql) ? requestedSql : completedAnswer ? conversation.LatestQuerySql() : "";
        }
    }
    private string? contextAccountId;
    private IAssistantDatabaseSession? database;
    private QueryTabViewModel? databaseSource;
    private AssistantQueryApprovalDialog? approvalDialog;

    public AssistantWindow() : this(new ChatGptAssistantClient(), new WorkspaceViewModel(), null, new LocalSettingsStore()) { }
    public AssistantWindow(IAssistantClient client, WorkspaceViewModel workspace, QueryTabViewModel? source, LocalSettingsStore settings, IAssistantClient? claudeClient = null)
    {
        InitializeComponent();
        this.client = chatGptClient = client; this.claudeClient = claudeClient ?? new ClaudeAssistantClient(settings); ownsClaude = claudeClient is null; this.workspace = workspace; this.source = source ?? workspace.SelectedTab!; this.settings = settings;
        connectionNotesStore = new(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "assistant-contexts.json"));
        SourceLabel.Text = $"Source: {this.source.DocumentName}. Database access is optional; each query requires approval.";
        DatabaseConnections.ItemsSource = workspace.Tabs;
        DatabaseConnections.SelectedItem = this.source;
        ChatMessages.ItemsSource = messages;
        sourceConnection = (this.source.IsConnected, this.source.ConnectionLabel);
        this.source.PropertyChanged += OnSourceConnectionChanged;
        DatabaseConnections.SelectionChanged += async (_, _) =>
        {
            if (!ready) return;
            if (database is null && openedChat is null && messages.Count > 0) ResetConversation();
            await RefreshConnectionNotesAsync();
        };
        Providers.ItemsSource = new[] { "ChatGPT", "Claude" };
        Providers.SelectedIndex = 0;
        ready = true;
        RefreshProvider();
        Models.SelectionChanged += (_, _) => RefreshButtons();
        Opened += async (_, _) => await RunAsync(async token => { await RefreshConnectionNotesAsync(); await this.client.InitializeAsync(token); await ConnectedAsync(token); });
        Closed += async (_, _) =>
        {
            lifetime.Cancel(); operation?.Cancel(); approvalDialog?.Close(false);
            this.source.PropertyChanged -= OnSourceConnectionChanged;
            if (notesSource is { } previousNotesSource) previousNotesSource.PropertyChanged -= OnNotesSourceChanged;
            notesRevision++;
            conversation.Clear(); messages.Clear(); latestEntry = pendingEntry = null;
            await RevokeDatabaseAsync();
            providerChats.Clear();
            if (ownsClaude) this.claudeClient.Dispose();
        };
        RefreshConversationInfo(); RefreshPreview(); RefreshButtons();
    }

    private void RefreshProvider()
    {
        updatingProvider = true;
        Providers.SelectedIndex = IsClaude ? 1 : 0;
        updatingProvider = false;
        SignInButton.Content = IsClaude ? "Set up API key" : "Continue with ChatGPT";
        SignOutButton.Content = IsClaude ? "Remove API key" : "Sign out";
        AddAccountButton.IsVisible = Accounts.IsVisible = !IsClaude;
        ProviderDisclosure.Text = $"Send shares this chat and selected context with {Recipient}. Provider chats stay separate; switching disables database access.";
        AllowSchemaAccess.Content = $"Allow schema inspection (names and columns shared with {Recipient})";
    }
    private async void OnProviderChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!ready || updatingProvider) return;
        var next = Providers.SelectedIndex == 1 ? claudeClient : chatGptClient;
        if (ReferenceEquals(next, client)) return;
        if (operation is not null) { RefreshProvider(); return; }
        await RunAsync(async token =>
        {
            providerChats[client.ProviderId] = (conversation.CompletedExchanges > 0 ? CreateSavedChat() : null, QuestionInput.Text ?? "");
            await RevokeDatabaseAsync(); ResetConversation();
            client = next; RefreshProvider(); Models.ItemsSource = Array.Empty<AssistantModel>(); UpdateAccounts();
            contextAccountId = null; ShareConnectionNotes.IsChecked = false;
            if (providerChats.TryGetValue(client.ProviderId, out var previous))
            {
                if (previous.Chat is { } saved) await RestoreChatAsync(saved, token);
                else QuestionInput.Text = previous.Draft;
            }
            await client.InitializeAsync(token); await ConnectedAsync(token);
            if (openedChat is { } restored && Models.Items.OfType<AssistantModel>().FirstOrDefault(model => model.Id == restored.ModelId) is { } selected) Models.SelectedItem = selected;
            AssistantStatus.Text = $"{ProviderName} selected. Review Context before sending. Database access is off.";
        });
    }

    private async Task OpenBrowserAsync(Uri uri)
    { if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException("The browser could not be opened. Check the system's default browser and try again."); }

    private async Task ConnectedAsync(CancellationToken token)
    {
        UpdateAccounts();
        Models.ItemsSource = Array.Empty<AssistantModel>();
        AccountStatus.Text = client.Account is { } account ? account.DisplayName + (IsClaude ? " · Claude Console API billing" : account.CanUsePlan ? " · Using ChatGPT plan" : " · ChatGPT plan permission is not enabled")
            : IsClaude ? "Set up your Anthropic API key. API usage is billed through Claude Console." : "Sign in with an eligible ChatGPT account. Login credentials are stored in your desktop keyring.";
        if (client.Account?.CanUsePlan == true)
        {
            if (!IsClaude && !(await settings.LoadAsync(token)).ChatGptWelcomeShown)
            {
                await new SafetyPrivacyDialog(false, "You're using your ChatGPT plan.\n\nEligible AI requests in Rowfinch use your ChatGPT plan or available credits and are subject to account/workspace limits. You can manage Rowfinch's usage and access in ChatGPT settings.\n\nThe request preview and any schema or approved query results you enable are shared with OpenAI. Review sharing and approvals carefully.", "Got it")
                    { Title = "You're using your ChatGPT plan", Width = 570, Height = 360 }.ShowDialog<bool>(this);
                token.ThrowIfCancellationRequested();
                await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true, token);
            }
            var models = await client.GetModelsAsync(token);
            Models.ItemsSource = models;
            Models.SelectedItem = models.FirstOrDefault(model => model.Id == openedChat?.ModelId) ?? models.FirstOrDefault();
            AssistantStatus.Text = models.Count == 0 ? "No available models were returned. Review access in " + ProviderName + " settings." : "Ready. Follow-ups reuse earlier completed messages and shared data in this chat.";
        }
        else AssistantStatus.Text = IsClaude ? "Set up your Anthropic API key to use Claude." : "Continue with ChatGPT and approve plan access to use the assistant.";
    }

    private async void OnSignIn(object? sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (client is IApiKeyAssistantClient api)
        {
            var previousAccount = client.Account?.ClientId;
            if (!await new ClaudeApiKeyDialog(api, token).ShowDialog<bool>(this)) return;
            if (previousAccount != client.Account?.ClientId)
            {
                await RevokeDatabaseAsync();
                if (openedChat is not { } saved || saved.Provider != client.ProviderId || saved.AccountId != client.Account?.ClientId) ResetConversation();
            }
        }
        else await client.SignInAsync((Accounts.SelectedItem as AssistantAccount)?.ClientId ?? client.Account?.ClientId, OpenBrowserAsync, token);
        await ConnectedAsync(token);
    });
    private async void OnAddAccount(object? sender, RoutedEventArgs args) => await RunAsync(async token =>
    { await client.SignInAsync(null, OpenBrowserAsync, token); await ConnectedAsync(token); });
    private async void OnSignOut(object? sender, RoutedEventArgs args) => await RunAsync(async token =>
    { var message = await client.SignOutAsync(token); await RevokeDatabaseAsync(); ClearSharedContext(); await ConnectedAsync(token); AssistantStatus.Text = message; });
    private async void OnAccountChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!ready || updatingAccounts || Accounts.SelectedItem is not AssistantAccount account || operation is not null) return;
        await RunAsync(async token => { await client.SelectAccountAsync(account.ClientId, token); await RevokeDatabaseAsync(); if (openedChat is null) ClearSharedContext(); await ConnectedAsync(token); });
    }
    private async void OnManageUsage(object? sender, RoutedEventArgs args)
    { try { await OpenBrowserAsync(new Uri(IsClaude ? "https://platform.claude.com/settings/usage" : "https://chatgpt.com/settings/usage")); } catch (Exception error) { AssistantStatus.Text = error.Message; } }

    private void OnQuestionChanged(object? sender, TextChangedEventArgs args) { if (ready) RefreshPreview(); }
    private void OnSharingChanged(object? sender, RoutedEventArgs args) { if (ready) CaptureContext(); }
    private void OnRefreshContext(object? sender, RoutedEventArgs args) => CaptureContext();
    private void CaptureContext()
    {
        contextAccountId = client.Account?.ClientId;
        sqlSnapshot = ShareSql.IsChecked == true ? source.Sql : null;
        schemaSnapshot = ShareSchema.IsChecked == true ? string.Join("\n\n", source.Objects.Take(50).Select(node => node.Object is { } item
            ? item.QuotedName + " · " + item.Kind + (node.Columns is { } columns ? "\n" + string.Join("\n", columns.Where(column => !column.IsHidden).Select(column => "  " + ObjectSql.Quote(column.Name) + " " + column.Description)) : " (columns not loaded)") : "")) : null;
        rowsSnapshot = ShareRows.IsChecked == true ? source.SelectedResult : null;
        RefreshPreview();
    }
    private void RefreshPreview()
    {
        try
        {
            RequestPreview.Text = string.IsNullOrWhiteSpace(QuestionInput.Text) ? "Enter a question to preview the request." : AssistantContext.Build(QuestionInput.Text, sqlSnapshot, schemaSnapshot, rowsSnapshot, connectionNotes: ShareConnectionNotes.IsChecked == true ? ConnectionNotesInput.Text : null);
            requestError = null;
        }
        catch (ArgumentException error) { RequestPreview.Text = ""; requestError = error.Message; AssistantStatus.Text = error.Message; }
        RefreshButtons();
    }
    private void OnConnectionNotesChanged(object? sender, TextChangedEventArgs args)
    {
        if (!ready || updatingNotes) return;
        ConnectionNotesStatus.Text = ConnectionNotesInput.Text == savedNotes ? "Saved connection notes. Checked notes are included in the request preview." : "Notes edited. Save notes to keep these changes for future chats. Checked notes are included in the preview.";
        RefreshPreview();
    }
    private void OnNotesSharingChanged(object? sender, RoutedEventArgs args) { if (ready && !updatingNotes) RefreshPreview(); }
    private async void OnNotesSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(QueryTabViewModel.IsConnected) or nameof(QueryTabViewModel.ConnectionLabel) or nameof(QueryTabViewModel.SelectedProfile)
            or nameof(QueryTabViewModel.Server) or nameof(QueryTabViewModel.Database) or nameof(QueryTabViewModel.UserName) or nameof(QueryTabViewModel.AuthenticationIndex))
            await RefreshConnectionNotesAsync();
    }
    private async Task RefreshConnectionNotesAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        var selected = databaseSource ?? DatabaseConnections.SelectedItem as QueryTabViewModel ?? source;
        if (!ReferenceEquals(selected, notesSource))
        {
            if (notesSource is { } previous) previous.PropertyChanged -= OnNotesSourceChanged;
            notesSource = selected; notesSource.PropertyChanged += OnNotesSourceChanged;
        }
        var profile = selected.AssistantContextConnection;
        var key = profile is null ? null : AssistantConnectionContextStore.Key(profile);
        if (key == notesKey && (profile is not null || notesRevision > 0)) return;
        if (notesRevision > 0 && openedChat is null && messages.Count > 0)
        {
            if (pendingEntry is not null) { operation?.Cancel(); approvalDialog?.Close(false); }
            ResetConversation();
            AssistantStatus.Text = "The connection context changed. A new chat was started.";
        }
        var revision = ++notesRevision; notesKey = key; notesConnection = profile; loadingNotes = true;
        updatingNotes = true;
        savedNotes = ""; ConnectionNotesInput.Text = ""; ShareConnectionNotes.IsChecked = false;
        ConnectionNotesTarget.Text = profile is null ? "Choose a saved connection or enter valid connection details to keep notes. Demo has no saved connection context."
            : $"{profile.Name} · {profile.Database} · {profile.Server} · {profile.UserName}";
        updatingNotes = false; RefreshPreview();
        try
        {
            var notes = profile is null ? "" : await connectionNotesStore.LoadAsync(profile, lifetime.Token);
            if (revision != notesRevision || lifetime.IsCancellationRequested) return;
            updatingNotes = true;
            savedNotes = notes; ConnectionNotesInput.Text = notes; ShareConnectionNotes.IsChecked = profile is not null && openedChat is null;
            ConnectionNotesStatus.Text = "Save notes to reuse them in future chats.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { if (revision == notesRevision) ConnectionNotesStatus.Text = "Saved connection notes could not be loaded. Check the local assistant-contexts.json file. Your notes file has not been changed."; }
        finally
        {
            if (revision == notesRevision) { updatingNotes = false; loadingNotes = false; RefreshPreview(); }
        }
    }
    private async void OnSaveConnectionNotes(object? sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var profile = notesConnection ?? throw new InvalidOperationException("Choose a connection before saving notes.");
        var key = notesKey;
        var notes = ConnectionNotesInput.Text ?? "";
        await connectionNotesStore.SaveAsync(profile, notes, token);
        if (key == notesKey) { savedNotes = notes; ConnectionNotesStatus.Text = string.IsNullOrWhiteSpace(notes) ? "Saved notes cleared for this connection." : "Notes saved for this connection. They will load in future chats."; }
        AssistantStatus.Text = "Connection notes saved locally. Nothing was sent to the assistant provider.";
    });

    private void ClearSharedContext() => ResetConversation();
    private async void OnSend(object? sender, RoutedEventArgs args)
    {
        if (operation is not null || loadingNotes || requestError is not null || !conversation.CanContinue || Models.SelectedItem is not AssistantModel model || string.IsNullOrWhiteSpace(QuestionInput.Text)) return;
        if (openedChat is { } saved && (saved.Provider != client.ProviderId || saved.AccountId != client.Account?.ClientId))
        { AssistantStatus.Text = "This saved chat belongs to a different assistant provider or account. Select its original saved account or choose New chat."; return; }
        if (contextAccountId is not null && contextAccountId != client.Account?.ClientId)
        { await RevokeDatabaseAsync(); ClearSharedContext(); AssistantStatus.Text = "The assistant account changed. The previous chat was cleared; review context before sending again."; return; }
        contextAccountId = client.Account?.ClientId;
        openedChat = null;
        var request = RequestPreview.Text!;
        var question = QuestionInput.Text!;
        var revision = conversation.Revision;
        await RunAsync(async token =>
        {
            completedAnswer = false; requestedSql = ""; AssistantTabs.SelectedIndex = 0;
            var entry = new AssistantChatEntry(question);
            latestEntry = pendingEntry = entry; messages.Add(entry); RefreshButtons(); ScrollChat(true);
            AssistantStatus.Text = ProviderName + (IsClaude ? " API · receiving answer…" : " plan · receiving answer…");
            var tools = database is { } access ? new AssistantDatabaseTools(access, AllowSchemaAccess.IsChecked == true, ApproveQueryAsync, AddDatabaseActivity) : null;
            try
            {
                var progress = new Progress<string>(delta =>
                {
                    if (!token.IsCancellationRequested && revision == conversation.Revision && ReferenceEquals(pendingEntry, entry))
                    { entry.Answer += delta; ScrollChat(); }
                });
                var answer = await client.AskConversationAsync(model.Id, request, conversation, tools, progress, token);
                token.ThrowIfCancellationRequested();
                if (revision != conversation.Revision) return;
                entry.Answer = answer; entry.Status = "Assistant"; completedAnswer = true; openedChat = null;
                QuestionInput.Text = "";
                RefreshConversationInfo(); ScrollChat();
                AssistantStatus.Text = conversation.CanContinue ? "Completed. Ask a follow-up or start a new chat." : "This chat reached its history limit. Choose New chat to continue.";
            }
            catch (OperationCanceledException)
            {
                entry.Status = "Assistant · cancelled; not included in follow-ups";
                entry.Error = "Request cancelled. Any displayed answer may be partial and is not used in follow-ups.";
                ScrollChat(); throw;
            }
            catch (Exception error)
            {
                entry.Status = "Assistant · failed; not included in follow-ups";
                entry.Error = AssistantErrorMessage(error);
                ScrollChat(); throw;
            }
            finally
            {
                entry.DatabaseFeedback = tools?.LastRequestIssue ?? "";
                if (ReferenceEquals(pendingEntry, entry)) pendingEntry = null;
            }
        }, revision);
    }

    public SavedAssistantChat CreateSavedChat() => conversation.Save(
        messages.Where(message => message.Status == "Assistant").Select(message => new SavedChatMessage(message.Question, message.Answer)),
        database?.Target ?? historyTarget, (Models.SelectedItem as AssistantModel)?.Id, QuestionInput.Text ?? "");

    public async Task RestoreChatAsync(SavedAssistantChat saved, CancellationToken token = default)
    {
        saved.Validate(); // Validate before changing the current chat or access.
        token.ThrowIfCancellationRequested();
        await RevokeDatabaseAsync();
        token.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
        ResetConversation();
        if (saved.Provider != client.ProviderId)
        {
            client = saved.Provider == "claude" ? claudeClient : chatGptClient;
            RefreshProvider();
            try { await client.InitializeAsync(token); await ConnectedAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { Models.ItemsSource = Array.Empty<AssistantModel>(); UpdateAccounts(); }
        }
        conversation.Restore(saved);
        AllowSchemaAccess.IsChecked = AllowQueryAccess.IsChecked = false;
        ShareConnectionNotes.IsChecked = false;
        foreach (var message in saved.Messages)
            messages.Add(new AssistantChatEntry(message.Question) { Answer = message.Answer, Status = "Assistant" });
        latestEntry = messages.Last(); completedAnswer = true;
        openedChat = saved; historyTarget = saved.DatabaseTarget;
        contextAccountId = null; // Viewing a file needs no sign-in; Send checks the saved account.
        QuestionInput.Text = saved.Draft;
        if (Models.Items.OfType<AssistantModel>().FirstOrDefault(model => model.Id == saved.ModelId) is { } model)
            Models.SelectedItem = model;
        DatabaseContextLabel.Text = "Database access: off. History from: " + (saved.DatabaseTarget ?? "no live database access") + ". Saved data may be out of date.";
        DatabaseAccessStatus.Text = "Access is off. To resume with fresh database access, enable the original database/server/user: " + (saved.DatabaseTarget ?? "none; choose New chat to enable access") + ".";
        AssistantTabs.SelectedIndex = 0;
        RefreshConversationInfo(); RefreshPreview(); ScrollChat(true);
        AssistantStatus.Text = $"Chat opened locally. Review its history in Context. Send shares that history with {ProviderName}; new queries still require approval.";
    }

    private async void OnSaveChat(object? sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var saved = CreateSavedChat();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save chat · includes shared data (unencrypted)",
            SuggestedFileName = $"Rowfinch-chat-{DateTime.Now:yyyy-MM-dd-HHmm}.rowfinch-chat.json",
            DefaultExtension = "rowfinch-chat.json", ShowOverwritePrompt = true, FileTypeChoices = [ChatFiles]
        });
        if (file is null) { AssistantStatus.Text = "Save cancelled."; return; }
        token.ThrowIfCancellationRequested();
        var path = file.TryGetLocalPath() ?? throw new InvalidOperationException("Choose a local file to save this chat.");
        await AssistantChatFile.WriteAsync(path, saved, token);
        AssistantStatus.Text = $"Saved {file.Name}. This unencrypted file includes shared data; keep it private.";
    });

    private async void OnOpenChat(object? sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Open a Rowfinch chat", AllowMultiple = false, FileTypeFilter = [ChatFiles, FilePickerFileTypes.All] });
        if (files.Count == 0) { AssistantStatus.Text = "Open cancelled."; return; }
        SavedAssistantChat saved;
        await using (var input = await files[0].OpenReadAsync()) saved = await AssistantChatFile.ReadAsync(input, token);
        token.ThrowIfCancellationRequested();
        if ((messages.Count > 0 || !string.IsNullOrWhiteSpace(QuestionInput.Text)) && !await ConfirmReplaceChatAsync(token))
        { AssistantStatus.Text = "Open cancelled. Current chat retained."; return; }
        token.ThrowIfCancellationRequested();
        await RestoreChatAsync(saved, token);
    });

    private async Task<bool> ConfirmReplaceChatAsync(CancellationToken token)
    {
        var dialog = new Window { Title = "Open saved chat", Width = 450, Height = 210, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel" };
        var replace = new Button { Content = "Open saved chat" }; replace.Classes.Add("primary");
        cancel.Click += (_, _) => dialog.Close(false); replace.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 18, Children =
        {
            new TextBlock { Text = "Opening this file replaces the current chat and disables database access. If you want to keep this chat, cancel and choose Save chat first.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, replace } }
        } };
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(this);
    }

    private void OnNewChat(object? sender, RoutedEventArgs args)
    {
        if (pendingEntry is not null) { operation?.Cancel(); approvalDialog?.Close(false); }
        ResetConversation(); AssistantTabs.SelectedIndex = 0;
        AssistantStatus.Text = "New chat started. Any enabled database access is retained; each new query still needs approval.";
        QuestionInput.Focus();
    }
    private void ResetConversation(bool clearQuestion = true)
    {
        conversation.Clear(); messages.Clear(); latestEntry = pendingEntry = null; completedAnswer = false;
        openedChat = null; historyTarget = null; requestedSql = "";
        if (database is null)
        {
            DatabaseContextLabel.Text = "Database access: off. Saved chats contain shared data; protect the files.";
            DatabaseAccessStatus.Text = "Access is off. No schema or rows are fetched for the assistant.";
        }
        ShareSql.IsChecked = ShareSchema.IsChecked = ShareRows.IsChecked = false;
        sqlSnapshot = schemaSnapshot = null; rowsSnapshot = null;
        contextAccountId = client.Account?.ClientId;
        DatabaseActivity.Text = "";
        if (clearQuestion) QuestionInput.Text = "";
        RefreshConversationInfo(); RefreshPreview();
    }
    private void RefreshConversationInfo()
    {
        var count = conversation.CompletedExchanges;
        var exchanges = count == 1 ? "1 exchange" : $"{count} exchanges";
        ChatHistorySummary.Text = count == 0 ? "Ask a question or follow up. Review sharing in Context."
            : $"{exchanges} · earlier messages and shared data are included in follow-ups. New chat clears history.";
        ChatHistoryExpander.Header = $"Earlier messages and shared data ({exchanges})";
        ChatHistoryPreview.Text = conversation.Preview();
    }
    private void ScrollChat(bool force = false)
    {
        if (force || ChatScroll.Offset.Y + ChatScroll.Viewport.Height >= ChatScroll.Extent.Height - 60)
            Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested) ChatScroll.ScrollToEnd(); }, DispatcherPriority.Background);
    }
    private void OnSourceConnectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(QueryTabViewModel.IsConnected) or nameof(QueryTabViewModel.ConnectionLabel))) return;
        var current = (source.IsConnected, source.ConnectionLabel);
        if (sourceConnection == current) return;
        sourceConnection = current;
        if (openedChat is not null && database is null) return;
        if (pendingEntry is not null) { operation?.Cancel(); approvalDialog?.Close(false); }
        ResetConversation();
        AssistantStatus.Text = "The source database connection changed. A new chat was started.";
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, int? chatRevision = null)
    {
        if (operation is not null || lifetime.IsCancellationRequested) return;
        var accountCount = client.SavedAccounts.Count;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = cancellation; AssistantStatus.Text = "Working…"; RefreshButtons();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { if (chatRevision is null || chatRevision == conversation.Revision) AssistantStatus.Text = "Cancelled. Any displayed answer may be partial and is not used in follow-ups."; }
        catch (Exception error) { if (chatRevision is null || chatRevision == conversation.Revision) AssistantStatus.Text = AssistantErrorMessage(error); }
        finally
        {
            cancellation.Cancel(); operation = null;
            if (contextAccountId is not null && contextAccountId != client.Account?.ClientId)
            { await RevokeDatabaseAsync(); ClearSharedContext(); }
            UpdateAccounts(client.SavedAccounts.Count > accountCount);
            RefreshButtons();
        }
    }

    private string AssistantErrorMessage(Exception error)
    {
        var message = error is HttpRequestException ? $"Could not reach {Recipient}. Check your network connection and try again." : error.Message;
        return message.Length <= 2000 ? message : message[..2000] + "…";
    }
    private async void OnEnableDatabase(object? sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (database is not null) return;
        if (AllowSchemaAccess.IsChecked != true && AllowQueryAccess.IsChecked != true)
            throw new InvalidOperationException("Choose schema inspection, read-only queries, or both before enabling access.");
        if (DatabaseConnections.SelectedItem is not QueryTabViewModel { IsConnected: true, IsClosing: false } selected)
            throw new InvalidOperationException("Choose a connected query tab. You can connect a separate tab using a SELECT-only account.");
        var candidate = await selected.OpenAssistantDatabaseAsync(AllowQueryAccess.IsChecked == true, token);
        try
        {
            if (openedChat is { } saved)
            {
                if ((saved.Provider != client.ProviderId || saved.AccountId != client.Account?.ClientId) || saved.DatabaseTarget is null || saved.DatabaseTarget != candidate.Target)
                    throw new InvalidOperationException("To retain this saved chat, use its original assistant provider, account and database/server/user: " + (saved.DatabaseTarget ?? "no database access") + ". Otherwise choose New chat first.");
                conversation.BindRestoredDatabase(saved.AccountId, candidate, AllowSchemaAccess.IsChecked == true, candidate.CanQuery);
            }
            else ResetConversation(clearQuestion: messages.Count > 0);
        }
        catch { await candidate.DisposeAsync(); throw; }
        database = candidate; historyTarget = database.Target;
        databaseSource = selected; databaseSource.PropertyChanged += OnDatabaseSourceChanged;
        await RefreshConnectionNotesAsync();
        openedChat = null;
        contextAccountId = client.Account?.ClientId;
        DatabaseAccessStatus.Text = "Enabled: " + database.Target + " · " +
            (AllowSchemaAccess.IsChecked == true ? "schema inspection" : "") + (database.CanQuery ? " · read-only queries with approval" : "");
        DatabaseContextLabel.Text = DatabaseAccessStatus.Text;
        AddDatabaseActivity(DatabaseAccessStatus.Text);
        AssistantStatus.Text = "Database access enabled for this window. Ask a question to use it.";
    });
    private async void OnDisableDatabase(object? sender, RoutedEventArgs args)
    {
        operation?.Cancel(); approvalDialog?.Close(false);
        await RevokeDatabaseAsync(); RefreshButtons();
        AssistantStatus.Text = "Database access disabled. Pending work was cancelled.";
    }
    private async void OnDatabaseSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (databaseSource is { } tab && ((!tab.IsConnected && args.PropertyName == nameof(QueryTabViewModel.IsConnected)) || tab.IsClosing))
        {
            operation?.Cancel(); approvalDialog?.Close(false);
            await RevokeDatabaseAsync(); RefreshButtons();
        }
    }
    private async Task RevokeDatabaseAsync()
    {
        if (databaseSource is { } tab) tab.PropertyChanged -= OnDatabaseSourceChanged;
        databaseSource = null;
        var previous = database; database = null;
        if (previous is null) return;
        ResetConversation();
        await RefreshConnectionNotesAsync();
        DatabaseAccessStatus.Text = "Access is off. Enable it again to allow database requests.";
        DatabaseContextLabel.Text = "Database access: off.";
        AddDatabaseActivity("Database access disabled.");
        try { await previous.DisposeAsync(); }
        catch (Exception) { AssistantStatus.Text = "Database access disabled; session cleanup could not be confirmed."; }
    }
    private async Task<bool> ApproveQueryAsync(AssistantQueryApproval request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (database is null || lifetime.IsCancellationRequested) return false;
        // Retain the complete requested SQL for manual review even if it is rejected or the answer fails.
        requestedSql = request.Sql;
        // Reject unsupported SQL before asking for approval. The tool reports this to the model for correction.
        ReadOnlySqlPolicy.Validate(request.Sql);
        AssistantStatus.Text = "Waiting for your query approval…";
        var dialog = new AssistantQueryApprovalDialog(request, Recipient);
        approvalDialog = dialog;
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        try
        {
            var approved = await dialog.ShowDialog<bool>(this);
            token.ThrowIfCancellationRequested();
            AssistantStatus.Text = approved ? "Running approved read-only query…" : "Query declined. Receiving answer…";
            return approved && database is not null;
        }
        finally { approvalDialog = null; }
    }
    private void AddDatabaseActivity(string message)
    {
        // No disk log; retain a bounded amount in this window only.
        var text = (DatabaseActivity.Text ?? "") + "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + "\n\n";
        DatabaseActivity.Text = text.Length <= 100_000 ? text : "[Earlier activity removed]\n" + text[^90_000..];
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => operation?.Cancel();
    private async void OnCopyAnswer(object? sender, RoutedEventArgs args)
    {
        try { if (Clipboard is { } clipboard) { await clipboard.SetTextAsync(LatestAnswer); AssistantStatus.Text = "Answer copied."; } }
        catch (Exception) { AssistantStatus.Text = "The answer could not be copied to the clipboard."; }
    }
    public static string SuggestedSql(string answer) => string.Join("\n\n", Regex.Matches(answer,
        @"```(?:sql|tsql|t-sql)[ \t]*\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Select(match => match.Groups[1].Value.Trim()));
    private void OnOpenSql(object? sender, RoutedEventArgs args)
    {
        if (operation is not null || !workspace.CanManageTabs || !workspace.Tabs.Contains(source)) return;
        var sql = LatestSql;
        if (string.IsNullOrWhiteSpace(sql)) return;
        var tab = workspace.NewTab(sql, "Assistant suggestion.sql", source);
        tab.SetDocument(sql, tab.DocumentName, isSaved: false);
        AssistantStatus.Text = "Suggested SQL opened in an unsaved tab. Review it and choose Run when ready.";
    }
    private void RefreshButtons()
    {
        if (!ready) return;
        var busy = operation is not null;
        SaveChatButton.IsEnabled = !busy && conversation.CompletedExchanges > 0;
        OpenChatButton.IsEnabled = !busy;
        NewChatButton.IsEnabled = !busy || pendingEntry is not null;
        Providers.IsEnabled = SignInButton.IsEnabled = AddAccountButton.IsEnabled = Accounts.IsEnabled = !busy;
        DatabaseConnections.IsEnabled = AllowSchemaAccess.IsEnabled = AllowQueryAccess.IsEnabled = !busy && database is null;
        EnableDatabaseButton.IsEnabled = !busy && database is null;
        DisableDatabaseButton.IsEnabled = database is not null;
        SignOutButton.IsEnabled = !busy && (client.Account is not null || Accounts.SelectedItem is AssistantAccount);
        AccountStatus.Text = client.Account is { } active ? active.DisplayName + (IsClaude ? " · Claude Console API billing" : active.CanUsePlan ? " · Using ChatGPT plan" : " · ChatGPT plan permission is not enabled") : IsClaude ? "Set up your Anthropic API key to use Claude." : "Continue with ChatGPT to use the assistant.";
        Models.IsEnabled = QuestionInput.IsEnabled = ShareSql.IsEnabled = ShareSchema.IsEnabled = ShareRows.IsEnabled = RefreshContextButton.IsEnabled = !busy;
        SendAssistantButton.IsEnabled = !busy && !loadingNotes && conversation.CanContinue && requestError is null && client.Account?.CanUsePlan == true &&
            (IsClaude || (Accounts.SelectedItem as AssistantAccount)?.ClientId == client.Account.ClientId) && Models.SelectedItem is AssistantModel && !string.IsNullOrWhiteSpace(QuestionInput.Text);
        ConnectionNotesInput.IsEnabled = ShareConnectionNotes.IsEnabled = SaveConnectionNotesButton.IsEnabled = !busy && !loadingNotes && notesConnection is not null;
        CancelAssistantButton.IsEnabled = busy;
        CopyAnswerButton.IsEnabled = !busy && !string.IsNullOrEmpty(LatestAnswer);
        OpenSqlButton.IsEnabled = !busy && workspace.CanManageTabs && workspace.Tabs.Contains(source) && !string.IsNullOrWhiteSpace(LatestSql);
        ToolTip.SetTip(OpenSqlButton, busy ? "Wait for the current operation to finish." : OpenSqlButton.IsEnabled ? "Open suggested SQL, or the most recent database query, for review. It will not run automatically." : "Available after a completed answer contains SQL or a full database query has been requested, including a rejected query.");
    }

    private void UpdateAccounts(bool preferNew = false)
    {
        var saved = client.SavedAccounts;
        var selected = Accounts.SelectedItem as AssistantAccount;
        updatingAccounts = true;
        Accounts.ItemsSource = saved;
        Accounts.SelectedItem = (preferNew ? saved.LastOrDefault() : null) ??
            saved.FirstOrDefault(account => account.ClientId == client.Account?.ClientId) ??
            saved.FirstOrDefault(account => account.ClientId == selected?.ClientId) ?? saved.FirstOrDefault();
        updatingAccounts = false;
    }
}
