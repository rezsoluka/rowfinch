using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Rowfinch.Core;

namespace Rowfinch.Desktop.Views;

public partial class ResultModelDialog : Window
{
    private readonly QueryResultSet result;

    public ResultModelDialog() : this(new QueryResultSet("Result", [], [], false)) { }

    public ResultModelDialog(QueryResultSet result, bool json = false)
    {
        InitializeComponent();
        this.result = result;
        ResultDescription.Text = $"{result.Name} · {result.Columns.Count} columns · {result.Rows.Count:N0} displayed rows" +
            (result.IsTruncated ? " · display limit applied" : "");
        ModelFormat.SelectedIndex = json ? 1 : 0;
        ModelFormat.SelectionChanged += (_, _) => RefreshPreview();
        ClassNameInput.TextChanged += (_, _) => RefreshPreview();
        UseFirstRow.IsCheckedChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    private bool IsJson => ModelFormat.SelectedIndex == 1;

    private void RefreshPreview()
    {
        ClassNameRow.IsVisible = !IsJson;
        UseFirstRow.IsVisible = IsJson;
        GenerationStatus.Text = "";
        GenerationHint.Text = IsJson
            ? "Without a first row, each property is null. Duplicate or unnamed columns receive unique property names."
            : "Annotations use reported column metadata. Unknown nullability allows null; keys and relationships are not inferred from names.";
        GeneratedText.Text = IsJson ? JsonExporter.Object(result, UseFirstRow.IsChecked == true)
            : CSharpResultGenerator.Generate(result, ClassNameInput.Text ?? "");
    }

    private void OnClose(object? sender, RoutedEventArgs args) => Close();

    private async void OnCopy(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (Clipboard is not { } clipboard) { GenerationStatus.Text = "Clipboard is unavailable."; return; }
            await clipboard.SetTextAsync(GeneratedText.Text ?? "");
            GenerationStatus.Text = "Copied to clipboard.";
        }
        catch (Exception error) { GenerationStatus.Text = error.Message; }
    }

    private async void OnSave(object? sender, RoutedEventArgs args)
    {
        try
        {
            var text = GeneratedText.Text ?? "";
            var extension = IsJson ? "json" : "cs";
            var name = IsJson ? "query-result" : CSharpResultGenerator.ClassName(ClassNameInput.Text ?? "");
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = IsJson ? "Save JSON object" : "Save C# class", SuggestedFileName = $"{name}.{extension}",
                DefaultExtension = extension, ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(IsJson ? "JSON" : "C#") { Patterns = [$"*.{extension}"] }]
            });
            if (file is null) return;
            await using var output = await file.OpenWriteAsync();
            output.SetLength(0);
            await using var writer = new StreamWriter(output, new UTF8Encoding(false));
            await writer.WriteAsync(text);
            await writer.FlushAsync();
            GenerationStatus.Text = $"Saved {file.Name}.";
        }
        catch (Exception error) { GenerationStatus.Text = error.Message; }
    }
}
