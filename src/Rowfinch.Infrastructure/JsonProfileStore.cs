using System.Text.Json;
using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly string path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonProfileStore(string? path = null)
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configRoot) || !Path.IsPathRooted(configRoot))
            configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        this.path = path ?? Path.Combine(configRoot, "rowfinch", "connections.json");
    }

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(input, Options, cancellationToken) ?? [];
    }

    public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".connections-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(output, profiles, Options, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
