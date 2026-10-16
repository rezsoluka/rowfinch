using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rowfinch.Core;
using Rowfinch.Desktop.Views;
using Xunit;

namespace Rowfinch.Tests;

public sealed class ClaudeApiKeyUiTests
{
    [AvaloniaFact]
    public async Task KeySetupMasksAndClearsSecretsAndOffersSessionOnlyRecovery()
    {
        using var client = new FakeKeyClient(); var owner = new Window(); owner.Show();
        var dialog = new ClaudeApiKeyDialog(client, TestContext.Current.CancellationToken);
        var closed = dialog.ShowDialog<bool>(owner);
        var key = Find<TextBox>(dialog, "ClaudeApiKey");
        var remember = Find<CheckBox>(dialog, "RememberClaudeKey");
        Assert.NotEqual(default, key.PasswordChar); Assert.True(remember.IsChecked);
        key.Text = "key-that-must-not-stay-in-ui";
        Find<TextBox>(dialog, "ClaudeWorkspaceId").Text = "wrkspc_test";
        Find<Button>(dialog, "SaveClaudeKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal("", key.Text);
        Assert.True(dialog.IsVisible);
        Assert.Contains("uncheck Remember", Find<TextBlock>(dialog, "ClaudeSetupStatus").Text);
        client.Fail = false; remember.IsChecked = false; key.Text = "session-only-key";
        if (Environment.GetEnvironmentVariable("ROWFINCH_CLAUDE_SETUP_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            dialog.UpdateLayout(); using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dialog); Assert.NotNull(frame);
            frame.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Find<Button>(dialog, "SaveClaudeKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await closed); Assert.False(client.Remember); Assert.Equal("session-only-key", client.Key); Assert.Equal("wrkspc_test", client.Workspace);
        Assert.Equal("", key.Text); owner.Close();
    }

    [AvaloniaFact]
    public async Task ClosingKeySetupCancelsPendingValidationAndClearsInput()
    {
        using var client = new FakeKeyClient { Pending = true }; var owner = new Window(); owner.Show();
        var dialog = new ClaudeApiKeyDialog(client, TestContext.Current.CancellationToken);
        var closed = dialog.ShowDialog<bool>(owner);
        var key = Find<TextBox>(dialog, "ClaudeApiKey"); key.Text = "pending-key";
        Find<Button>(dialog, "SaveClaudeKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<Button>(dialog, "CancelClaudeSetup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(await closed); Assert.Equal("", key.Text);
        await client.Cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        owner.Close();
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private sealed class FakeKeyClient : IApiKeyAssistantClient
    {
        public bool Fail = true, Pending, Remember; public string? Key, Workspace;
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "claude"; public AssistantAccount? Account => null; public IReadOnlyList<AssistantAccount> SavedAccounts => [];
        public async Task ConfigureAsync(string apiKey, string? workspaceId, bool remember, CancellationToken token)
        {
            if (Pending)
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            if (Fail) throw new CredentialStoreException(CredentialStoreFailure.LockedOrDenied);
            Key = apiKey; Workspace = workspaceId; Remember = remember;
        }
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task SignInAsync(string? clientId, Func<Uri, Task> browser, CancellationToken token) => throw new NotSupportedException();
        public Task SelectAccountAsync(string clientId, CancellationToken token) => throw new NotSupportedException();
        public Task<string> SignOutAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssistantModel>> GetModelsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<string> AskAsync(string model, string request, IProgress<string>? progress, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
