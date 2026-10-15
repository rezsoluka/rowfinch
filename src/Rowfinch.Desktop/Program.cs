using Avalonia;

namespace Rowfinch.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine($"Rowfinch {AppVersion.Version}");
            return;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Rowfinch — Your SQL workspace\n\nUsage: rowfinch [--demo | --version | --help]\n\n  --demo     Open a sample workspace without a database connection.\n  --version  Print the application version.\n  --help     Show this help.");
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect().With(new X11PlatformOptions { WmClass = "Rowfinch" })
        .WithInterFont().LogToTrace();
}
