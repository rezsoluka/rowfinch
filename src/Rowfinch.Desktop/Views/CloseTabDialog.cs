using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Rowfinch.Desktop.ViewModels;

namespace Rowfinch.Desktop.Views;

public enum CloseTabChoice { Cancel, Discard, Save }

public sealed class CloseTabDialog : Window
{
    public CloseTabDialog(QueryTabViewModel tab)
    {
        Title = $"Close {tab.DocumentName}?";
        Width = 520; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var discard = new Button { Content = tab.IsDirty ? "Close without saving" : "Close tab" };
        var save = new Button { Content = "Save and close", IsVisible = tab.IsDirty };
        save.Classes.Add("primary");
        cancel.Click += (_, _) => Close(CloseTabChoice.Cancel);
        discard.Click += (_, _) => Close(CloseTabChoice.Discard);
        save.Click += (_, _) => Close(CloseTabChoice.Save);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 20,
            Children =
            {
                new TextBlock { Text = tab.DocumentName, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = string.Join("\n\n", tab.CloseConcerns), TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, discard, save }
                }
            }
        };
    }
}
