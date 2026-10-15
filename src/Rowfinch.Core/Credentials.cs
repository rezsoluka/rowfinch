using System.Security.Cryptography;
using System.Text.Json;

namespace Rowfinch.Core;

public sealed record CredentialIdentity(Guid ProfileId, string Target)
{
    public static CredentialIdentity FromProfile(ConnectionProfile profile)
    {
        if (profile.Authentication != AuthenticationKind.SqlPassword)
            throw new ArgumentException("Only SQL passwords belong in this credential store.");
        var target = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            profile.Server.Trim().ToLowerInvariant(), profile.Database.Trim(), profile.UserName.Trim()
        });
        return new CredentialIdentity(profile.Id, Convert.ToHexString(SHA256.HashData(target)));
    }
}

public interface ICredentialStore
{
    Task<string?> GetAsync(CredentialIdentity identity, CancellationToken cancellationToken);
    Task StoreAsync(CredentialIdentity identity, string label, string password, CancellationToken cancellationToken);
    // Removes only Rowfinch SQL passwords associated with this profile, including old targets.
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken);
}

public enum CredentialStoreFailure { Unavailable, LockedOrDenied, TimedOut, Failed }

public sealed class CredentialStoreException(CredentialStoreFailure failure) : Exception(MessageFor(failure))
{
    public CredentialStoreFailure Failure { get; } = failure;
    public static string MessageFor(CredentialStoreFailure failure) => failure switch
    {
        CredentialStoreFailure.Unavailable => "The desktop keyring is unavailable. Start a desktop session with a Secret Service keyring, or use a session-only password.",
        CredentialStoreFailure.LockedOrDenied => "The desktop keyring is locked or access was denied. Unlock it and try again, or use a session-only password.",
        CredentialStoreFailure.TimedOut => "The desktop keyring request timed out. Unlock it and try again, or use a session-only password.",
        _ => "The desktop keyring could not complete the request. Unlock it and try again, or use a session-only password."
    };
}
