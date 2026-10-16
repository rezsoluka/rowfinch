using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rowfinch.Desktop.Views;

public sealed class SafetyPrivacyDialog : Window
{
    public const int NoticeVersion = 2;
    public const string Notice = """
        DATA SAFETY

        Rowfinch is beta software. SQL you run can change or permanently delete database data. Review queries and generated scripts before execution, keep appropriate backups, and use database permissions suited to your work. Exported CSV/JSON files and copied results may contain sensitive information; protect them accordingly.

        CONNECTIONS AND LOCAL DATA

        Connection profiles contain server/database/user settings and preferences, never SQL passwords or access tokens. SQL session credentials stay privately in memory while connected tabs use them. Optional remembered SQL passwords are stored in the desktop keyring. Each query tab has an independent database session.

        SQL documents and exports are saved where you choose. Displayed results stay in memory until replaced or the app closes. Preferences and the first-launch acknowledgement are stored in Rowfinch's configuration directory. Removing the app does not remove profiles, keyring entries, documents, or exports.

        NETWORK AND TELEMETRY

        Database operations communicate with your configured SQL server. Entra authentication communicates with Microsoft's identity services. Rowfinch has no application analytics or telemetry. Identity and database providers may process requests under their own policies.

        OPTIONAL CHATGPT ASSISTANT

        The assistant connects to OpenAI only when you use its sign-in or assistant features. Eligible requests use your ChatGPT plan or credits, subject to account/workspace limits. Model availability and data handling follow OpenAI's applicable terms and your account settings.

        Each message sends the request preview shown in the assistant, plus Rowfinch's SQL-assistance instructions. Follow-ups also include earlier completed messages, selected context and approved database results from this chat; review them under Earlier messages and shared data in Context. Turning sharing off does not remove earlier shared data. New chat clears the conversation, snapshots and activity; changing accounts or database connections/access also clears the chat. No transcript is written to disk unless you choose Save chat. Saved chat files are unencrypted and include completed messages, shared SQL/schema/results, opaque model state and your draft. Keep them private. Opening a file restores local history with database access off; the next Send shares that history. New queries still require approval. Credentials and access grants are not saved in chats. Sharing editor SQL, loaded schema, or a sample of displayed rows is optional and off by default. Review the preview for sensitive information before Send. Password fields and connection credentials are never added to assistant requests. A SQL document itself can contain secrets you entered, so review it before sharing.

        ChatGPT tokens and saved account registrations are stored in a separate desktop-keyring entry. The installation host ID and notice preferences are stored locally. Questions and answers are kept only in the assistant window's memory, unless you copy or save them yourself. Closing the assistant cancels its pending request; Sign out attempts remote session revocation and removes that account's tokens locally.

        Database access is separate, optional, and off by default. Enabling schema inspection allows the assistant to fetch visible object names and columns and share them with OpenAI during a question. Enabling queries requires a separate session using a dedicated read-only database identity, effective permission checks, restricted SELECT syntax, and your approval of each query before execution and sharing. Results are bounded to 100 rows, 100 columns, 15 seconds, limited cell sizes and total output. The activity view shows requests, decisions and shared results in memory. Disabling access, disconnecting/closing its selected database tab, changing ChatGPT accounts, or closing the assistant revokes access and cancels pending work. Access permissions are never saved for the next window or launch.

        The assistant cannot execute data-changing SQL. Other generated SQL remains a suggestion: review it and run it yourself when ready. No existing ChatGPT conversation history is imported.

        LICENSE

        Copyright (c) 2026 Ernesto Rodriguez and Rowfinch contributors. Rowfinch is distributed under the MIT License, with no warranty and the license's limitation of liability. Third-party components retain their own licenses. This notice explains the application's behavior and does not replace the license or applicable law.
        """;

    public SafetyPrivacyDialog(bool firstLaunch = false) : this(firstLaunch, Notice) { }

    public SafetyPrivacyDialog(bool firstLaunch, string notice, string? dismissLabel = null)
    {
        Title = firstLaunch ? "Welcome to Rowfinch" : "About Rowfinch · Safety and privacy";
        Width = 760; Height = 690; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var proceed = new Button { Name = "AcknowledgeNotice", Content = dismissLabel ?? (firstLaunch ? "I understand · Continue" : "Close") };
        proceed.Classes.Add("primary");
        proceed.Click += (_, _) => Close(true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (firstLaunch)
        {
            var exit = new Button { Content = "Close app" };
            exit.Click += (_, _) => Close(false);
            actions.Children.Add(exit);
        }
        actions.Children.Add(proceed);
        var text = new TextBox { Name = "SafetyPrivacyText", Text = notice, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ScrollViewer.SetVerticalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22), RowSpacing = 14 };
        grid.Children.Add(new TextBlock { Text = firstLaunch ? "Before using Rowfinch" : $"Rowfinch {AppVersion.DisplayVersion}", FontSize = 21, FontWeight = FontWeight.SemiBold });
        Grid.SetRow(text, 1); grid.Children.Add(text);
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        Content = grid;
    }
}
