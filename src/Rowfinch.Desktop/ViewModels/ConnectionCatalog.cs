using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using Rowfinch.Core;

namespace Rowfinch.Desktop.ViewModels;

/// <summary>Shared saved profiles; serialize writes so two tabs cannot lose each other's changes.</summary>
public sealed class ConnectionCatalog(IProfileStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim credentialGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, long> credentialVersions = new();
    private bool initialized;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public event Action<Guid>? PasswordForgotten;
    public long GetCredentialVersion(Guid profileId) => credentialVersions.GetValueOrDefault(profileId);
    public void NotifyPasswordForgotten(Guid profileId)
    {
        credentialVersions.AddOrUpdate(profileId, 1, (_, version) => version + 1);
        PasswordForgotten?.Invoke(profileId);
    }
    public async Task WithCredentialLockAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await credentialGate.WaitAsync(cancellationToken);
        try { await action(); }
        finally { credentialGate.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            foreach (var profile in await store.LoadAsync(cancellationToken)) Profiles.Add(profile);
            initialized = true;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = Profiles.Where(p => p.Id != profile.Id).Append(profile).ToArray();
            await store.SaveAsync(profiles, cancellationToken);
            var index = Profiles.ToList().FindIndex(p => p.Id == profile.Id);
            if (index >= 0) Profiles[index] = profile;
            else Profiles.Add(profile);
        }
        finally { gate.Release(); }
    }
}
