using System.Text;

namespace DesktopNook.Core;

public static class NotesMarkdown
{
    private static string Formatted(string text, NoteStrikeRange[]? strikes, NoteHighlightRange[]? highlights, NoteBoldRange[]? bolds)
    {
        if (highlights is not { Length: > 0 } && bolds is not { Length: > 0 }) return WithStrikes(text, strikes);
        var boundaries = new SortedSet<int> { 0, text.Length };
        foreach (var range in highlights ?? []) { boundaries.Add(range.Start); boundaries.Add(range.Start + range.Length); }
        foreach (var range in bolds ?? []) { boundaries.Add(range.Start); boundaries.Add(range.Start + range.Length); }
        foreach (var range in strikes ?? []) { boundaries.Add(range.Start); boundaries.Add(range.Start + range.Length); }
        var output = new StringBuilder();
        var points = boundaries.ToArray();
        for (int i = 0; i + 1 < points.Length; i++)
        {
            int start = points[i], length = points[i + 1] - start;
            var highlight = (highlights ?? []).FirstOrDefault(range => range.Start <= start && range.Start + range.Length >= start + length);
            bool bold = (bolds ?? []).Any(range => range.Start <= start && range.Start + range.Length >= start + length);
            bool struck = (strikes ?? []).Any(range => range.Start <= start && range.Start + range.Length >= start + length);
            var lines = text.Substring(start, length).ReplaceLineEndings("\n").Split('\n');
            for (int line = 0; line < lines.Length; line++)
            {
                if (line > 0) output.Append('\n');
                if (lines[line].Length == 0) continue;
                if (highlight.Length > 0) output.Append("<mark style=\"background-color:").Append(highlight.Color).Append("\">");
                if (struck) output.Append("<s>");
                if (bold) output.Append("<strong>");
                output.Append(System.Net.WebUtility.HtmlEncode(lines[line]));
                if (bold) output.Append("</strong>");
                if (struck) output.Append("</s>");
                if (highlight.Length > 0) output.Append("</mark>");
            }
        }
        return output.ToString();
    }
    private static string Heading(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("\\", "\\\\").Replace("#", "\\#").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");

    private static string WithStrikes(string text, NoteStrikeRange[]? strikes)
    {
        if (strikes is not { Length: > 0 }) return text;
        var output = new StringBuilder();
        int cursor = 0;
        foreach (var mark in strikes)
        {
            output.Append(text, cursor, mark.Start - cursor);
            var parts = text.Substring(mark.Start, mark.Length).ReplaceLineEndings("\n").Split('\n');
            for (int index = 0; index < parts.Length; index++)
            {
                if (index > 0) output.Append('\n');
                string part = parts[index];
                string trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    output.Append(part);
                    continue;
                }
                int leading = part.Length - part.TrimStart().Length;
                output.Append(part, 0, leading).Append("~~").Append(trimmed.Replace("~", "\\~")).Append("~~")
                    .Append(part, leading + trimmed.Length, part.Length - leading - trimmed.Length);
            }
            cursor = mark.Start + mark.Length;
        }
        return output.Append(text, cursor, text.Length - cursor).ToString();
    }

    public static string Export(NotesState state, ProjectNote? note = null, NoteProject? project = null)
    {
        var output = new StringBuilder();
        void Record(ProjectNote record, int level)
        {
            output.Append(new string('#', level)).Append(' ').AppendLine(Heading(record.Title));
            if (record.Archived) output.AppendLine("（已归档）");
            output.AppendLine();
            if (record.Kind == "text") output.AppendLine(Formatted(record.Text, record.BodyStrikes, record.BodyHighlights, record.BodyBolds));
            else foreach (var task in record.TasksInDisplayOrder())
            {
                var lines = Formatted(task.Text, task.Strikes, task.Highlights, task.Bolds).ReplaceLineEndings("\n").Split('\n');
                output.Append(task.Done ? "- [x] " : "- [ ] ").AppendLine(lines[0]);
                foreach (var line in lines.Skip(1)) output.Append("      ").AppendLine(line);
            }
            output.AppendLine();
        }
        if (note is not null) Record(note, 1);
        else
        {
            output.AppendLine("# 便签").AppendLine();
            foreach (var category in state.Projects.Where(p => project is null || p.Id == project.Id))
            {
                output.Append("## ").AppendLine(Heading(category.Name));
                if (category.Archived) output.AppendLine("（已归档分类）");
                output.AppendLine();
                foreach (var record in state.Notes.Where(n => n.ProjectId == category.Id).OrderBy(n => n.IsCompletedChecklist()))
                    Record(record, 3);
            }
        }
        return output.ToString();
    }
}
