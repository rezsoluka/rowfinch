using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Rowfinch.Desktop.ViewModels;
using Rowfinch.Desktop.Views;

namespace Rowfinch.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var demo = desktop.Args?.Contains("--demo") == true;
            desktop.MainWindow = new MainWindow(new WorkspaceViewModel(), demo, showFirstRun: !demo);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
