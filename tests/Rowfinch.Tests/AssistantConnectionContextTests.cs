using Rowfinch.Core;
using Rowfinch.Infrastructure;
using Xunit;

namespace Rowfinch.Tests;

public sealed class AssistantConnectionContextTests
{
    [Fact]
    public async Task NotesPersistByActualTargetAndConcurrentWindowsKeepOtherConnections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rowfinch-context-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "assistant-contexts.json");
        var profile = new ConnectionProfile { Server = "example.database.windows.net", Database = "Orders", UserName = "reader" };
        try
        {
            var first = new AssistantConnectionContextStore(path); var second = new AssistantConnectionContextStore(path);
            await Task.WhenAll(first.SaveAsync(profile, "PO is the order header; POitems contains lines.", TestContext.Current.CancellationToken),
                second.SaveAsync(profile with { Database = "Inventory" }, "Products are stored in Items.", TestContext.Current.CancellationToken));
            Assert.Contains("POitems", await second.LoadAsync(profile with { Id = Guid.NewGuid(), Name = "Renamed", Server = "EXAMPLE.database.windows.net " }, TestContext.Current.CancellationToken));
            Assert.Equal("Products are stored in Items.", await first.LoadAsync(profile with { Database = "Inventory" }, TestContext.Current.CancellationToken));
            Assert.Empty(await first.LoadAsync(profile with { Database = "Other" }, TestContext.Current.CancellationToken));
            Assert.Empty(await first.LoadAsync(profile with { UserName = "writer" }, TestContext.Current.CancellationToken));
            Assert.Empty(await first.LoadAsync(profile with { Authentication = AuthenticationKind.EntraInteractive }, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => first.SaveAsync(profile, new string('x', AssistantConnectionContextStore.MaximumNotesLength + 1), TestContext.Current.CancellationToken));
            Assert.Contains("POitems", await second.LoadAsync(profile, TestContext.Current.CancellationToken));
            await second.SaveAsync(profile, "", TestContext.Current.CancellationToken);
            Assert.Empty(await first.LoadAsync(profile, TestContext.Current.CancellationToken));
            Assert.Equal("Products are stored in Items.", await first.LoadAsync(profile with { Database = "Inventory" }, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
