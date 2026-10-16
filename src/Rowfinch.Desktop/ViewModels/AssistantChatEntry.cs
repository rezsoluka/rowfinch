namespace Rowfinch.Desktop.ViewModels;

public sealed class AssistantChatEntry(string question) : ObservableModel
{
    private string answer = "";
    private string status = "Assistant · receiving…";
    public string Question { get; } = question;
    public string Answer { get => answer; set => Set(ref answer, value); }
    public string Status { get => status; set => Set(ref status, value); }
}
