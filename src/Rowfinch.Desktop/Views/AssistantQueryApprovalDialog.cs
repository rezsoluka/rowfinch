using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Rowfinch.Core;

namespace Rowfinch.Desktop.Views;

public sealed class AssistantQueryApprovalDialog : Window
{
    public AssistantQueryApprovalDialog(AssistantQueryApproval request, string recipient = "OpenAI")
    {
        Title = "Approve assistant query"; Width = 720; Height = 570; MinWidth = 540; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var decline = new Button { Name = "DeclineQueryButton", Content = "Decline" };
        var approve = new Button { Name = "ApproveQueryButton", Content = "Run and share result" };
        approve.Classes.Add("primary");
        decline.Click += (_, _) => Close(false); approve.Click += (_, _) => Close(true);
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto,Auto"), RowSpacing = 12 };
        grid.Children.Add(new TextBlock { Text = "Connection: " + request.Target, TextWrapping = TextWrapping.Wrap });
        var reason = new TextBox { Text = request.Reason, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90 };
        ScrollViewer.SetVerticalScrollBarVisibility(reason, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Grid.SetRow(reason, 1); grid.Children.Add(reason);
        var sql = new TextBox { Name = "ApprovedSql", Text = request.Sql, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("DejaVu Sans Mono,monospace") };
        ScrollViewer.SetHorizontalScrollBarVisibility(sql, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(sql, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Grid.SetRow(sql, 2); grid.Children.Add(sql);
        var disclosure = new TextBlock { Text = $"Approval runs exactly this SQL on the connection above and shares the bounded result with {recipient} in this question. Limits: 100 rows, 100 columns, 15 seconds, 2000 text characters or 1500 binary bytes per cell, and bounded total output. Data may be sensitive. Decline runs nothing.", TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(disclosure, 3); grid.Children.Add(disclosure);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { decline, approve } };
        Grid.SetRow(buttons, 4); grid.Children.Add(buttons); Content = grid;
    }
}
