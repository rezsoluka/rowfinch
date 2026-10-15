using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Rowfinch.Desktop.Views;

public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string action)
    {
        Title = title; Width = 470; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = "Keep working" };
        var confirm = new Button { Content = action };
        confirm.Classes.Add("primary");
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, confirm } }
            }
        };
    }
}
