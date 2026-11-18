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
    public bool Archived { get; set; }
    public bool Favorite { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; set; }
    public string? DeleteBatch { get; set; }

    public void ChangeKind(string kind)
    {
        if (kind == Kind) return;
        if (kind == "text")
        {
            Text = string.Join("\n", Tasks.Select(task => task.Text));
        }
        else if (kind == "todo")
        {
            var body = Text.ReplaceLineEndings("\n");
            // Keep existing checkmarks on unchanged tasks, including duplicate lines.
            if (Tasks.Count == 0 || body != string.Join("\n", Tasks.Select(task => task.Text.ReplaceLineEndings("\n"))))
            {
                var previous = Tasks.GroupBy(task => task.Text, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => new Queue<NoteTask>(group), StringComparer.Ordinal);
                Tasks = body.Split('\n').Select(line =>
                    previous.TryGetValue(line, out var matches) && matches.TryDequeue(out var task)
                        ? task
                        : new NoteTask { Text = line }).ToList();
            }
        }
        else throw new ArgumentException("不支持的便签类型。", nameof(kind));

        Kind = kind;
        ModifiedUtc = DateTime.UtcNow;
    }
}
public sealed class NotesState
{
    public int Format { get; set; } = 2;
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
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
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
    // One atomic document groups each category with its records, preserving stable IDs.
    private sealed class CategoryData
    {
        public NoteProject Project { get; set; } = new();
        public List<ProjectNote> Records { get; set; } = [];
    }
    private sealed class Document
    {
        public int Format { get; set; } = 2;
        public long Revision { get; set; }
        public string? ActiveProject { get; set; }
        public double LeftFraction { get; set; }
        public bool RecordsInDisplayOrder { get; set; }
        public List<CategoryData> Categories { get; set; } = [];
    }
    private static Document Snapshot(NotesState state) => new()
    {
        Revision = state.Revision, ActiveProject = state.ActiveProject, LeftFraction = state.LeftFraction,
        RecordsInDisplayOrder = true,
        Categories = state.Projects.Select(p => new CategoryData { Project = p, Records = state.Notes.Where(n => n.ProjectId == p.Id).ToList() }).ToList()
    };
    private static NotesState Read(string path)
    {
        var text = File.ReadAllText(path);
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("Format", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var format)) throw new IOException("便签格式无效。");
        NotesState state;
        bool recordsInDisplayOrder = false;
        if (format == 1)
        {
            state = JsonSerializer.Deserialize<NotesState>(text, Json) ?? throw new IOException("便签为空。");
            Validate(state);
            // Preserve legacy trash in archive; all new deletes remove records outright.
            foreach (var p in state.Projects)
            { if (p.DeletedUtc is not null) p.Archived = true; p.DeletedUtc = null; p.DeleteBatch = null; }
            foreach (var n in state.Notes)
            { if (n.DeletedUtc is not null) n.Archived = true; n.DeletedUtc = null; n.DeleteBatch = null; }
            state.Format = 2;
        }
        else if (format == 2)
        {
            if (!json.RootElement.TryGetProperty("Categories", out var categories) || categories.ValueKind != JsonValueKind.Array) throw new IOException("缺少便签分类数据。");
            var doc = JsonSerializer.Deserialize<Document>(text, Json) ?? throw new IOException("便签为空。");
            if (doc.Categories is null || doc.Categories.Any(c => c is null || c.Project is null || c.Records is null || c.Records.Any(n => n is null || n.ProjectId != c.Project.Id)))
                throw new IOException("便签分类存储无效。");
            state = new() { Revision = doc.Revision, ActiveProject = doc.ActiveProject, LeftFraction = doc.LeftFraction,
                Projects = doc.Categories.Select(c => c.Project).ToList(), Notes = doc.Categories.SelectMany(c => c.Records).ToList() };
            recordsInDisplayOrder = doc.RecordsInDisplayOrder;
        }
        else throw new IOException("便签版本暂不支持。");
        Validate(state);
        // Earlier versions displayed favorites first, then sorted by creation time.
        if (!recordsInDisplayOrder)
            state.Notes = state.Notes.OrderByDescending(note => note.Favorite).ThenBy(note => note.CreatedUtc).ToList();
        return state;
    }
    public NotesState Draft() => JsonSerializer.Deserialize<NotesState>(JsonSerializer.Serialize(State, Json), Json)!;
    public void Commit(NotesState draft)
    {
        if (_blocked) throw new IOException(RecoveryMessage);
        Validate(draft);
        if (draft.Format != 2) throw new IOException("请先迁移旧版便签格式。");
        if (draft.Revision != State.Revision) throw new IOException("便签版本已变化，请保留草稿后重试。");
        var next = JsonSerializer.Deserialize<NotesState>(JsonSerializer.Serialize(draft, Json), Json)!;
        next.Revision++;
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, Snapshot(next), Json); stream.Flush(true); }
            // Always back up validated in-memory state, never rotate damaged disk bytes over a good backup.
            var backup = FilePath + ".bak.tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, Snapshot(State.Revision == 0 ? next : State), Json); stream.Flush(true); }
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
        if (state.Format is not (1 or 2) || state.Revision < 0 || state.Projects is null || state.Notes is null ||
            !double.IsFinite(state.LeftFraction) || state.LeftFraction < .2 || state.LeftFraction > .8)
            throw new IOException("便签格式无效或版本暂不支持。");
        var ids = new HashSet<string>();
        foreach (var p in state.Projects)
            if (p is null || (state.Format == 2 && (p.DeletedUtc is not null || p.DeleteBatch is not null)) || !Guid.TryParseExact(p.Id, "N", out _) || !ids.Add(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Search is null || p.EditorKey is null || p.Caret < 0 || p.SelectionLength < 0 || !double.IsFinite(p.Scroll) || p.Scroll < 0)
                throw new IOException("便签分类无效。");
        var noteIds = new HashSet<string>();
        foreach (var n in state.Notes)
            if (n is null || (state.Format == 2 && (n.DeletedUtc is not null || n.DeleteBatch is not null)) || !Guid.TryParseExact(n.Id, "N", out _) || !noteIds.Add(n.Id) || !ids.Contains(n.ProjectId) ||
                n.Kind is not ("text" or "todo") || n.Title is null || n.Text is null || n.Tasks is null || n.Tasks.Any(t => t is null || t.Text is null) ||
                (n.DeletedUtc is null && state.Projects.Single(p => p.Id == n.ProjectId).DeletedUtc is not null))
                throw new IOException("便签记录或分类归属无效。");
    }
    public static NoteProject AddProject(NotesState draft, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 100) throw new ArgumentException("分类名称需为 1 至 100 个字符。");
        EnsureName(draft, name, null);
        var project = new NoteProject { Name = name }; draft.Projects.Add(project); draft.ActiveProject = project.Id; return project;
    }
    private static void EnsureName(NotesState state, string name, string? except)
    {
        if (state.Projects.Any(p => p.Id != except && p.DeletedUtc is null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("分类名称已存在，请换一个名称。");
    }
    public static void RenameProject(NotesState state, NoteProject project, string name)
    {
        name = name.Trim(); if (name.Length == 0 || name.Length > 100) throw new ArgumentException("分类名称需为 1 至 100 个字符。");
        EnsureName(state, name, project.Id); project.Name = name; project.ModifiedUtc = DateTime.UtcNow;
    }

    public static void ReorderNote(NotesState state, ProjectNote note, ProjectNote target, bool after)
    {
        if (note == target || note.ProjectId != target.ProjectId || note.Archived || target.Archived ||
            !state.Projects.Any(project => project.Id == note.ProjectId && !project.Archived) ||
            !state.Notes.Contains(target) || !state.Notes.Remove(note)) return;
        var index = state.Notes.IndexOf(target);
        state.Notes.Insert(after ? index + 1 : index, note);
    }

    public static void MoveNoteToProject(NotesState state, ProjectNote note, NoteProject destination)
    {
        if (note.ProjectId == destination.Id || destination.Archived || !state.Notes.Remove(note)) return;
        note.ProjectId = destination.Id;
        note.ModifiedUtc = DateTime.UtcNow;
        state.Notes.Add(note);
    }

    public static void DeleteProject(NotesState state, NoteProject project, string? destination = null)
    {
        if (destination is not null && !state.Projects.Any(p => p.Id == destination && p.Id != project.Id && !p.Archived && p.DeletedUtc is null))
            throw new ArgumentException("请选择其他活动分类。");
        if (destination is not null)
        {
            var moved = state.Notes.Where(note => note.ProjectId == project.Id).ToList();
            state.Notes.RemoveAll(note => note.ProjectId == project.Id);
            foreach (var note in moved) note.ProjectId = destination;
            state.Notes.AddRange(moved);
        }
        else state.Notes.RemoveAll(n => n.ProjectId == project.Id);
        state.Projects.Remove(project);
        if (state.ActiveProject == project.Id) state.ActiveProject = state.Projects.FirstOrDefault(p => !p.Archived)?.Id;
    }
}
