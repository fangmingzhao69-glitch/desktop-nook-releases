using System.Text;

namespace DesktopNook.Core;

public static class NotesMarkdown
{
    private static string Heading(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("\\", "\\\\").Replace("#", "\\#").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");

    public static string Export(NotesState state, ProjectNote? note = null, NoteProject? project = null)
    {
        var output = new StringBuilder();
        void Record(ProjectNote record, int level)
        {
            output.Append(new string('#', level)).Append(' ').AppendLine(Heading(record.Title));
            if (record.Archived) output.AppendLine("（已归档）");
            output.AppendLine();
            if (record.Kind == "text") output.AppendLine(record.Text);
            else foreach (var task in record.Tasks)
            {
                var lines = task.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
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
                foreach (var record in state.Notes.Where(n => n.ProjectId == category.Id)) Record(record, 3);
            }
        }
        return output.ToString();
    }
}
