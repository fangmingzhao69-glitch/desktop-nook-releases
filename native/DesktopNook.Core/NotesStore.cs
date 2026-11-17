using System.Text.Json;

namespace DesktopNook.Core;

public sealed class NoteProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public bool Archived { get; set; }
    public string? DeleteBatch { get; set; }
    public DateTime? DeletedUtc { get; set; }
    public string Search { get; set; } = "";
    public double Scroll { get; set; }
    public string? SelectedNote { get; set; }
    public string EditorKey { get; set; } = "";
    public int Caret { get; set; }
    public int SelectionLength { get; set; }
}
public sealed class NoteTask
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}
public sealed class ProjectNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    public List<NoteTask> Tasks { get; set; } = [];
    public bool Favorite { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; set; }
    public string? DeleteBatch { get; set; }
}
public sealed class NotesState
{
    public int Format { get; set; } = 1;
    public long Revision { get; set; }
    public List<NoteProject> Projects { get; set; } = [];
    public List<ProjectNote> Notes { get; set; } = [];
    public string? ActiveProject { get; set; }
    public double LeftFraction { get; set; } = .646;
}

// A single atomic snapshot makes project moves/deletes/restores transactional.
// This store never touches launcher metadata. UI drafts live separately until Commit succeeds.
public sealed class NotesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "notes.json");
    public NotesState State { get; private set; } = new();
    public string? RecoveryMessage { get; private set; }
    private bool _blocked;
    public NotesStore(string dataRoot)
    {
        DirectoryPath = Path.Combine(dataRoot, "Notes");
        if (!File.Exists(FilePath) && !File.Exists(FilePath + ".bak")) return;
        try { State = Read(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            try
            {
                State = Read(FilePath + ".bak");
                // Preserve broken bytes before allowing another commit, keeping the valid backup intact.
                if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".damaged-" + Guid.NewGuid().ToString("N"));
                RecoveryMessage = "便签主文件异常，已读取有效备份；损坏原文件已保留。";
            }
            catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { _blocked = true; RecoveryMessage = "便签文件及备份无法读取，已停止写入。请从数据目录保留原文件后恢复备份。"; }
        }
    }
    private static NotesState Read(string path)
    {
        var state = JsonSerializer.Deserialize<NotesState>(File.ReadAllText(path), Json) ?? throw new IOException("便签为空。");
        Validate(state); return state;
    }
    public NotesState Draft() => JsonSerializer.Deserialize<NotesState>(JsonSerializer.Serialize(State, Json), Json)!;
    public void Commit(NotesState draft)
    {
        if (_blocked) throw new IOException(RecoveryMessage);
        Validate(draft);
        if (draft.Revision != State.Revision) throw new IOException("便签版本已变化，请保留草稿后重试。");
        var next = JsonSerializer.Deserialize<NotesState>(JsonSerializer.Serialize(draft, Json), Json)!;
        next.Revision++;
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, next, Json); stream.Flush(true); }
            // Always back up validated in-memory state, never rotate damaged disk bytes over a good backup.
            var backup = FilePath + ".bak.tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, State.Revision == 0 ? next : State, Json); stream.Flush(true); }
                File.Move(backup, FilePath + ".bak", true);
            }
            finally { if (File.Exists(backup)) File.Delete(backup); }
            File.Move(temp, FilePath, true);
            State = next; draft.Revision = next.Revision;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Validate(NotesState state)
    {
        if (state.Format != 1 || state.Revision < 0 || state.Projects is null || state.Notes is null ||
            !double.IsFinite(state.LeftFraction) || state.LeftFraction < .2 || state.LeftFraction > .8)
            throw new IOException("便签格式无效或版本暂不支持。");
        var ids = new HashSet<string>();
        foreach (var p in state.Projects)
            if (p is null || !Guid.TryParseExact(p.Id, "N", out _) || !ids.Add(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Search is null || p.EditorKey is null || p.Caret < 0 || p.SelectionLength < 0 || !double.IsFinite(p.Scroll) || p.Scroll < 0)
                throw new IOException("便签项目无效。");
        var noteIds = new HashSet<string>();
        foreach (var n in state.Notes)
            if (n is null || !Guid.TryParseExact(n.Id, "N", out _) || !noteIds.Add(n.Id) || !ids.Contains(n.ProjectId) ||
                n.Kind is not ("text" or "todo") || n.Title is null || n.Text is null || n.Tasks is null || n.Tasks.Any(t => t is null || t.Text is null) ||
                (n.DeletedUtc is null && state.Projects.Single(p => p.Id == n.ProjectId).DeletedUtc is not null))
                throw new IOException("便签记录或项目归属无效。");
    }
    public static NoteProject AddProject(NotesState draft, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 100) throw new ArgumentException("项目名称需为 1 至 100 个字符。");
        EnsureName(draft, name, null);
        var project = new NoteProject { Name = name }; draft.Projects.Add(project); draft.ActiveProject = project.Id; return project;
    }
    private static void EnsureName(NotesState state, string name, string? except)
    {
        if (state.Projects.Any(p => p.Id != except && p.DeletedUtc is null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("项目名称已存在，请换一个名称。");
    }
    public static void RenameProject(NotesState state, NoteProject project, string name)
    {
        name = name.Trim(); if (name.Length == 0 || name.Length > 100) throw new ArgumentException("项目名称需为 1 至 100 个字符。");
        EnsureName(state, name, project.Id); project.Name = name; project.ModifiedUtc = DateTime.UtcNow;
    }
    public static void DeleteProject(NotesState state, NoteProject project, string? destination = null)
    {
        if (destination is not null && !state.Projects.Any(p => p.Id == destination && p.Id != project.Id && !p.Archived && p.DeletedUtc is null))
            throw new ArgumentException("请选择其他活动项目。");
        var batch = Guid.NewGuid().ToString("N");
        foreach (var note in state.Notes.Where(n => n.ProjectId == project.Id && n.DeletedUtc is null))
            if (destination is not null) note.ProjectId = destination;
            else { note.DeletedUtc = DateTime.UtcNow; note.DeleteBatch = batch; }
        project.DeletedUtc = DateTime.UtcNow; project.DeleteBatch = batch;
    }
    public static void RestoreProject(NotesState state, NoteProject project, string name)
    {
        RenameProject(state, project, name); project.DeletedUtc = null; project.Archived = false;
        foreach (var note in state.Notes.Where(n => n.ProjectId == project.Id && project.DeleteBatch is not null && n.DeleteBatch == project.DeleteBatch))
        { note.DeletedUtc = null; note.DeleteBatch = null; }
        project.DeleteBatch = null;
    }
}
