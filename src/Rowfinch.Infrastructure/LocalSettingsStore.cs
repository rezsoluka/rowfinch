using System.Text.Json;

namespace Rowfinch.Infrastructure;

public sealed class LocalSettings
{
    public int SafetyNoticeVersion { get; set; }
    public bool ChatGptWelcomeShown { get; set; }
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid().ToString();
}

public sealed class LocalSettingsStore(string? path = null)
{
    private readonly string path = path ?? Path.Combine(ConfigDirectory, "settings.json");
    public string FilePath => Path.GetFullPath(path);
    private readonly SemaphoreSlim gate = new(1, 1);
    public static string ConfigDirectory
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "rowfinch");
        }
    }

    public async Task<LocalSettings> LoadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return await ReadAsync(token); }
        finally { gate.Release(); }
    }

    public async Task UpdateAsync(Action<LocalSettings> update, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var settings = await ReadAsync(token);
            update(settings);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(temporary, options))
                    await JsonSerializer.SerializeAsync(output, settings, cancellationToken: token);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task<LocalSettings> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(path)) return new();
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LocalSettings>(input, cancellationToken: token) ?? new();
    }
}
