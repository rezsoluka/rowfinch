using System.Collections.ObjectModel;
using Rowfinch.Core;

namespace Rowfinch.Desktop.ViewModels;

public sealed class ObjectBrowserNode : ObservableModel
{
    private readonly Func<ObjectBrowserNode, Task<IReadOnlyList<DatabaseColumn>?>>? loadColumns;
    private bool isExpanded;
    private bool loading;

    public ObjectBrowserNode(DatabaseObject item, Func<ObjectBrowserNode, Task<IReadOnlyList<DatabaseColumn>?>> loadColumns)
    {
        Object = item; Label = item.DisplayName; Detail = item.Kind;
        this.loadColumns = loadColumns;
        if (item.HasColumns) Children.Add(new("Expand to load columns", ""));
    }

    private ObjectBrowserNode(string label, string detail) { Label = label; Detail = detail; }
    public DatabaseObject? Object { get; }
    public string Label { get; }
    public string Detail { get; }
    public ObservableCollection<ObjectBrowserNode> Children { get; } = [];
    public IReadOnlyList<DatabaseColumn>? Columns { get; private set; }
    public Task LoadTask { get; private set; } = Task.CompletedTask;
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value) && value && Columns is null && !loading && Object?.HasColumns == true)
                LoadTask = LoadColumnsAsync();
        }
    }

    public async Task LoadColumnsAsync(bool refresh = false)
    {
        if (loading || Object?.HasColumns != true || !refresh && Columns is not null) return;
        loading = true;
        Children.Clear(); Children.Add(new("Loading columns…", ""));
        try
        {
            var columns = await loadColumns!(this);
            if (columns is not null) SetColumns(columns);
            else
            {
                Columns = null;
                Children.Clear(); Children.Add(new("Columns unavailable", "Right-click the object to refresh; see Messages."));
            }
        }
        finally { loading = false; }
    }

    internal void SetColumns(IReadOnlyList<DatabaseColumn> columns)
    {
        Columns = columns.ToArray();
        Children.Clear();
        foreach (var column in columns.OrderBy(c => c.Ordinal)) Children.Add(new(column.Name, column.Description));
        if (columns.Count == 0) Children.Add(new("No columns visible", "Check metadata permissions."));
    }
}
