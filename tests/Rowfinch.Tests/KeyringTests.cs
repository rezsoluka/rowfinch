using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;
using Rowfinch.Desktop.Views;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class KeyringTests
{
    private const string Password = "unit-test-password;\"not-a-real-credential";

    [Fact]
    public async Task SessionOnlyConnectionNeverAccessesKeyring()
    {
        var keyring = new MemoryKeyring();
        var session = new Session();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => session, keyring);
        var tab = workspace.SelectedTab!;
        Configure(tab); tab.Password = Password;
        await tab.ConnectAsync();
        Assert.True(tab.IsConnected);
        Assert.Equal(Password, session.OpenPassword);
        Assert.Equal(0, keyring.GetCalls + keyring.StoreCalls + keyring.DeleteCalls);
        Assert.Equal("", tab.Password);
    }

    [Fact]
    public async Task RememberStoresOnlyAfterLoginAndNeverWritesSecretToProfileJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-credentials-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "connections.json");
        var keyring = new MemoryKeyring();
        var session = new Session();
        keyring.BeforeStore = () => Assert.True(session.IsConnected);
        try
        {
            await using var workspace = new WorkspaceViewModel(new JsonProfileStore(path), () => session, keyring);
            var tab = workspace.SelectedTab!;
            Configure(tab); tab.Password = Password; tab.RememberPassword = true;
            await tab.ConnectAsync();
            Assert.True(tab.IsConnected);
            var profile = Assert.Single(workspace.Profiles);
            Assert.True(profile.RememberPassword);
            Assert.Equal(Password, keyring.Entries[CredentialIdentity.FromProfile(profile)]);
            Assert.Equal("", tab.Password);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);
            Assert.False(document.RootElement[0].TryGetProperty("Password", out _));
            Assert.False(document.RootElement[0].TryGetProperty("AccessToken", out _));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedAuthenticationDoesNotStorePasswordOrSaveRememberPreference()
    {
        var keyring = new MemoryKeyring();
        var store = new ProfileStore();
        await using var workspace = new WorkspaceViewModel(store, () => new Session { FailLogin = true }, keyring);
        var tab = workspace.SelectedTab!;
        Configure(tab); tab.Password = Password; tab.RememberPassword = true;
        await tab.ConnectAsync();
        Assert.False(tab.IsConnected);
        Assert.Equal(0, keyring.StoreCalls);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task AnotherTabRetrievesSavedPasswordJustBeforeConnecting()
    {
        var keyring = new MemoryKeyring();
        var firstSession = new Session();
        var secondSession = new Session();
        var sessions = new Queue<Session>([firstSession, secondSession]);
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => sessions.Dequeue(), keyring);
        var first = workspace.SelectedTab!;
        Configure(first); first.Password = Password; first.RememberPassword = true;
        await first.ConnectAsync();
        var second = workspace.NewTab();
        Assert.Equal("", second.Password);
        Assert.True(second.RememberPassword);
        Assert.Equal(0, keyring.GetCalls);
        await second.ConnectAsync();
        Assert.Equal(1, keyring.GetCalls);
        Assert.Equal(Password, secondSession.OpenPassword);
        Assert.True(first.IsConnected);
        Assert.True(second.IsConnected);
        Assert.Equal("", second.Password);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("database")]
    [InlineData("user")]
    public async Task EditedConnectionNeverReceivesPasswordForPreviousTarget(string field)
    {
        var profile = Profile();
        var keyring = new MemoryKeyring();
        keyring.Entries[CredentialIdentity.FromProfile(profile)] = Password;
        var createdSessions = 0;
        await using var workspace = new WorkspaceViewModel(new ProfileStore(profile), () => { createdSessions++; return new Session(); }, keyring);
        await workspace.InitializeAsync();
        var tab = workspace.SelectedTab!;
        tab.SelectedProfile = workspace.Profiles[0];
        if (field == "server") tab.Server = "other.database.windows.net";
        else if (field == "database") tab.Database = "other-database";
        else tab.UserName = "other-user";
        await tab.ConnectAsync();
        Assert.Equal(0, createdSessions);
        Assert.Equal("Action failed", tab.Status);
        Assert.Contains("No saved password matches", tab.KeyringMessage);
        Assert.Equal(Password, Assert.Single(keyring.Entries).Value);
    }

    [Fact]
    public async Task TypedPasswordOverridesExistingCredentialAndUpdatesItAfterLogin()
    {
        var profile = Profile();
        var keyring = new MemoryKeyring();
        keyring.Entries[CredentialIdentity.FromProfile(profile)] = "old-test-password";
        var session = new Session();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(profile), () => session, keyring);
        await workspace.InitializeAsync();
        var tab = workspace.SelectedTab!;
        tab.SelectedProfile = workspace.Profiles[0]; tab.Password = Password;
        await tab.ConnectAsync();
        Assert.Equal(0, keyring.GetCalls);
        Assert.Equal(Password, session.OpenPassword);
        Assert.Equal(Password, keyring.Entries[CredentialIdentity.FromProfile(profile)]);
    }

    [Theory]
    [InlineData(CredentialStoreFailure.Unavailable, "unavailable")]
    [InlineData(CredentialStoreFailure.LockedOrDenied, "locked")]
    [InlineData(CredentialStoreFailure.TimedOut, "timed out")]
    public async Task KeyringReadFailureOffersManualPasswordWithoutAttemptingSqlLogin(CredentialStoreFailure failure, string expected)
    {
        var profile = Profile();
        var keyring = new MemoryKeyring { ReadFailure = new CredentialStoreException(failure) };
        var session = new Session();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(profile), () => session, keyring);
        await workspace.InitializeAsync();
        var tab = workspace.SelectedTab!;
        tab.SelectedProfile = workspace.Profiles[0];
        await tab.ConnectAsync();
        Assert.False(session.IsConnected);
        Assert.Contains(expected, tab.KeyringMessage);
        Assert.Contains("Enter your password", tab.Messages);
        tab.Password = Password; tab.RememberPassword = false;
        await tab.ConnectAsync();
        Assert.True(tab.IsConnected);
        Assert.Equal(Password, session.OpenPassword);
        Assert.Equal(1, keyring.GetCalls);
    }

    [Fact]
    public async Task WriteFailureKeepsConnectionAndSuppressesBackendExceptionDetails()
    {
        var keyring = new MemoryKeyring { WriteFailure = new InvalidOperationException(Password) };
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => new Session(), keyring);
        var tab = workspace.SelectedTab!;
        Configure(tab); tab.Password = Password; tab.RememberPassword = true;
        await tab.ConnectAsync();
        Assert.True(tab.IsConnected);
        Assert.Contains("could not be confirmed", tab.KeyringMessage);
        Assert.DoesNotContain(Password, tab.KeyringMessage + tab.Messages + tab.Status, StringComparison.Ordinal);
        Assert.Equal("", tab.Password);
    }

    [Fact]
    public async Task ReadFailureDoesNotExposeBackendExceptionDetails()
    {
        var profile = Profile();
        var keyring = new MemoryKeyring { ReadFailure = new InvalidOperationException(Password) };
        await using var workspace = new WorkspaceViewModel(new ProfileStore(profile), () => new Session(), keyring);
        await workspace.InitializeAsync();
        var tab = workspace.SelectedTab!;
        tab.SelectedProfile = workspace.Profiles[0];
        await tab.ConnectAsync();
        Assert.DoesNotContain(Password, tab.KeyringMessage + tab.Messages + tab.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgetRemovesAllTargetsForProfileAndUpdatesTabsWithoutClosingSessions()
    {
        var keyring = new MemoryKeyring();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => new Session(), keyring);
        var first = workspace.SelectedTab!;
        Configure(first); first.Password = Password; first.RememberPassword = true;
        await first.ConnectAsync();
        var profile = Assert.Single(workspace.Profiles);
        keyring.Entries[CredentialIdentity.FromProfile(profile with { Database = "old-target" })] = "old-variant";
        var unrelated = CredentialIdentity.FromProfile(profile with { Id = Guid.NewGuid() });
        keyring.Entries[unrelated] = "unrelated-test-credential";
        var second = workspace.NewTab();
        await second.ConnectAsync();
        await first.ForgetPasswordAsync();
        Assert.False(first.RememberPassword);
        Assert.False(second.RememberPassword);
        Assert.True(first.IsConnected);
        Assert.True(second.IsConnected);
        Assert.Equal(unrelated, Assert.Single(keyring.Entries).Key);
        Assert.False(Assert.Single(workspace.Profiles).RememberPassword);
        Assert.Equal("Password forgotten", first.Status);
    }

    [Fact]
    public async Task FailedForgetDoesNotClaimSuccessOrChangePreference()
    {
        var keyring = new MemoryKeyring();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => new Session(), keyring);
        var tab = workspace.SelectedTab!;
        Configure(tab); tab.Password = Password; tab.RememberPassword = true;
        await tab.ConnectAsync();
        keyring.DeleteFailure = new CredentialStoreException(CredentialStoreFailure.LockedOrDenied);
        await tab.ForgetPasswordAsync();
        Assert.Equal("Action failed", tab.Status);
        Assert.Contains("removal was not confirmed", tab.KeyringMessage);
        Assert.True(tab.RememberPassword);
        Assert.True(tab.IsConnected);
        Assert.Single(keyring.Entries);
        Assert.True(Assert.Single(workspace.Profiles).RememberPassword);
    }

    [Fact]
    public async Task ForgetDuringAnotherTabsLoginPreventsThatLoginFromRestoringPassword()
    {
        var keyring = new MemoryKeyring();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new Queue<Session>([new(), new() { LoginStarted = started, LoginRelease = release }]);
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => sessions.Dequeue(), keyring);
        var first = workspace.SelectedTab!;
        Configure(first); first.Password = Password; first.RememberPassword = true;
        await first.ConnectAsync();
        var second = workspace.NewTab();
        var connecting = second.ConnectAsync();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await first.ForgetPasswordAsync();
        release.TrySetResult();
        await connecting;
        Assert.True(first.IsConnected);
        Assert.True(second.IsConnected);
        Assert.False(second.RememberPassword);
        Assert.Empty(keyring.Entries);
        Assert.Equal(1, keyring.StoreCalls);
        Assert.False(Assert.Single(workspace.Profiles).RememberPassword);
    }

    [Fact]
    public async Task ForgetWaitsForInFlightStorageThenRemovesThePassword()
    {
        var keyring = new MemoryKeyring();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => new Session(), keyring);
        var first = workspace.SelectedTab!;
        Configure(first); first.Password = Password; first.RememberPassword = true;
        await first.ConnectAsync();
        keyring.StoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        keyring.StoreRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = workspace.NewTab();
        var connecting = second.ConnectAsync();
        await keyring.StoreStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var forgetting = first.ForgetPasswordAsync();
        Assert.Equal(0, keyring.DeleteCalls);
        keyring.StoreRelease.TrySetResult();
        await Task.WhenAll(connecting, forgetting);
        Assert.Empty(keyring.Entries);
        Assert.False(first.RememberPassword);
        Assert.False(second.RememberPassword);
        Assert.True(first.IsConnected);
        Assert.True(second.IsConnected);
    }

    [Fact]
    public async Task SavingRememberPreferenceAloneDoesNotPersistAnUnverifiedPassword()
    {
        var keyring = new MemoryKeyring();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), credentialStore: keyring);
        var tab = workspace.SelectedTab!;
        Configure(tab); tab.Password = Password; tab.RememberPassword = true;
        await tab.SaveProfileAsync();
        Assert.Equal(0, keyring.StoreCalls);
        Assert.True(Assert.Single(workspace.Profiles).RememberPassword);
    }

    [Fact]
    public async Task EntraConnectionDoesNotAccessPasswordKeyring()
    {
        var keyring = new MemoryKeyring();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => new Session(), keyring);
        var tab = workspace.SelectedTab!;
        Configure(tab); tab.RememberPassword = true; tab.AuthenticationIndex = 1;
        await tab.ConnectAsync();
        Assert.True(tab.IsConnected);
        Assert.False(tab.RememberPassword);
        Assert.Equal(0, keyring.GetCalls + keyring.StoreCalls + keyring.DeleteCalls);
    }

    [AvaloniaFact]
    public async Task RememberCheckboxAndForgetActionReflectAuthenticationAndSavedProfile()
    {
        var keyring = new MemoryKeyring();
        await using var workspace = new WorkspaceViewModel(new ProfileStore(), () => new Session(), keyring);
        var window = new MainWindow(workspace);
        window.Show();
        var tab = workspace.SelectedTab!;
        Configure(tab);
        var view = window.ActiveQueryView!;
        var checkbox = view.FindControl<CheckBox>("RememberPasswordCheckBox")!;
        var forget = view.FindControl<Button>("ForgetPasswordButton")!;
        Assert.True(checkbox.IsVisible);
        Assert.False(forget.IsEffectivelyEnabled);
        checkbox.IsChecked = true;
        Assert.True(tab.RememberPassword);
        await tab.SaveProfileAsync();
        Assert.True(forget.IsEffectivelyEnabled);
        tab.AuthenticationIndex = 1;
        Assert.False(checkbox.IsVisible);
        Assert.False(tab.RememberPassword);
        window.Close();
    }

    private static ConnectionProfile Profile() => new()
    { Name = "Test", Server = "test.database.windows.net", Database = "application", UserName = "developer", RememberPassword = true };
    private static void Configure(QueryTabViewModel tab)
    { tab.Server = "test.database.windows.net"; tab.Database = "application"; tab.UserName = "developer"; }

    private sealed class ProfileStore(params ConnectionProfile[] profiles) : IProfileStore
    {
        public IReadOnlyList<ConnectionProfile> Saved { get; private set; } = profiles;
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
        { Saved = profiles.ToArray(); return Task.CompletedTask; }
    }

    private sealed class MemoryKeyring : ICredentialStore
    {
        public Dictionary<CredentialIdentity, string> Entries { get; } = [];
        public Exception? ReadFailure { get; init; }
        public Exception? WriteFailure { get; init; }
        public Exception? DeleteFailure { get; set; }
        public Action? BeforeStore { get; set; }
        public TaskCompletionSource? StoreStarted { get; set; }
        public TaskCompletionSource? StoreRelease { get; set; }
        public int GetCalls { get; private set; }
        public int StoreCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public Task<string?> GetAsync(CredentialIdentity identity, CancellationToken cancellationToken)
        {
            GetCalls++;
            if (ReadFailure is not null) throw ReadFailure;
            return Task.FromResult(Entries.GetValueOrDefault(identity));
        }
        public async Task StoreAsync(CredentialIdentity identity, string label, string password, CancellationToken cancellationToken)
        {
            StoreCalls++; BeforeStore?.Invoke();
            if (WriteFailure is not null) throw WriteFailure;
            StoreStarted?.TrySetResult();
            if (StoreRelease is not null) await StoreRelease.Task.WaitAsync(cancellationToken);
            Entries[identity] = password;
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (DeleteFailure is not null) throw DeleteFailure;
            foreach (var key in Entries.Keys.Where(k => k.ProfileId == profileId).ToArray()) Entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class Session : IQuerySession
    {
        public bool FailLogin { get; init; }
        public TaskCompletionSource? LoginStarted { get; init; }
        public TaskCompletionSource? LoginRelease { get; init; }
        public bool IsConnected { get; private set; }
        public string? OpenPassword { get; private set; }
        public async Task OpenAsync(ConnectionProfile profile, string? password, CancellationToken cancellationToken)
        {
            if (FailLogin) throw new InvalidOperationException("Login failed.");
            LoginStarted?.TrySetResult();
            if (LoginRelease is not null) await LoginRelease.Task.WaitAsync(cancellationToken);
            OpenPassword = password; IsConnected = true;
        }
        public Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, int timeoutSeconds, IProgress<string>? messages, CancellationToken cancellationToken) => Task.FromResult(new QueryExecution([], 0, TimeSpan.Zero));
        public Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DatabaseObject>>([]);
        public Task<int> GetTransactionCountAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public ValueTask DisposeAsync() { IsConnected = false; OpenPassword = null; return ValueTask.CompletedTask; }
    }
}
