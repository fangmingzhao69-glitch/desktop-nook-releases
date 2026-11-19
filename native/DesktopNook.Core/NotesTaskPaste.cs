namespace DesktopNook.Core;

public static class NotesTaskPaste
{
    // Single-line clipboard content stays in the editor's normal paste flow.
    public static List<NoteTask> ParseMultiline(string text)
    {
        string normalized = text.ReplaceLineEndings("\n");
        // Clipboard data often includes the terminator of its last line.
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        if (!normalized.Contains('\n')) return [];
        return normalized.Split('\n').Select(line => new NoteTask { Text = line }).ToList();
    }
}
