using System.ComponentModel;
using System.Text;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Rowfinch.Core;
using Rowfinch.Desktop.ViewModels;

namespace Rowfinch.Desktop.Views;

public partial class QueryTabView : UserControl
{
    private readonly QueryTabViewModel workspace;
    private static readonly FilePickerFileType SqlFiles = new("SQL scripts") { Patterns = ["*.sql"] };

    public QueryTabView() : this(new QueryTabViewModel()) { }

    public QueryTabView(QueryTabViewModel workspace)
    {
        InitializeComponent();
        this.workspace = workspace;
        DataContext = workspace;
        SqlEditor.Text = workspace.Sql;
        SqlEditor.Options.ConvertTabsToSpaces = true;
        SqlEditor.Options.IndentationSize = 4;
        using var highlighting = XmlReader.Create(new StringReader(SqlHighlighting));
        SqlEditor.SyntaxHighlighting = HighlightingLoader.Load(highlighting, HighlightingManager.Instance);
        SqlEditor.TextChanged += (_, _) => workspace.Sql = SqlEditor.Text;
        workspace.PropertyChanged += OnWorkspaceChanged;
        ShowResult(workspace.SelectedResult);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(workspace.Sql) && SqlEditor.Text != workspace.Sql) SqlEditor.Text = workspace.Sql;
        if (args.PropertyName == nameof(workspace.SelectedResult)) ShowResult(workspace.SelectedResult);
    }

    private void ShowResult(QueryResultSet? result)
    {
        ResultsTable.ItemsSource = null;
        ResultsTable.Columns.Clear();
        if (result is null) return;
        for (var i = 0; i < result.Columns.Count; i++)
        {
            var index = i;
            var column = result.Columns[i];
            ResultsTable.Columns.Add(new TableViewColumn
            {
                Header = string.IsNullOrEmpty(column.Name) ? "(unnamed)" : column.Name,
                Width = new GridLength(170),
                CellTemplate = new FuncDataTemplate<object?[]>((row, _) =>
                {
                    var value = row?[index];
                    var text = CellFormatter.Format(value);
                    var cell = new SelectableTextBlock
                    {
                        Text = text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ↵ ", StringComparison.Ordinal),
                        Foreground = value is null ? new SolidColorBrush(Color.Parse("#819693")) : Brushes.WhiteSmoke,
                        FontSize = 12, MaxHeight = 28, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };
                    ToolTip.SetTip(cell, $"{column.TypeName}\n{text}");
                    return cell;
                })
            });
        }
        ResultsTable.ItemsSource = result.Rows;
    }

    private async void OnRun(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => await RunQueryAsync();
    private Task RunQueryAsync() => workspace.CanExecute
        ? workspace.ExecuteAsync(SqlEditor.SelectedText) : Task.CompletedTask;

    private async void OnDisconnect(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (!workspace.CanExecute) return;
        if (workspace.TransactionCount != 0 && !await new ConfirmDialog("Disconnect?",
            "An open or unverified transaction will be rolled back when this connection closes.", "Disconnect").ShowDialog<bool>(Owner)) return;
        await workspace.DisconnectAsync();
    }

    public async Task HandleShortcutAsync(KeyEventArgs args)
    {
        if (args.Key == Key.F5 || args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control))
        { args.Handled = true; await RunQueryAsync(); }
        else if (args.Key == Key.Escape && workspace.IsBusy) { args.Handled = true; workspace.Cancel(); }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.O)
        { args.Handled = true; OpenSqlRequested?.Invoke(this, EventArgs.Empty); }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.S)
        { args.Handled = true; await SaveSqlAsync(); }
    }

    public QueryTabViewModel Tab => workspace;
    public event EventHandler? OpenSqlRequested;
    private Window Owner => TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("The query tab is not attached to a window.");
    private void OnOpenSql(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => OpenSqlRequested?.Invoke(this, EventArgs.Empty);

    private async void OnSaveSql(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => await SaveSqlAsync();
    public async Task<bool> SaveSqlAsync(Window? owner = null)
    {
        try
        {
            var text = workspace.Sql;
            var file = await (owner ?? Owner).StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save SQL script", SuggestedFileName = workspace.DocumentName, DefaultExtension = "sql", FileTypeChoices = [SqlFiles], ShowOverwritePrompt = true });
            if (file is null) return false;
            await using (var output = await file.OpenWriteAsync())
            {
                output.SetLength(0);
                await using var writer = new StreamWriter(output, new UTF8Encoding(false));
                await writer.WriteAsync(text);
            }
            if (workspace.Sql != text) return false;
            workspace.MarkSaved(file.Name);
            return true;
        }
        catch (Exception error) { workspace.ReportError(error); return false; }
    }

    private async void OnExportCsv(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (workspace.IsBusy || workspace.SelectedResult is not { } result) return;
        try
        {
            var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export displayed rows", SuggestedFileName = "rowfinch-results.csv", DefaultExtension = "csv", FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }], ShowOverwritePrompt = true });
            if (file is null) return;
            await workspace.RunOperationAsync(async token =>
            {
                await using var output = await file.OpenWriteAsync();
                output.SetLength(0);
                await CsvExporter.WriteAsync(result, output, token);
            }, "Exporting displayed rows…", $"Exported {result.Rows.Count:N0} rows");
        }
        catch (Exception error) { workspace.ReportError(error); }
    }

    private async void OnCopyRows(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null || workspace.SelectedResult is not { } result) return;
            var rows = ResultsTable.SelectedItems?.Cast<object?[]>().ToArray() ?? [];
            var text = new StringBuilder(string.Join('\t', result.Columns.Select(c => c.Name))).AppendLine();
            foreach (var row in rows)
                text.AppendLine(string.Join('\t', row.Select(CellFormatter.Format)));
            await clipboard.SetTextAsync(text.ToString());
        }
        catch (Exception error) { workspace.ReportError(error); }
    }

    private async void OnObjectDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (!workspace.CanExecute || sender is not Control { DataContext: ObjectBrowserNode { Object: { } item } node }) return;
        args.Handled = true;
        if (item.HasColumns) node.IsExpanded = !node.IsExpanded;
        else if (item.HasDefinition) await workspace.OpenDefinitionAsync(node);
    }

    private void OnObjectContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (sender is not Control { DataContext: ObjectBrowserNode { Object: { } item } node } control) return;
        args.Handled = true;
        var menu = new ContextMenu();
        var actions = new List<Control>();
        void Add(string label, Func<Task> action)
        {
            var entry = new MenuItem { Header = label, IsEnabled = workspace.CanExecute };
            entry.Click += async (_, _) =>
            {
                try { if (workspace.CanExecute) await action(); }
                catch (Exception error) { workspace.ReportError(error); }
            };
            actions.Add(entry);
        }
        if (item.HasColumns)
        {
            Add("Select top 100", () => workspace.PreviewRowsAsync(node));
            Add("Generate SELECT in new tab", () => workspace.GenerateSelectAsync(node));
        }
        if (item.HasDefinition) Add("Open definition in new tab", () => workspace.OpenDefinitionAsync(node));
        actions.Add(new Separator());
        Add("Copy qualified name", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(item.QuotedName);
        });
        if (item.HasColumns) Add("Refresh columns", () => workspace.RefreshObjectAsync(node));
        menu.ItemsSource = actions;
        control.ContextMenu = menu;
        menu.Open(control);
    }

    public void Detach() => workspace.PropertyChanged -= OnWorkspaceChanged;

    private const string SqlHighlighting = """
        <SyntaxDefinition name="SQL" extensions=".sql" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#80958C" fontStyle="italic" />
          <Color name="String" foreground="#E2C48D" />
          <Color name="Keyword" foreground="#83D5B6" fontWeight="bold" />
          <Color name="Number" foreground="#C7B3EA" />
          <RuleSet ignoreCase="true">
            <Span color="Comment" begin="--" />
            <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
            <Span color="String" multiline="true" begin="'" end="'" />
            <Keywords color="Keyword">
              <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>JOIN</Word><Word>INNER</Word><Word>LEFT</Word><Word>RIGHT</Word><Word>FULL</Word><Word>OUTER</Word><Word>ON</Word><Word>AS</Word><Word>AND</Word><Word>OR</Word><Word>NOT</Word><Word>NULL</Word><Word>IS</Word><Word>IN</Word><Word>LIKE</Word><Word>BETWEEN</Word><Word>TOP</Word><Word>DISTINCT</Word><Word>ORDER</Word><Word>GROUP</Word><Word>BY</Word><Word>HAVING</Word><Word>ASC</Word><Word>DESC</Word><Word>INSERT</Word><Word>INTO</Word><Word>VALUES</Word><Word>UPDATE</Word><Word>SET</Word><Word>DELETE</Word><Word>CREATE</Word><Word>ALTER</Word><Word>DROP</Word><Word>TABLE</Word><Word>VIEW</Word><Word>PROCEDURE</Word><Word>EXEC</Word><Word>EXECUTE</Word><Word>DECLARE</Word><Word>BEGIN</Word><Word>END</Word><Word>TRANSACTION</Word><Word>COMMIT</Word><Word>ROLLBACK</Word><Word>GO</Word><Word>WITH</Word><Word>UNION</Word><Word>ALL</Word><Word>CASE</Word><Word>WHEN</Word><Word>THEN</Word><Word>ELSE</Word><Word>CAST</Word><Word>CONVERT</Word><Word>COUNT</Word><Word>SUM</Word><Word>AVG</Word><Word>MIN</Word><Word>MAX</Word><Word>PRINT</Word><Word>WAITFOR</Word><Word>DELAY</Word>
            </Keywords>
            <Rule color="Number">\b[0-9]+(\.[0-9]+)?\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
