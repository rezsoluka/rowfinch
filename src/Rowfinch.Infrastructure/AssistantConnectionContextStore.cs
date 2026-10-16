using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

/// <summary>Explicit, local business notes scoped to an exact nonsecret connection target.</summary>
public sealed class AssistantConnectionContextStore(string? path = null)
{
    public const int MaximumNotesLength = 20_000;
    private readonly string path = Path.GetFullPath(path ?? Path.Combine(LocalSettingsStore.ConfigDirectory, "assistant-contexts.json"));
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    public static string Key(ConnectionProfile profile)
    {
        profile.Validate();
        var target = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Server = profile.Server.Trim().ToLowerInvariant(), Database = profile.Database.Trim(),
            User = profile.UserName.Trim(), Authentication = (int)profile.Authentication
        });
        return Convert.ToHexString(SHA256.HashData(target));
    }

    public async Task<string> LoadAsync(ConnectionProfile profile, CancellationToken token = default)
    {
        var key = Key(profile);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token);
        try { return (await ReadAsync(token)).GetValueOrDefault(key, ""); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ConnectionProfile profile, string notes, CancellationToken token = default)
    {
        if (notes.Length > MaximumNotesLength) throw new ArgumentException("Connection notes are limited to 20,000 characters.");
        var key = Key(profile);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var values = await ReadAsync(token);
            if (string.IsNullOrWhiteSpace(notes)) values.Remove(key); else values[key] = notes;
            if (values.Count > 200) throw new InvalidOperationException("Connection notes are limited to 200 connection targets.");
            var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".assistant-contexts-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(temporary, options))
                    await JsonSerializer.SerializeAsync(output, values, cancellationToken: token);
                token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        await using var input = File.OpenRead(path);
        if (input.Length > 32_000_000) throw new InvalidDataException("The saved connection notes file exceeds its size limit.");
        var values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(input, cancellationToken: token)
            ?? throw new InvalidDataException("The saved connection notes file is invalid.");
        if (values.Count > 200 || values.Any(pair => pair.Key.Length != 64 || !pair.Key.All(char.IsAsciiHexDigit) || pair.Value is null || pair.Value.Length > MaximumNotesLength))
            throw new InvalidDataException("The saved connection notes file is invalid.");
        return values;
    }
}
