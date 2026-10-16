using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Rowfinch.Core;

namespace Rowfinch.Desktop.Views;

public sealed class ClaudeApiKeyDialog : Window
{
    public ClaudeApiKeyDialog(IApiKeyAssistantClient client, CancellationToken token)
    {
        Title = "Set up Claude"; Width = 580; Height = 550; MinWidth = 500; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var key = new TextBox { Name = "ClaudeApiKey", PasswordChar = '●', MaxLength = 4096, PlaceholderText = "Anthropic API key" };
        var workspace = new TextBox { Name = "ClaudeWorkspaceId", MaxLength = 200, PlaceholderText = "Optional workspace ID (wrkspc_…)" };
        var remember = new CheckBox { Name = "RememberClaudeKey", Content = "Remember in the desktop keyring", IsChecked = true };
        var status = new TextBlock { Name = "ClaudeSetupStatus", TextWrapping = TextWrapping.Wrap };
        var cancel = new Button { Name = "CancelClaudeSetup", Content = "Cancel" };
        var save = new Button { Name = "SaveClaudeKey", Content = "Validate and use key" }; save.Classes.Add("primary");
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var closeRegistration = token.Register(() => Dispatcher.UIThread.Post(() => { if (IsVisible) Close(false); }));
        bool busy = false;
        cancel.Click += (_, _) => { lifetime.Cancel(); Close(false); };
        Closed += (_, _) => { lifetime.Cancel(); key.Text = ""; closeRegistration.Dispose(); if (!busy) lifetime.Dispose(); };
        save.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; save.IsEnabled = key.IsEnabled = workspace.IsEnabled = remember.IsEnabled = false;
            status.Text = "Checking model access…";
            try
            {
                await client.ConfigureAsync(key.Text ?? "", workspace.Text, remember.IsChecked == true, lifetime.Token);
                if (!lifetime.IsCancellationRequested) Close(true);
            }
            catch (OperationCanceledException) { if (IsVisible) status.Text = "Cancelled."; }
            catch (Exception error) { if (IsVisible) status.Text = error is HttpRequestException ? "Could not reach Anthropic. Check your network connection." : error is CredentialStoreException ? "The desktop keyring could not store the key. Unlock it and retry, or uncheck Remember to use a session-only key." : error.Message; }
            finally
            {
                key.Text = ""; busy = false;
                save.IsEnabled = key.IsEnabled = workspace.IsEnabled = remember.IsEnabled = true;
                if (!IsVisible) lifetime.Dispose();
            }
        };
        var body = new StackPanel { Spacing = 14, Children =
        {
            new TextBlock { Text = "Use your own Anthropic API key", FontSize = 20, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Create a key in Claude Console. Requests use your Console API credits and billing. Review your spending limits there. Validation only lists models; it sends no chat or database data.", TextWrapping = TextWrapping.Wrap },
            key, workspace,
            new TextBlock { Text = "Workspace ID is required for keys that are not scoped to a single workspace. Find it in Console → Settings → Workspaces.", TextWrapping = TextWrapping.Wrap, FontSize = 11 },
            remember,
            new TextBlock { Text = "Remembered keys stay in Ubuntu’s keyring. Uncheck to use the key only until Rowfinch closes. Keys are excluded from chat files, connection notes and settings. Changing the key or workspace starts a new chat.", TextWrapping = TextWrapping.Wrap, FontSize = 11 },
            status
        } };
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = RowDefinitions.Parse("*,Auto"), RowSpacing = 14 };
        grid.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } };
        Grid.SetRow(actions, 1); grid.Children.Add(actions); Content = grid;
    }
}
