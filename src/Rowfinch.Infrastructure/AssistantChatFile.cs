using Rowfinch.Core;

namespace Rowfinch.Infrastructure;

public static class AssistantChatFile
{
    public static async Task<SavedAssistantChat> ReadAsync(Stream input, CancellationToken token = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = await input.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > SavedAssistantChat.MaximumFileBytes) throw new InvalidDataException("This chat file exceeds the size limit.");
            buffer.Write(chunk, 0, read);
        }
        return SavedAssistantChat.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    public static async Task WriteAsync(string path, SavedAssistantChat chat, CancellationToken token = default)
    {
        var bytes = chat.Serialize();
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, ".rowfinch-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
                await output.WriteAsync(bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
