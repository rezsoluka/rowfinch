namespace Rowfinch.Desktop.ViewModels;

public sealed class AssistantChatEntry(string question) : ObservableModel
{
    private string answer = "";
    private string status = "Assistant · receiving…";
    private string error = "";
    private string databaseFeedback = "";
    public string Question { get; } = question;
    public string Answer { get => answer; set => Set(ref answer, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string Error { get => error; set { if (Set(ref error, value)) Notify(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string DatabaseFeedback { get => databaseFeedback; set { if (Set(ref databaseFeedback, value)) Notify(nameof(HasDatabaseFeedback)); } }
    public bool HasDatabaseFeedback => DatabaseFeedback.Length > 0;
}
