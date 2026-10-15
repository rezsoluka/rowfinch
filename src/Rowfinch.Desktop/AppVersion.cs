using System.Reflection;

namespace Rowfinch.Desktop;

/// <summary>The desktop assembly's build version, shared by the header and CLI.</summary>
public static class AppVersion
{
    public static string FullVersion { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    // Keep prerelease labels, but leave SDK-supplied commit metadata in the tooltip.
    public static string Version { get; } = FullVersion.Split('+')[0];
    public static string DisplayVersion => $"v{Version}";
    public static string BuildDetails => $"Rowfinch {FullVersion}";
}
