using System.Collections.ObjectModel;
using Rowfinch.Core;

namespace Rowfinch.Desktop.ViewModels;

public sealed class ObjectBrowserNode : ObservableModel
{
    private readonly Func<ObjectBrowserNode, Task<IReadOnlyList<DatabaseColumn>?>>? loadColumns;
    private bool isExpanded;
    private bool loading;
    private bool isSelected;
    private bool updatingSelection;
    private readonly Action<bool>? selectionChanged;
    private readonly Dictionary<string, bool> selection = new(StringComparer.Ordinal);

    public ObjectBrowserNode(DatabaseObject item, Func<ObjectBrowserNode, Task<IReadOnlyList<DatabaseColumn>?>> loadColumns)
    {
        Object = item; Label = item.DisplayName; Detail = item.Kind;
        this.loadColumns = loadColumns;
        SelectAllColumnsCommand = new(_ => SetAllColumns(true), () => Columns is not null);
        UnselectAllColumnsCommand = new(_ => SetAllColumns(false), () => Columns is not null);
        if (item.HasColumns) Children.Add(new("Expand to load columns", ""));
    }

    private ObjectBrowserNode(string label, string detail) { Label = label; Detail = detail; }
    private ObjectBrowserNode(DatabaseColumn column, ObjectBrowserNode owner, bool selected, Action<bool> changed)
    {
        Column = column; Owner = owner; Label = column.Name; Detail = column.Description;
        isSelected = selected; selectionChanged = changed;
    }
    public DatabaseObject? Object { get; }
    public DatabaseColumn? Column { get; }
    public ObjectBrowserNode? Owner { get; }
    public string Label { get; }
    public string Detail { get; }
    public ObservableCollection<ObjectBrowserNode> Children { get; } = [];
    public IReadOnlyList<DatabaseColumn>? Columns { get; private set; }
    public ActionCommand? SelectAllColumnsCommand { get; }
    public ActionCommand? UnselectAllColumnsCommand { get; }
    public bool IsSelectableColumn => Column is { IsHidden: false };
    public bool HasColumnActions => Object?.HasColumns == true && IsExpanded && Columns is not null;
    public IReadOnlyList<DatabaseColumn> SelectedColumns => Children.Where(c => c.IsSelectableColumn && c.IsSelected).Select(c => c.Column!).ToArray();
    public string SelectionSummary => $"{SelectedColumns.Count} of {Children.Count(c => c.IsSelectableColumn)} selected";
    public bool IsSelected
    {
        get => isSelected;
        set { if (IsSelectableColumn && Set(ref isSelected, value)) selectionChanged?.Invoke(value); }
    }
    public Task LoadTask { get; private set; } = Task.CompletedTask;
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (!Set(ref isExpanded, value)) return;
            Notify(nameof(HasColumnActions));
            if (value && Columns is null && !loading && Object?.HasColumns == true)
                LoadTask = LoadColumnsAsync();
        }
    }

    public Task LoadColumnsAsync(bool refresh = false)
    {
        if (loading) return LoadTask;
        if (Object?.HasColumns != true || !refresh && Columns is not null) return Task.CompletedTask;
        return LoadTask = LoadColumnsCoreAsync();
    }

    private async Task LoadColumnsCoreAsync()
    {
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
                RefreshSelection();
            }
        }
        finally { loading = false; }
    }

    internal void SetColumns(IReadOnlyList<DatabaseColumn> columns)
    {
        var previousSelection = new Dictionary<string, bool>(selection, StringComparer.Ordinal);
        Columns = columns.ToArray();
        Children.Clear();
        selection.Clear();
        foreach (var column in columns.OrderBy(c => c.Ordinal))
        {
            var selected = !column.IsHidden && (!previousSelection.TryGetValue(column.Name, out var wasSelected) || wasSelected);
            selection[column.Name] = selected;
            Children.Add(new(column, this, selected, value => { selection[column.Name] = value; if (!updatingSelection) RefreshSelection(); }));
        }
        if (columns.Count == 0) Children.Add(new("No columns visible", "Check metadata permissions."));
        RefreshSelection();
    }

    private void SetAllColumns(bool value)
    {
        updatingSelection = true;
        try { foreach (var child in Children.Where(c => c.IsSelectableColumn)) child.IsSelected = value; }
        finally { updatingSelection = false; RefreshSelection(); }
    }

    private void RefreshSelection()
    {
        Notify(nameof(HasColumnActions)); Notify(nameof(SelectedColumns)); Notify(nameof(SelectionSummary));
        SelectAllColumnsCommand?.Refresh(); UnselectAllColumnsCommand?.Refresh();
    }
}
