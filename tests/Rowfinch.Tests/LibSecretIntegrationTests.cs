using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class LibSecretIntegrationTests
{
    public static bool IntegrationEnabled => Environment.GetEnvironmentVariable("ROWFINCH_KEYRING_TEST") == "1";

    [Fact(SkipUnless = nameof(IntegrationEnabled), Skip = "Set ROWFINCH_KEYRING_TEST=1 to test a temporary credential in the desktop keyring.")]
    public async Task NativeKeyringStoresLooksUpUpdatesAndForgetsOnlyItsTestProfile()
    {
        var store = new LibSecretCredentialStore();
        var profile = new ConnectionProfile
        { Id = Guid.NewGuid(), Name = "Temporary Rowfinch integration test", Server = "test.invalid", Database = "test", UserName = "test" };
        var identity = CredentialIdentity.FromProfile(profile);
        var otherTarget = CredentialIdentity.FromProfile(profile with { Database = "other" });
        var secret = "synthetic-" + Guid.NewGuid().ToString("N") + "-é\nsemicolon;quote\"";
        var token = TestContext.Current.CancellationToken;
        try
        {
            Assert.Null(await store.GetAsync(identity, token));
            await store.StoreAsync(identity, profile.Name, secret, token);
            Assert.Equal(secret, await store.GetAsync(identity, token));
            Assert.Null(await store.GetAsync(otherTarget, token));
            await store.StoreAsync(identity, profile.Name, secret + "-updated", token);
            Assert.Equal(secret + "-updated", await store.GetAsync(identity, token));
            await store.StoreAsync(otherTarget, profile.Name, "second-synthetic-secret", token);
            await store.DeleteAsync(profile.Id, token);
            Assert.Null(await store.GetAsync(identity, token));
            Assert.Null(await store.GetAsync(otherTarget, token));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await store.DeleteAsync(profile.Id, cleanup.Token);
        }
    }

    [Fact]
    public async Task AlreadyCancelledRequestNeverTouchesDesktopKeyring()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var store = new LibSecretCredentialStore();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync(new CredentialIdentity(Guid.NewGuid(), "test"), cancelled.Token));
    }
}
