using System.Text;

namespace DesktopNook.Core;

public static class NotesMarkdown
{
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
            if (record.Kind == "text") output.AppendLine(WithStrikes(record.Text, record.BodyStrikes));
            else foreach (var task in record.TasksInDisplayOrder())
            {
                var lines = WithStrikes(task.Text, task.Strikes).ReplaceLineEndings("\n").Split('\n');
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
