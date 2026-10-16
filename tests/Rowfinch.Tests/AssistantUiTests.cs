using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Rowfinch.Desktop.Views;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class AssistantUiTests
{
    [AvaloniaFact]
    public async Task FirstLaunchRequiresTheSafetyNoticeAndRemembersAcknowledgement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-notice-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        try
        {
            var profiles = new Profiles();
            await using var workspace = new WorkspaceViewModel(profiles);
            var window = new MainWindow(workspace, showFirstRun: true, settings: settings);
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var notice = Assert.Single(window.OwnedWindows.OfType<SafetyPrivacyDialog>());
            Assert.False(profiles.Loaded.Task.IsCompleted);
            Assert.Equal(0, (await settings.LoadAsync()).SafetyNoticeVersion);
            Assert.Contains("permanently delete", SafetyPrivacyDialog.Notice);
            var acknowledge = Assert.Single(notice.GetVisualDescendants().OfType<Button>(), button => button.Name == "AcknowledgeNotice");
            Click(notice, acknowledge);
            await profiles.Loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SafetyPrivacyDialog.NoticeVersion, (await settings.LoadAsync()).SafetyNoticeVersion);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(settings.FilePath));
            window.Close();
            var secondProfiles = new Profiles();
            await using var secondWorkspace = new WorkspaceViewModel(secondProfiles);
            var secondWindow = new MainWindow(secondWorkspace, showFirstRun: true, settings: settings);
            secondWindow.Show();
            await secondProfiles.Loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Empty(secondWindow.OwnedWindows.OfType<SafetyPrivacyDialog>());
            Click(secondWindow, secondWindow.FindControl<Button>("SafetyPrivacyButton")!);
            notice = Assert.Single(secondWindow.OwnedWindows.OfType<SafetyPrivacyDialog>());
            Assert.Contains("About Rowfinch", notice.Title);
            notice.Close(); secondWindow.Close();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task ClosingTheFirstLaunchNoticeLeavesItUnacknowledged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-notice-exit-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, showFirstRun: true, settings: settings);
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Single(window.OwnedWindows.OfType<SafetyPrivacyDialog>()).Close(false);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(window.IsVisible);
        Assert.Equal(0, (await settings.LoadAsync()).SafetyNoticeVersion);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task AssistantSharesOnlyThePreviewAndOpensReviewedSqlWithoutExecutingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-assistant-ui-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client);
        window.Show();
        var source = workspace.SelectedTab!;
        await source.StartDemoAsync();
        source.Sql = "SELECT 'sensitive SQL';";
        source.Password = "never-send-this-password";
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        Assert.False(assistant.FindControl<CheckBox>("ShareSql")!.IsChecked);
        Assert.False(assistant.FindControl<CheckBox>("ShareSchema")!.IsChecked);
        Assert.False(assistant.FindControl<CheckBox>("ShareRows")!.IsChecked);
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Explain this query";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var tabs = assistant.FindControl<TabControl>("AssistantTabs")!;
        Assert.Equal(new[] { "Chat", "Context", "Database access" }, tabs.Items.Cast<TabItem>().Select(tab => tab.Header?.ToString()));
        Assert.Contains(assistant, assistant.FindControl<TextBox>("QuestionInput")!.GetVisualAncestors());
        Assert.Contains(assistant, assistant.FindControl<ScrollViewer>("ChatScroll")!.GetVisualAncestors());
        tabs.SelectedIndex = 1;
        var preview = assistant.FindControl<TextBox>("RequestPreview")!;
        Assert.Equal("Explain this query", preview.Text);
        Assert.DoesNotContain("sensitive SQL", preview.Text);
        Click(assistant, assistant.FindControl<CheckBox>("ShareSql")!);
        Assert.Contains("sensitive SQL", preview.Text);
        Click(assistant, assistant.FindControl<CheckBox>("ShareRows")!);
        Assert.Contains("Northwind", preview.Text);
        Assert.DoesNotContain("never-send-this-password", preview.Text);
        var reviewed = preview.Text;
        source.Sql = "SELECT 'edited after preview';";
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await client.AskStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Contains(assistant, assistant.FindControl<TextBox>("QuestionInput")!.GetVisualAncestors());
        Assert.Contains(assistant, assistant.FindControl<ScrollViewer>("ChatScroll")!.GetVisualAncestors());
        Assert.Equal("Explain this query", assistant.FindControl<TextBox>("QuestionInput")!.Text);
        Assert.Equal(reviewed, client.LastRequest);
        Assert.DoesNotContain("edited after preview", client.LastRequest);
        var openSql = assistant.FindControl<Button>("OpenSqlButton")!;
        Assert.False(openSql.IsEnabled);
        var answerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        openSql.PropertyChanged += (_, change) => { if (change.Property == InputElement.IsEnabledProperty && openSql.IsEnabled) answerReady.TrySetResult(); };
        client.Release.TrySetResult();
        await answerReady.Task.WaitAsync(TestContext.Current.CancellationToken);
        var answer = Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items.Cast<AssistantChatEntry>());
        Assert.Equal("Explain this query", answer.Question);
        Assert.Equal("Try this:\n```sql\nSELECT 42;\n```", answer.Answer);
        Assert.Equal("", assistant.FindControl<TextBox>("QuestionInput")!.Text);
        Assert.Contains("Explain this query", assistant.FindControl<TextBox>("ChatHistoryPreview")!.Text);
        Assert.True(assistant.FindControl<Button>("OpenSqlButton")!.IsEnabled);
        if (Environment.GetEnvironmentVariable("ROWFINCH_ASSISTANT_SCREENSHOT") is { Length: > 0 } path)
        {
            assistant.UpdateLayout();
            using var frame = assistant.CaptureRenderedFrame();
            Assert.NotNull(frame); frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            tabs.SelectedIndex = 1; assistant.UpdateLayout();
            using var contextFrame = assistant.CaptureRenderedFrame(); Assert.NotNull(contextFrame);
            contextFrame.Save(Path.ChangeExtension(path, ".context.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            tabs.SelectedIndex = 0;
        }
        Click(assistant, assistant.FindControl<Button>("OpenSqlButton")!);
        var suggestion = workspace.SelectedTab!;
        await suggestion.ConnectionReady;
        Assert.NotSame(source, suggestion);
        Assert.Equal("SELECT 42;", suggestion.Sql);
        Assert.True(suggestion.IsDirty);
        Assert.True(suggestion.IsDemo);
        Assert.Empty(suggestion.Results);
        Assert.Equal("SELECT 'edited after preview';", source.Sql);
        Assert.Equal(4, source.SelectedResult!.Rows.Count);
        suggestion.MarkSaved(suggestion.DocumentName); source.MarkSaved(source.DocumentName);
        assistant.Close(); window.Close();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task CancelledAnswersCannotOpenSuggestedSqlAndClosingTheSourceClosesItsAssistant()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-assistant-cancel-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client);
        window.Show();
        var source = workspace.SelectedTab!;
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Suggest a SELECT";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await client.AskStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Click(assistant, assistant.FindControl<Button>("CancelAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(assistant.FindControl<Button>("OpenSqlButton")!.IsEnabled);
        Assert.True(await workspace.TryCloseTabAsync(source, _ => Task.FromResult(true)));
        Assert.False(assistant.IsVisible);
        window.Close();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseAccessIsOptInAndEveryQueryRequiresApproval(bool approve)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-db-ui-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client); window.Show();
        var source = workspace.SelectedTab!; await source.StartDemoAsync();
        var originalResult = source.SelectedResult; var originalSql = source.Sql;
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        Assert.False(assistant.FindControl<CheckBox>("AllowSchemaAccess")!.IsChecked);
        Assert.False(assistant.FindControl<CheckBox>("AllowQueryAccess")!.IsChecked);
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        Click(assistant, assistant.FindControl<CheckBox>("AllowSchemaAccess")!);
        Click(assistant, assistant.FindControl<CheckBox>("AllowQueryAccess")!);
        Click(assistant, assistant.FindControl<Button>("EnableDatabaseButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.True(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 0;
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Show a sample of customers";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await client.QueryRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var dialog = Assert.Single(assistant.OwnedWindows.OfType<AssistantQueryApprovalDialog>());
        Assert.Equal("SELECT TOP (10) * FROM dbo.Customers;", dialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "ApprovedSql").Text);
        Assert.Contains("CustomerId", assistant.FindControl<TextBox>("DatabaseActivity")!.Text);
        Assert.DoesNotContain("Northwind", assistant.FindControl<TextBox>("DatabaseActivity")!.Text);
        Assert.False(client.DatabaseCompleted.Task.IsCompleted);
        Click(dialog, dialog.GetVisualDescendants().OfType<Button>().Single(control => control.Name == (approve ? "ApproveQueryButton" : "DeclineQueryButton")));
        await client.DatabaseCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(approve ? "Northwind" : "declined", client.DatabaseResult);
        Assert.Same(originalResult, source.SelectedResult); Assert.Equal(originalSql, source.Sql);
        Assert.False(source.IsBusy);
        var saved = assistant.CreateSavedChat();
        await assistant.RestoreChatAsync(SavedAssistantChat.Deserialize(saved.Serialize()));
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        Assert.False(assistant.FindControl<CheckBox>("AllowQueryAccess")!.IsChecked);
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        Click(assistant, assistant.FindControl<CheckBox>("AllowSchemaAccess")!);
        Click(assistant, assistant.FindControl<CheckBox>("AllowQueryAccess")!);
        // Enabling a different database must not bind old history or grant access.
        await assistant.RestoreChatAsync(saved with { DatabaseTarget = "another database" });
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        Click(assistant, assistant.FindControl<CheckBox>("AllowQueryAccess")!);
        Click(assistant, assistant.FindControl<Button>("EnableDatabaseButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        await assistant.RestoreChatAsync(saved);
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        Click(assistant, assistant.FindControl<CheckBox>("AllowSchemaAccess")!);
        Click(assistant, assistant.FindControl<CheckBox>("AllowQueryAccess")!);
        Click(assistant, assistant.FindControl<Button>("EnableDatabaseButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.True(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Refresh the saved sample";
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 0;
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var newApproval = Assert.Single(assistant.OwnedWindows.OfType<AssistantQueryApprovalDialog>());
        Click(newApproval, newApproval.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "DeclineQueryButton"));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(2, assistant.FindControl<ItemsControl>("ChatMessages")!.Items.Count);
        Assert.Equal(4, client.LastHistory.Count);
        var openSql = assistant.FindControl<Button>("OpenSqlButton")!;
        Assert.True(openSql.IsEnabled); // The answer has no SQL fence; recover the actual database query.
        Click(assistant, openSql);
        var openedQuery = workspace.SelectedTab!; await openedQuery.ConnectionReady;
        Assert.Equal("SELECT TOP (10) * FROM dbo.Customers;", openedQuery.Sql);
        Assert.Empty(openedQuery.Results); openedQuery.MarkSaved(openedQuery.DocumentName);
        Click(assistant, assistant.FindControl<Button>("NewChatButton")!);
        Assert.Empty(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.True(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        if (Environment.GetEnvironmentVariable("ROWFINCH_DATABASE_SCREENSHOT") is { Length: > 0 } path)
        {
            assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2; assistant.UpdateLayout();
            using var frame = assistant.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        Click(assistant, assistant.FindControl<Button>("DisableDatabaseButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        source.MarkSaved(source.DocumentName); assistant.Close(); window.Close();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task DisconnectingTheChosenDatabaseRevokesAccessAndCancelsPendingApproval()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-db-revoke-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client); window.Show();
        var source = workspace.SelectedTab!; await source.StartDemoAsync();
        var other = workspace.NewTab(); await other.ConnectionReady;
        workspace.SelectedTab = source;
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        assistant.FindControl<ComboBox>("DatabaseConnections")!.SelectedItem = other;
        Click(assistant, assistant.FindControl<CheckBox>("AllowQueryAccess")!);
        Click(assistant, assistant.FindControl<Button>("EnableDatabaseButton")!);
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 0;
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Show customers";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await client.QueryRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Single(assistant.OwnedWindows.OfType<AssistantQueryApprovalDialog>());
        await other.DisconnectAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(assistant.OwnedWindows.OfType<AssistantQueryApprovalDialog>());
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        Assert.True(assistant.IsVisible); Assert.Null(client.DatabaseResult);
        source.MarkSaved(source.DocumentName); assistant.Close(); window.Close();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task FollowUpsRetainTheConversationAndNewChatClearsSharedContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-follow-up-ui-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant(); client.Release.TrySetResult();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client); window.Show();
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        var input = assistant.FindControl<TextBox>("QuestionInput")!;
        input.Text = "Which orders match?";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(client.LastHistory);
        input.Text = "Show those orders with customer names";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var entries = assistant.FindControl<ItemsControl>("ChatMessages")!.Items.Cast<AssistantChatEntry>().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("Which orders match?", entries[0].Question);
        Assert.Equal("Show those orders with customer names", entries[1].Question);
        Assert.Contains(client.LastHistory, item => item.GetProperty("content").GetString() == "Which orders match?");
        Assert.Contains("SELECT 42", assistant.FindControl<TextBox>("ChatHistoryPreview")!.Text);
        if (Environment.GetEnvironmentVariable("ROWFINCH_FOLLOWUP_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            assistant.UpdateLayout(); using var frame = assistant.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Click(assistant, assistant.FindControl<Button>("NewChatButton")!);
        Assert.Empty(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.DoesNotContain("Which orders", assistant.FindControl<TextBox>("ChatHistoryPreview")!.Text);
        input.Text = "New topic";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(client.LastHistory);
        // Changing the source connection starts another chat without mixing old messages.
        await workspace.SelectedTab!.StartDemoAsync();
        Assert.Empty(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        input.Text = "After reconnection";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Click(assistant, assistant.FindControl<Button>("SignOutButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.DoesNotContain("After reconnection", assistant.FindControl<TextBox>("ChatHistoryPreview")!.Text);
        workspace.SelectedTab.MarkSaved(workspace.SelectedTab.DocumentName); assistant.Close(); window.Close();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task NewChatDuringAnAnswerCannotBeRepopulatedByLateProgress()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-reset-pending-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client); window.Show();
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Old question";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await client.AskStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Click(assistant, assistant.FindControl<Button>("NewChatButton")!);
        client.Release.TrySetResult();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.DoesNotContain("Old question", assistant.FindControl<TextBox>("ChatHistoryPreview")!.Text);
        Assert.False(assistant.FindControl<Button>("OpenSqlButton")!.IsEnabled);
        assistant.Close(); window.Close();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task SavedChatRestoresMessagesAndDraftAndSendsHistoryOnlyOnTheNextMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-save-chat-ui-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant(); client.Release.TrySetResult();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client); window.Show();
        var source = workspace.SelectedTab!; source.Password = "never-save-password";
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        Assert.False(assistant.FindControl<Button>("SaveChatButton")!.IsEnabled);
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Which orders match?";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.True(assistant.FindControl<Button>("SaveChatButton")!.IsEnabled);
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Show their customers";
        var saved = assistant.CreateSavedChat();
        var path = Path.Combine(directory, "saved.rowfinch-chat.json");
        await AssistantChatFile.WriteAsync(path, saved);
        Assert.DoesNotContain("never-save-password", await File.ReadAllTextAsync(path));
        assistant.Close();
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        await using (var input = File.OpenRead(path)) await assistant.RestoreChatAsync(await AssistantChatFile.ReadAsync(input));
        Assert.Equal("Which orders match?", client.LastRequest); // Opening does not send anything.
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.Equal("Show their customers", assistant.FindControl<TextBox>("QuestionInput")!.Text);
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        Assert.Contains("SELECT 42", assistant.FindControl<TextBox>("ChatHistoryPreview")!.Text);
        await Assert.ThrowsAsync<InvalidDataException>(() => assistant.RestoreChatAsync(saved with { Version = 99 }));
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        await assistant.RestoreChatAsync(saved with { AccountId = "other-saved-account" });
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal("Which orders match?", client.LastRequest);
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        await assistant.RestoreChatAsync(saved);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal("Show their customers", client.LastRequest);
        Assert.Equal(2, client.LastHistory.Count);
        Assert.Equal(2, assistant.FindControl<ItemsControl>("ChatMessages")!.Items.Count);
        Click(assistant, assistant.FindControl<Button>("SignOutButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await assistant.RestoreChatAsync(saved);
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.False(assistant.FindControl<Button>("SendAssistantButton")!.IsEnabled);
        assistant.Close(); window.Close(); Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task ConnectionNotesLoadInNewChatsAppearInPreviewAndNeverLeakToAnotherTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-notes-ui-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant(); client.Release.TrySetResult();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client); window.Show();
        var source = workspace.SelectedTab!;
        source.Server = "orders.example.test"; source.Database = "Orders"; source.UserName = "reader";
        source.Password = "not-a-note-or-saved-secret";
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 1;
        var notes = "PO is the order header; POitems contains PO lines. SystemId defines the product series.";
        assistant.FindControl<TextBox>("ConnectionNotesInput")!.Text = notes;
        Click(assistant, assistant.FindControl<Button>("SaveConnectionNotesButton")!);
        await WaitUntilAsync(() => assistant.FindControl<Button>("SaveConnectionNotesButton")!.IsEnabled);
        Assert.Null(client.LastRequest);
        Assert.DoesNotContain("not-a-note-or-saved-secret", await File.ReadAllTextAsync(Path.Combine(directory, "assistant-contexts.json"), TestContext.Current.CancellationToken));
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 0;
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Find orders for series 155";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(notes, assistant.FindControl<TextBox>("RequestPreview")!.Text);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(notes, client.LastRequest);
        Click(assistant, assistant.FindControl<Button>("NewChatButton")!);
        Assert.Equal(notes, assistant.FindControl<TextBox>("ConnectionNotesInput")!.Text);
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "Another question";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(notes, assistant.FindControl<TextBox>("RequestPreview")!.Text);
        assistant.Close();
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await WaitUntilAsync(() => window.OwnedWindows.OfType<AssistantWindow>().SingleOrDefault()?.FindControl<TextBox>("ConnectionNotesInput")!.Text == notes);
        assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "After restarting the assistant";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(notes, assistant.FindControl<TextBox>("RequestPreview")!.Text);
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 1;
        Click(assistant, assistant.FindControl<CheckBox>("ShareConnectionNotes")!);
        Assert.DoesNotContain(notes, assistant.FindControl<TextBox>("RequestPreview")!.Text);
        source.Database = "AnotherDatabase";
        await WaitUntilAsync(() => assistant.FindControl<Button>("SaveConnectionNotesButton")!.IsEnabled);
        Assert.Empty(assistant.FindControl<TextBox>("ConnectionNotesInput")!.Text!);
        Assert.DoesNotContain(notes, assistant.FindControl<TextBox>("RequestPreview")!.Text);
        source.Database = "Orders";
        await WaitUntilAsync(() => assistant.FindControl<TextBox>("ConnectionNotesInput")!.Text == notes);
        Assert.Contains(notes, assistant.FindControl<TextBox>("RequestPreview")!.Text);
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        if (Environment.GetEnvironmentVariable("ROWFINCH_NOTES_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            assistant.UpdateLayout(); using var frame = assistant.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        assistant.Close(); window.Close(); Directory.Delete(directory, true);
    }

    [AvaloniaFact]
    public async Task RejectedQueryRemainsReviewableEvenIfTheAssistantFailsAfterReceivingFeedback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-rejected-query-ui-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var client = new FakeAssistant { QuerySql = "SELECT * INTO dbo.Copy FROM dbo.Customers;", FailAfterQuery = true };
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: client); window.Show();
        var source = workspace.SelectedTab!; await source.StartDemoAsync();
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await client.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        Click(assistant, assistant.FindControl<CheckBox>("AllowQueryAccess")!);
        Click(assistant, assistant.FindControl<Button>("EnableDatabaseButton")!);
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 0;
        assistant.FindControl<TextBox>("QuestionInput")!.Text = "This week's totals";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Click(assistant, assistant.FindControl<Button>("SendAssistantButton")!);
        await client.DatabaseCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => assistant.FindControl<Button>("OpenSqlButton")!.IsEnabled);
        Assert.Empty(assistant.OwnedWindows.OfType<AssistantQueryApprovalDialog>());
        Assert.Contains("sql_policy_rejected", client.DatabaseResult);
        Assert.False(assistant.FindControl<Button>("SaveChatButton")!.IsEnabled);
        Assert.Contains("failed", Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items.Cast<AssistantChatEntry>()).Status);
        Click(assistant, assistant.FindControl<Button>("OpenSqlButton")!);
        var review = workspace.SelectedTab!; await review.ConnectionReady;
        Assert.Equal(client.QuerySql, review.Sql); Assert.True(review.IsDirty); Assert.Empty(review.Results);
        review.MarkSaved(review.DocumentName);
        Click(assistant, assistant.FindControl<Button>("NewChatButton")!);
        Assert.False(assistant.FindControl<Button>("OpenSqlButton")!.IsEnabled);
        source.MarkSaved(source.DocumentName); assistant.Close(); window.Close(); Directory.Delete(directory, true);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    [AvaloniaFact]
    public async Task ProvidersKeepSeparateHistoriesAndNeverCarryDatabaseAccessOrSharingAcross()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-provider-ui-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalSettingsStore(Path.Combine(directory, "settings.json"));
        await settings.UpdateAsync(value => value.ChatGptWelcomeShown = true);
        using var chatGpt = new FakeAssistant(); chatGpt.Release.TrySetResult();
        using var claude = new FakeAssistant { ProviderId = "claude" }; claude.Release.TrySetResult();
        await using var workspace = new WorkspaceViewModel(new Profiles());
        var window = new MainWindow(workspace, settings: settings, assistantClient: chatGpt, claudeClient: claude); window.Show();
        await workspace.SelectedTab!.StartDemoAsync();
        Click(window, window.FindControl<Button>("OpenAssistantButton")!);
        await chatGpt.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        var assistant = Assert.Single(window.OwnedWindows.OfType<AssistantWindow>());
        var question = assistant.FindControl<TextBox>("QuestionInput")!;
        var send = assistant.FindControl<Button>("SendAssistantButton")!;
        question.Text = "Private ChatGPT question";
        Click(assistant, send);
        await WaitUntilAsync(() => assistant.FindControl<Button>("SaveChatButton")!.IsEnabled);
        var savedGpt = assistant.CreateSavedChat(); Assert.Equal("chatgpt", savedGpt.Provider);
        var selector = assistant.FindControl<ComboBox>("Providers")!;
        selector.SelectedIndex = 1;
        await claude.ModelsLoaded.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => selector.IsEnabled);
        Assert.Empty(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.Contains("Anthropic", assistant.FindControl<TextBlock>("ProviderDisclosure")!.Text);
        Assert.Contains("Anthropic", assistant.FindControl<CheckBox>("AllowSchemaAccess")!.Content!.ToString());
        Assert.Equal("Set up API key", assistant.FindControl<Button>("SignInButton")!.Content);
        Assert.False(assistant.FindControl<CheckBox>("ShareSql")!.IsChecked);
        Assert.False(assistant.FindControl<CheckBox>("ShareConnectionNotes")!.IsChecked);
        question.Text = "Private Claude question"; Click(assistant, send);
        await WaitUntilAsync(() => assistant.FindControl<Button>("SaveChatButton")!.IsEnabled);
        Assert.Equal("Private Claude question", claude.LastRequest); Assert.Empty(claude.LastHistory);
        var savedClaude = assistant.CreateSavedChat(); Assert.Equal("claude", savedClaude.Provider);
        Assert.DoesNotContain("Private ChatGPT", System.Text.Json.JsonSerializer.Serialize(savedClaude));
        question.Text = "Claude draft";
        selector.SelectedIndex = 0;
        await WaitUntilAsync(() => selector.IsEnabled);
        Assert.Single(assistant.FindControl<ItemsControl>("ChatMessages")!.Items);
        Assert.Equal("Private ChatGPT question", assistant.FindControl<ItemsControl>("ChatMessages")!.Items.Cast<AssistantChatEntry>().Single().Question);
        Assert.Contains("OpenAI", assistant.FindControl<TextBlock>("ProviderDisclosure")!.Text);
        selector.SelectedIndex = 1;
        await WaitUntilAsync(() => selector.IsEnabled);
        Assert.Equal("Claude draft", question.Text);
        Assert.Equal("Private Claude question", assistant.FindControl<ItemsControl>("ChatMessages")!.Items.Cast<AssistantChatEntry>().Single().Question);
        question.Text = "Claude follow-up"; Click(assistant, send);
        await WaitUntilAsync(() => assistant.FindControl<Button>("SaveChatButton")!.IsEnabled);
        Assert.Equal(2, claude.LastHistory.Count);
        Assert.DoesNotContain("Private ChatGPT", System.Text.Json.JsonSerializer.Serialize(claude.LastHistory));
        // Enabling schema access is explicit; switching providers closes its independent session.
        assistant.FindControl<TabControl>("AssistantTabs")!.SelectedIndex = 2;
        assistant.FindControl<CheckBox>("AllowSchemaAccess")!.IsChecked = true;
        Click(assistant, assistant.FindControl<Button>("EnableDatabaseButton")!);
        await WaitUntilAsync(() => assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled && selector.IsEnabled);
        selector.SelectedIndex = 0;
        await WaitUntilAsync(() => selector.IsEnabled);
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        // Opening a saved Claude file selects its provider, with access and sharing off.
        await assistant.RestoreChatAsync(savedClaude);
        Assert.Equal(1, selector.SelectedIndex);
        Assert.False(assistant.FindControl<Button>("DisableDatabaseButton")!.IsEnabled);
        Assert.False(assistant.FindControl<CheckBox>("ShareConnectionNotes")!.IsChecked);
        if (Environment.GetEnvironmentVariable("ROWFINCH_CLAUDE_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            assistant.UpdateLayout(); using var frame = assistant.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        assistant.Close(); window.Close(); Directory.Delete(directory, true);
    }

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        window.UpdateLayout();
    }
    private sealed class Profiles : IProfileStore
    {
        public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken token = default) { Loaded.TrySetResult(); return Task.FromResult<IReadOnlyList<ConnectionProfile>>([]); }
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class FakeAssistant : IAssistantClient
    {
        public string ProviderId { get; init; } = "chatgpt";
        public AssistantAccount? Account { get; private set; } = new("test-client", "test-user", "person@example.test", true);
        public IReadOnlyList<AssistantAccount> SavedAccounts => Account is { } account ? [account] : [];
        public TaskCompletionSource ModelsLoaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AskStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastRequest { get; private set; }
        public IReadOnlyList<System.Text.Json.JsonElement> LastHistory { get; private set; } = [];
        public async Task<string> AskConversationAsync(string model, string request, AssistantConversation conversation, AssistantDatabaseTools? database, IProgress<string>? progress, CancellationToken token)
        {
            using var turn = conversation.Begin(Account!.ClientId, database?.ConversationScope, database?.AllowSchema == true, database?.AllowQueries == true, ProviderId);
            LastHistory = turn.Input;
            var answer = database is null ? await AskAsync(model, request, progress, token) : await AskWithDatabaseAsync(model, request, database, progress, token);
            var input = turn.Input.Cast<object>().ToList();
            input.Add(new { role = "user", content = request });
            if (database is not null)
            {
                var callId = "call_" + Guid.NewGuid().ToString("N");
                input.Add(new { type = "function_call", name = "query", @namespace = "rowfinch", call_id = callId, arguments = "{\"sql\":\"SELECT TOP (10) * FROM dbo.Customers;\"}" });
                input.Add(new { type = "function_call_output", call_id = callId, output = DatabaseResult });
            }
            if (ProviderId == "claude") input.Add(new { role = "assistant", content = new[] { new { type = "text", text = answer } } });
            else input.Add(new { role = "assistant", content = answer });
            turn.Commit(input);
            return answer;
        }
        public string? DatabaseResult { get; private set; }
        public string QuerySql { get; init; } = "SELECT TOP (10) * FROM dbo.Customers;";
        public bool FailAfterQuery { get; init; }
        public TaskCompletionSource QueryRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DatabaseCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task SignInAsync(string? clientId, Func<Uri, Task> browser, CancellationToken token) => Task.CompletedTask;
        public Task SelectAccountAsync(string clientId, CancellationToken token) => Task.CompletedTask;
        public Task<string> SignOutAsync(CancellationToken token) { Account = null; return Task.FromResult("Signed out."); }
        public Task<IReadOnlyList<AssistantModel>> GetModelsAsync(CancellationToken token)
        { ModelsLoaded.TrySetResult(); return Task.FromResult<IReadOnlyList<AssistantModel>>([new("test-model", "Test model")]); }
        public async Task<string> AskAsync(string model, string request, IProgress<string>? progress, CancellationToken token)
        {
            LastRequest = request; AskStarted.TrySetResult();
            progress?.Report("Try this:");
            await Release.Task.WaitAsync(token);
            progress?.Report("\n```sql\nSELECT 42;\n```");
            return "Try this:\n```sql\nSELECT 42;\n```";
        }
        public async Task<string> AskWithDatabaseAsync(string model, string request, AssistantDatabaseTools database, IProgress<string>? progress, CancellationToken token)
        {
            LastRequest = request; AskStarted.TrySetResult();
            if (database.AllowSchema) await database.InvokeAsync("describe_object", "{\"schema\":\"dbo\",\"name\":\"Customers\"}", token);
            QueryRequested.TrySetResult();
            DatabaseResult = await database.InvokeAsync("query", System.Text.Json.JsonSerializer.Serialize(new { sql = QuerySql, reason = "Get a small sample of customers." }), token);
            DatabaseCompleted.TrySetResult();
            if (FailAfterQuery) throw new InvalidOperationException("The fake assistant failed after receiving policy feedback.");
            return "Database response received.";
        }
        public void Dispose() { }
    }
}
