using System.Text;
using System.Text.RegularExpressions;

namespace DesktopNook.Core;

public static partial class NotesTaskPaste
{
    private static readonly Regex Strike = new(@"(?:~~|\\~\\~)(?<text>.+?)(?:~~|\\~\\~)", RegexOptions.NonBacktracking);

    // Single-line clipboard content stays in the editor's normal paste flow.
    public static List<NoteTask> ParseMultiline(string text)
    {
        string normalized = text.ReplaceLineEndings("\n");
        if (!normalized.Contains('\n')) return [];
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return [];

        var tasks = new List<NoteTask>(lines.Length);
        foreach (var line in lines)
        {
            string body = ListPrefix().Replace(line, "", 1);
            var checkbox = Checkbox().Match(body);
            bool done = checkbox.Success && !string.IsNullOrWhiteSpace(checkbox.Groups["checked"].Value);
            if (checkbox.Success) body = body[checkbox.Length..];
            if (body.Length == 0) continue;

            var task = new NoteTask { Text = body, Done = done };
            var matches = Strike.Matches(body);
            if (matches.Count > 0)
            {
                var plain = new StringBuilder(body.Length);
                var marks = new List<NoteStrikeRange>(matches.Count);
                int cursor = 0;
                foreach (Match match in matches)
                {
                    plain.Append(body, cursor, match.Index - cursor);
                    var struck = match.Groups["text"];
                    marks.Add(new(plain.Length, struck.Length));
                    plain.Append(struck.Value);
                    cursor = match.Index + match.Length;
                }
                plain.Append(body, cursor, body.Length - cursor);
                task.Text = plain.ToString();
                task.Strikes = marks.ToArray();
            }
            tasks.Add(task);
        }
        return tasks;
    }

    [GeneratedRegex(@"^(?:[0-9]+[、)）]\s*|[0-9]+[.．]\s+|[-+*•]\s+)")]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"^\[(?<checked>[ xX])\](?:\s+|$)")]
    private static partial Regex Checkbox();

}
