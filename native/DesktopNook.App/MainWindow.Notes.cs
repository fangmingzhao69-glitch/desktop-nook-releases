using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow
{
    internal NotesStore NotesStore = null!;
    internal NotesState NotesDraft = null!;
    private readonly DispatcherTimer _notesSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _notesDeadline = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _notesSearch = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _notesDirty, _notesComposing, _renderingNotes, _showNotesArchive;
    private StackPanel _projectTabs = null!, _noteCards = null!;
    private ScrollViewer _projectScroll = null!;
    private TextBox _noteSearch = null!;
    private TextBlock _notesSearchHint = null!;
    private TextBlock _notesStatus = null!, _notesCounts = null!;
    private Button _addNote = null!, _addNoteProject = null!, _notesArchiveButton = null!;
    private Grid _notesSearchLayer = null!;
    private ScrollViewer _notesScroll = null!;
    private NoteProject? NoteProject => NotesDraft?.Projects.FirstOrDefault(p => p.Id == NotesDraft.ActiveProject);
    private static TextBlock NotesText(string text, double size = 12) => new() { Text = text, FontSize = size, Foreground = Brush("#35566E"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private Button NotesButton(string text, Action<Button> action, bool primary = false)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(9,6,9,6), VerticalAlignment = VerticalAlignment.Center };
        if (primary) b.Style = (Style)FindResource("PrimaryButton");
        b.Click += (_,_) => action(b); return b;
    }
    private void InitializeNotes()
    {
        NotesStore = new(Store.DataDirectory); NotesDraft = NotesStore.Draft();
        NotesHost.RowDefinitions.Add(new() { Height = new(78) });
        NotesHost.RowDefinitions.Add(new() { Height = new(49) });
        NotesHost.RowDefinitions.Add(new()); NotesHost.RowDefinitions.Add(new() { Height = new(46) });
        var title = new DockPanel { Margin = new Thickness(18,0,16,0), LastChildFill = true };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var moreButton=NotesButton("···", b => OnMore(b,new()));AutomationProperties.SetName(moreButton,"更多操作");controls.Children.Add(moreButton);
        var minimizeButton=NotesButton("−", b => OnMinimizeButton(b,new()));AutomationProperties.SetName(minimizeButton,"最小化轻匣");minimizeButton.ToolTip="最小化";controls.Children.Add(minimizeButton);
        var closeButton=NotesButton("×", b => OnCloseButton(b,new()));AutomationProperties.SetName(closeButton,"关闭轻匣");closeButton.ToolTip="关闭轻匣";controls.Children.Add(closeButton);
        DockPanel.SetDock(controls,Dock.Right); title.Children.Add(controls);
        _addNote = NotesButton("＋ 记一条", _ => AddProjectNote(), true);
        AutomationProperties.SetAutomationId(_addNote,"AddProjectNote");
        DockPanel.SetDock(_addNote,Dock.Right); title.Children.Add(_addNote);
        var heading = NotesText("便签",17);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Foreground = (Brush)FindResource("Ink");
        title.Children.Add(heading);
        NotesHost.Children.Add(title);
        var projects = new DockPanel { Margin = new Thickness(14,0,12,0) }; Grid.SetRow(projects,1);
        var manage = NotesButton("⋯",b=>ProjectManagement(b));manage.ToolTip="分类管理"; DockPanel.SetDock(manage,Dock.Right); projects.Children.Add(manage);
        _notesArchiveButton = NotesButton("查看归档", _ => ToggleNotesArchive());
        AutomationProperties.SetAutomationId(_notesArchiveButton, "NotesArchive");
        DockPanel.SetDock(_notesArchiveButton, Dock.Right);
        projects.Children.Add(_notesArchiveButton);
        _addNoteProject = NotesButton("＋ 新建分类", _ => NewNoteProject());
        AutomationProperties.SetName(_addNoteProject, "新建分类");
        DockPanel.SetDock(_addNoteProject, Dock.Right);
        projects.Children.Add(_addNoteProject);
        _projectTabs = new StackPanel { Orientation=Orientation.Horizontal };
        _projectScroll = new ScrollViewer { Content=_projectTabs, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(_projectScroll, "NoteProjectTabs");
        projects.Children.Add(_projectScroll); NotesHost.Children.Add(projects);
        var body=new Grid { Margin=new Thickness(16,8,16,0) }; Grid.SetRow(body,2);
        body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new());
        _noteSearch=new TextBox { FontSize=12,Padding=new Thickness(9,7,9,7),Margin=new Thickness(0,0,0,8), Height=34 };
        AutomationProperties.SetName(_noteSearch,"搜索当前分类的记录");
        TrackComposition(_noteSearch);
        _noteSearch.TextChanged+=(_,_)=>{if(_renderingNotes||NoteProject is null)return;NoteProject.Search=_noteSearch.Text;_notesSearchHint.Visibility=_noteSearch.Text.Length==0?Visibility.Visible:Visibility.Collapsed;NotesChanged();_notesSearch.Stop();_notesSearch.Start();};
        _notesSearchLayer=new Grid();_notesSearchLayer.Children.Add(_noteSearch);_notesSearchHint=NotesText("搜索当前分类的记录…",12);_notesSearchHint.Margin=new Thickness(10,0,10,8);_notesSearchHint.IsHitTestVisible=false;_notesSearchHint.Foreground=Brush("#8998A6");_notesSearchLayer.Children.Add(_notesSearchHint);body.Children.Add(_notesSearchLayer);
        _notesCounts=NotesText("",11);AutomationProperties.SetName(_notesCounts,"分类记录统计");_notesCounts.Margin=new Thickness(0,0,0,8);_notesCounts.TextWrapping=TextWrapping.NoWrap;_notesCounts.TextTrimming=TextTrimming.CharacterEllipsis;Grid.SetRow(_notesCounts,1);body.Children.Add(_notesCounts);
        _noteCards=new StackPanel(); _notesScroll=new ScrollViewer { Content=_noteCards, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        _notesScroll.ScrollChanged+=(_,e)=>{if(e.VerticalChange==0)return;_shutter.InvalidateSnapshot();if(!_renderingNotes&&!_showNotesArchive&&NoteProject is not null){NoteProject.Scroll=_notesScroll.VerticalOffset;NotesChanged();}};
        Grid.SetRow(_notesScroll,2);body.Children.Add(_notesScroll);NotesHost.Children.Add(body);
        var footer=new DockPanel { Margin=new Thickness(12,0,12,0) };Grid.SetRow(footer,3);
        var retry=NotesButton("重试",_=>FlushNotes());DockPanel.SetDock(retry,Dock.Right);footer.Children.Add(retry);
        var export=NotesButton("导出",_=>ExportNotes());DockPanel.SetDock(export,Dock.Right);footer.Children.Add(export);
        _notesStatus=NotesText("已保存",10);_notesStatus.TextTrimming=TextTrimming.CharacterEllipsis;footer.Children.Add(_notesStatus);NotesHost.Children.Add(footer);
        _notesSave.Tick+=(_,_)=>{_notesSave.Stop();FlushNotes(false);};
        _notesDeadline.Tick+=(_,_)=>{_notesDeadline.Stop();FlushNotes(false);};
        _notesSearch.Tick+=(_,_)=>{if(_notesComposing||_noteDragHandle is not null)return;_notesSearch.Stop();RenderNoteCards();};
        SessionEndingCancelEventHandler ending=(_,e)=>{if(!FlushNotes(false))e.Cancel=true;};Application.Current.SessionEnding+=ending;
        Closed+=(_,_)=>{Application.Current.SessionEnding-=ending;_notesSave.Stop();_notesDeadline.Stop();_notesSearch.Stop();};
        RenderNotes();
        if(NotesStore.RecoveryMessage is not null){_notesStatus.Text=NotesStore.RecoveryMessage;_notesStatus.ToolTip=_notesStatus.Text;}
    }
    private void TrackComposition(TextBox editor)
    {
        TextCompositionManager.AddPreviewTextInputStartHandler(editor,(_,e)=>{if(e.TextComposition.CompositionText.Length>0)_notesComposing=true;});
        TextCompositionManager.AddPreviewTextInputUpdateHandler(editor,(_,e)=>_notesComposing=e.TextComposition.CompositionText.Length>0);
        TextCompositionManager.AddPreviewTextInputHandler(editor,(_,_)=>{_notesComposing=false;if(_notesDirty){_notesSave.Stop();_notesSave.Start();}});
        editor.LostKeyboardFocus+=(_,_)=>{_notesComposing=false;};
    }
    private void NotesChanged(bool body=false,ProjectNote? note=null)
    {
        if(_renderingNotes)return;
        _notesDirty=true;_shutter.InvalidateSnapshot();
        if(body&&note is not null)note.ModifiedUtc=DateTime.UtcNow;
        _notesStatus.Text=(NoteProject?.Name??"便签")+" · 保存中…";
        _notesSave.Stop();_notesSave.Start();if(!_notesDeadline.IsEnabled)_notesDeadline.Start();
    }
    internal bool FlushNotes(bool showError=true)
    {
        if(NotesDraft is null)return true;
        if(_notesComposing){_notesStatus.Text="请先确认输入法文字，再完成操作";return false;}
        if(!_notesDirty)return true;
        _notesSave.Stop();_notesDeadline.Stop();
        try{NotesStore.Commit(NotesDraft);_notesDirty=false;_notesStatus.Text=(NoteProject?.Name??"便签")+" · 已保存";_notesStatus.ToolTip=null;return true;}
        catch(Exception ex){_notesStatus.Text="保存失败 · 草稿已保留，请重试或导出";_notesStatus.ToolTip=ex.Message;
            if(showError){_interactions++;try{UiDialogs.Show(this,"便签保存失败，草稿仍保留在当前窗口。请重试或导出后再操作。\n"+ex.Message,"便签保存失败");}finally{_interactions--;}}return false;}
    }
    private void ChangeNotes(Action action)
    {
        if(!FlushNotes())return;
        var before=NotesStore.Draft();
        try{action();NotesChanged();if(!FlushNotes()){NotesDraft=before;_notesDirty=false;}RenderNotes();}
        catch(Exception ex){NotesDraft=before;_notesDirty=false;RenderNotes();_interactions++;try{UiDialogs.Show(this,ex.Message,"便签");}finally{_interactions--;}}
    }
    private void NewNoteProject()
    {
        if(!FlushNotes())return;var name=Ask("新建分类","分类名称","");if(name is null)return;
        ChangeNotes(()=>{NotesStore.AddProject(NotesDraft,name);});
    }
    internal void SelectNoteProject(string id)
    {
        if(!FlushNotes())return;
        ChangeNotes(()=>{NotesDraft.ActiveProject=id;});
    }
    internal void AddProjectNote()
    {
        if(NoteProject is null||NoteProject.Archived)return;
        ChangeNotes(()=>{
            var note=new ProjectNote{ProjectId=NoteProject.Id,Kind="todo",Title="待办清单"};
            note.Tasks.Add(new());NotesDraft.Notes.Add(note);NoteProject.Search="";NoteProject.SelectedNote=note.Id;
        });
        Dispatcher.BeginInvoke(()=>{var box=FindVisualChildren<TextBox>(_noteCards).LastOrDefault();box?.Focus();box?.BringIntoView();},DispatcherPriority.Loaded);
    }
    internal void RenderNotes()
    {
        if(NotesDraft is null)return;
        _renderingNotes=true;
        try
        {
            _notesArchiveButton.Content = _showNotesArchive ? "返回便签" : "查看归档";
            _projectTabs.Visibility = _showNotesArchive ? Visibility.Collapsed : Visibility.Visible;
            _addNoteProject.Visibility = _projectTabs.Visibility;
            _addNote.Visibility = _projectTabs.Visibility;
            _notesSearchLayer.Visibility = _projectTabs.Visibility;
            if (_showNotesArchive)
            {
                RenderNotesArchive();
                UpdateNotesColumns();
                return;
            }
            if(NoteProject is null)NotesDraft.ActiveProject=NotesDraft.Projects.FirstOrDefault(p=>!p.Archived)?.Id;
            _projectTabs.Children.Clear();
            foreach(var project in NotesDraft.Projects.Where(p=>!p.Archived || p.Id==NotesDraft.ActiveProject))
            {
                var b=NotesButton(project.Name+(project.Archived?"（归档）":""),_=>SelectNoteProject(project.Id));b.Content=new TextBlock{Text=project.Name+(project.Archived?"（归档）":""),TextTrimming=TextTrimming.CharacterEllipsis};b.MaxWidth=140;b.ToolTip=project.Name+" · 右键管理分类";
                b.Background=project.Id==NotesDraft.ActiveProject?Brush("#DDF2FF"):Brushes.Transparent;
                b.Foreground=Brush("#0877BE");b.Margin=new Thickness(0,0,4,0);
                AutomationProperties.SetAutomationId(b,"Project-"+project.Id);
                if (!project.Archived) EnableNoteProjectDrag(b, project);
                b.MouseRightButtonUp+=(_,e)=>{e.Handled=true;NoteProjectMenu(b,project);};_projectTabs.Children.Add(b);
            }
            _addNote.IsEnabled=NoteProject is {Archived:false};
            _noteSearch.IsEnabled=NoteProject is not null;
            _noteSearch.Text=NoteProject?.Search??"";_notesSearchHint.Text="搜索"+(NoteProject?.Name??"当前分类")+"的记录…";_notesSearchHint.Visibility=_noteSearch.Text.Length==0?Visibility.Visible:Visibility.Collapsed;_noteSearch.ToolTip="搜索"+(NoteProject?.Name??"当前分类")+"的记录";
            RenderNoteCards();UpdateNotesColumns();
            _notesStatus.Text=(NoteProject?.Name??"便签")+(_notesDirty?" · 保存中…":" · 已保存");
        }
        finally{_renderingNotes=false;}
        var offset=NoteProject?.Scroll??0;
        Dispatcher.BeginInvoke(()=>{_renderingNotes=true;try{var p=NoteProject;var box=FindVisualChildren<TextBox>(_noteCards).FirstOrDefault(b=>b.Tag as string==p?.SelectedNote+":"+p?.EditorKey);if(box is not null&&p is not null){box.Focus();box.Select(Math.Min(p.Caret,box.Text.Length),Math.Min(p.SelectionLength,box.Text.Length-Math.Min(p.Caret,box.Text.Length)));}_notesScroll.ScrollToVerticalOffset(offset);}finally{_renderingNotes=false;}},DispatcherPriority.Loaded);
    }
    private void UpdateNoteCounts()
    {
        var notes=NotesDraft.Notes.Where(n=>n.ProjectId==NoteProject?.Id&&!n.Archived).ToList();
        var tasks=notes.Where(n=>n.Kind=="todo").SelectMany(n=>n.Tasks).Where(t=>!string.IsNullOrWhiteSpace(t.Text)).ToList();
        var search=NoteProject?.Search??"";
        _notesCounts.Text=$"{notes.Count} 条记录 · 待完成 {tasks.Count(t=>!t.Done)} · 已完成 {tasks.Count(t=>t.Done)}"+(search.Length>0?$" · 匹配 {notes.Count(MatchesNote)} 条":"");
        _notesCounts.ToolTip=_notesCounts.Text;
    }
    private bool MatchesNote(ProjectNote note)
    {
        var search = NoteProject?.Search ?? "";
        var body = note.Kind == "text" ? note.Text : string.Join("\n", note.Tasks.Select(task => task.Text));
        return search.Length == 0 || (note.Title + "\n" + body).Contains(search, StringComparison.OrdinalIgnoreCase);
    }
    private void RenderNoteCards()
    {
        if (_notesComposing || _showNotesArchive) return;
        _shutter.InvalidateSnapshot();
        _noteCards.Children.Clear();
        UpdateNoteCounts();
        var notes = ActiveCategoryNotes();
        bool hasResults = false;
        for (int i = 0; i < notes.Count; i++)
        {
            if (!MatchesNote(notes[i])) continue;
            AddNoteCard(notes[i], i + 1, NoteProject?.Archived == true);
            hasResults = true;
        }
        if (!hasResults)
        {
            var empty=NotesText(NoteProject is null?"先新建一个分类，再开始记录":!string.IsNullOrEmpty(NoteProject.Search)?"未找到匹配记录":NoteProject.Archived?"这个分类已归档":"这个分类还没有记录\n点击『记一条』开始",12);
            empty.Margin=new Thickness(8,36,8,20);_noteCards.Children.Add(empty);
            if(NoteProject is null)_noteCards.Children.Add(NotesButton("＋ 新建分类",_=>NewNoteProject(),true));
        }
    }
    private TextBox NoteEditor(string text,ProjectNote note,Action<string> changed,bool multiline=false,string key="title",Action? delete=null,bool readOnly=false)
    {
        var editor=new TextBox{Text=text,FontSize=12,Padding=new Thickness(4),BorderThickness=new Thickness(0),Background=Brushes.Transparent,
            AcceptsReturn=multiline,TextWrapping=TextWrapping.Wrap,IsReadOnly=readOnly,MinHeight=26};
        editor.Tag=note.Id+":"+key;
        editor.ContextMenu=new ContextMenu();
        editor.ContextMenuOpening+=(_,e)=>{
            e.Handled=true;
            Popup(editor,[Menu("剪切",()=>editor.Cut(),!editor.IsReadOnly&&editor.SelectionLength>0),
                Menu("复制",()=>editor.Copy(),editor.SelectionLength>0),
                Menu("粘贴",()=>editor.Paste(),!editor.IsReadOnly&&Clipboard.ContainsText()),new Separator(),
                Menu(delete is null?"删除便签…":"删除此项",delete??(()=>DeleteNote(note)),!editor.IsReadOnly)]);
        };
        TrackComposition(editor);
        editor.SelectionChanged+=(_,_)=>{if(_renderingNotes||_showNotesArchive||!editor.IsKeyboardFocused||NoteProject is null)return;NoteProject.EditorKey=key;NoteProject.Caret=editor.SelectionStart;NoteProject.SelectionLength=editor.SelectionLength;NotesChanged();};
        editor.TextChanged+=(_,_)=>{changed(editor.Text);NotesChanged(true,note);UpdateNoteCounts();};
        editor.GotKeyboardFocus+=(_,_)=>{if(!_showNotesArchive&&NoteProject is not null){NoteProject.SelectedNote=note.Id;NoteProject.EditorKey=key;NotesChanged();}};
        return editor;
    }
    private void AddNoteCard(ProjectNote note, int number, bool readOnly)
    {
        var content=new StackPanel();
        var border=new Border { Tag=note, CornerRadius=new CornerRadius(10),BorderBrush=Brush("#CDE5F6"),BorderThickness=new Thickness(1),Padding=new Thickness(10),Margin=new Thickness(0,0,0,10),Background=Brush("#F5FAFE"),Child=content };
        var header=new DockPanel();var menu=NotesButton("···",b=>NoteCardMenu(b,note));AutomationProperties.SetName(menu,"便签操作");DockPanel.SetDock(menu,Dock.Right);header.Children.Add(menu);
        string metadata = note.ModifiedUtc.ToLocalTime().ToString("MM月dd日 HH:mm");
        if (note.Favorite) metadata += " · 常用";
        if (note.Archived) metadata += " · 已单独归档";
        content.Children.Add(NoteOrderBar(note, number, metadata, readOnly));
        var title = NoteEditor(note.Title, note, t => note.Title = t, readOnly: readOnly);
        title.FontWeight = FontWeights.SemiBold;
        title.FontSize = 14;
        AutomationProperties.SetName(title, "记录标题");
        if (!readOnly) title.ToolTip = "点击编辑便签名称";
        header.Children.Add(title);
        content.Children.Add(header);
        if(note.Kind=="text")
        {
            var body=NoteEditor(note.Text,note,t=>note.Text=t,true,"body",readOnly:readOnly);body.MinHeight=65;AutomationProperties.SetName(body,"文字记录正文");content.Children.Add(body);
        }
        else
        {
            var rows=new StackPanel();content.Children.Add(rows);
            void BuildRows(int focus=-1)
            {
                rows.Children.Clear();
                foreach(var task in note.Tasks)
                {
                    var row=new DockPanel{Margin=new Thickness(0,3,0,0)};
                    var check=new CheckBox{IsChecked=task.Done,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(2,7,6,0),IsEnabled=!readOnly};
                    AutomationProperties.SetName(check,"完成待办");DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
                    var input=NoteEditor(task.Text,note,t=>task.Text=t,key:"task-"+note.Tasks.IndexOf(task),delete:()=>ChangeNotes(()=>{note.Tasks.Remove(task);note.ModifiedUtc=DateTime.UtcNow;}),readOnly:readOnly);input.TextDecorations=task.Done?TextDecorations.Strikethrough:null;input.Foreground=Brush(task.Done?"#8998A6":"#35566E");
                    check.Click+=(_,_)=>{task.Done=check.IsChecked==true;input.TextDecorations=task.Done?TextDecorations.Strikethrough:null;input.Foreground=Brush(task.Done?"#8998A6":"#35566E");NotesChanged(true,note);UpdateNoteCounts();};
                    input.PreviewKeyDown+=(_,e)=>{
                        if(_notesComposing||input.IsReadOnly||e.Key==Key.ImeProcessed)return;
                        var index=note.Tasks.IndexOf(task);
                        if(e.Key==Key.Enter){e.Handled=true;note.Tasks.Insert(index+1,new());NotesChanged(true,note);BuildRows(index+1);}
                        else if(e.Key==Key.Back&&input.Text.Length==0&&note.Tasks.Count>1){e.Handled=true;note.Tasks.Remove(task);NotesChanged(true,note);BuildRows(Math.Max(0,index-1));}
                    };
                    row.Children.Add(input);rows.Children.Add(row);
                }
                if(focus>=0)Dispatcher.BeginInvoke(()=>{var box=FindVisualChildren<TextBox>(rows).ElementAtOrDefault(focus);box?.Focus();box?.BringIntoView();});
            }
            BuildRows();
            if(!readOnly)content.Children.Add(NotesButton("＋ 添加待办",_=>{if(_notesComposing)return;note.Tasks.Add(new());NotesChanged(true,note);BuildRows(note.Tasks.Count-1);}));
        }
        border.ContextMenu=new ContextMenu();
        border.ContextMenuOpening+=(_,e)=>{e.Handled=true;NoteCardMenu(border,note);};
        _noteCards.Children.Add(border);
    }
    private bool ConfirmNotes(string message)
    {_interactions++;try{return UiDialogs.Show(this,message,"便签",MessageBoxButton.OKCancel)==MessageBoxResult.OK;}finally{_interactions--;}}
    private void DeleteNote(ProjectNote note)
    {
        if(ConfirmNotes($"删除便签“{note.Title}”？此操作无法撤销。"))
            ChangeNotes(()=>NotesDraft.Notes.Remove(note));
    }
    private void NoteCardMenu(FrameworkElement owner, ProjectNote note)
    {
        var project = NotesDraft.Projects.Single(p => p.Id == note.ProjectId);
        bool editable = !note.Archived && !project.Archived;
        var entries = new List<object>();
        if (editable)
        {
            entries.Add(Menu("重命名便签…", () =>
            {
                var name = Ask("重命名便签", "便签名称", note.Title);
                if (!string.IsNullOrWhiteSpace(name))
                    ChangeNotes(() => { note.Title = name.Trim(); note.ModifiedUtc = DateTime.UtcNow; });
            }));
            var type = new MenuItem { Header = "便签类型" };
            foreach (var (kind, label) in new[] { ("todo", "待办清单"), ("text", "文字记录") })
            {
                var item = Menu(label, () => ChangeNotes(() => note.ChangeKind(kind)), note.Kind != kind);
                item.IsChecked = note.Kind == kind;
                type.Items.Add(item);
            }
            entries.Add(type);
            entries.Add(Menu(note.Favorite ? "取消常用" : "设为常用", () => ChangeNotes(() => note.Favorite = !note.Favorite)));
            var order = ActiveCategoryNotes().Where(MatchesNote).ToList();
            int index = order.IndexOf(note);
            bool canReorder = CanReorderNotes && index >= 0;
            entries.Add(Menu("上移一位", () => ChangeNotes(() => NotesStore.ReorderNote(NotesDraft, note, order[index - 1], false)), canReorder && index > 0));
            entries.Add(Menu("下移一位", () => ChangeNotes(() => NotesStore.ReorderNote(NotesDraft, note, order[index + 1], true)), canReorder && index + 1 < order.Count));
            var move = new MenuItem { Header = "移动到其他分类" };
            foreach (var destination in NotesDraft.Projects.Where(p => p.Id != note.ProjectId && !p.Archived))
                move.Items.Add(Menu(destination.Name, () => ChangeNotes(() => NotesStore.MoveNoteToProject(NotesDraft, note, destination))));
            move.IsEnabled = move.Items.Count > 0;
            entries.Add(move);
            entries.Add(Menu("归档此便签", () => ChangeNotes(() => note.Archived = true)));
        }
        else if (note.Archived)
        {
            entries.Add(Menu("恢复此便签", () => ChangeNotes(() => note.Archived = false)));
        }
        entries.Add(Menu("导出记录…", () => ExportNotes(note)));
        entries.Add(new Separator());
        entries.Add(Menu("删除便签…", () => DeleteNote(note)));
        Popup(owner, entries);
    }
    private void NoteProjectMenu(Button owner,NoteProject project)
    {
        var idx=NotesDraft.Projects.IndexOf(project);
        Popup(owner,[Menu("重命名",()=>{var name=Ask("重命名分类","分类名称",project.Name);if(name is not null)ChangeNotes(()=>NotesStore.RenameProject(NotesDraft,project,name));}),
            Menu("向前移动",()=>ChangeNotes(()=>{NotesDraft.Projects.Remove(project);NotesDraft.Projects.Insert(idx-1,project);}),idx>0),
            Menu("向后移动",()=>ChangeNotes(()=>{NotesDraft.Projects.Remove(project);NotesDraft.Projects.Insert(idx+1,project);}),idx<NotesDraft.Projects.Count-1),
            Menu(project.Archived?"恢复整个分类":"归档整个分类…",()=>SetNotesCategoryArchive(project)),
            Menu("导出分类…",()=>ExportNotes(project:project)),Menu("删除分类…",()=>DeleteNoteProject(owner,project))]);
    }
    private void DeleteNoteProject(Button owner,NoteProject project)
    {
        var count=NotesDraft.Notes.Count(n=>n.ProjectId==project.Id);
        var move=new MenuItem{Header=$"将 {count} 条记录移到其他分类后删除"};
        foreach(var p in NotesDraft.Projects.Where(p=>p.Id!=project.Id&&!p.Archived))move.Items.Add(Menu(p.Name,()=>{if(ConfirmNotes($"删除分类“{project.Name}”，并将 {count} 条记录移至“{p.Name}”？"))ChangeNotes(()=>NotesStore.DeleteProject(NotesDraft,project,p.Id));}));
        Popup(owner,[move,Menu($"删除分类及 {count} 条记录…",()=>{if(ConfirmNotes($"删除分类“{project.Name}”及其 {count} 条记录？此操作无法撤销。"))ChangeNotes(()=>NotesStore.DeleteProject(NotesDraft,project));})]);
    }
    private void ProjectManagement(Button owner) =>
        Popup(owner, [Menu("新建分类…", NewNoteProject), Menu("导出全部便签…", () => ExportNotes())]);

    private void SetNotesCategoryArchive(NoteProject project)
    {
        if (!project.Archived && !ConfirmNotes($"归档整个分类“{project.Name}”？分类及其便签将移到归档区，可随时恢复整个分类。")) return;
        ChangeNotes(() =>
        {
            project.Archived = !project.Archived;
            if (project.Archived && NotesDraft.ActiveProject == project.Id)
                NotesDraft.ActiveProject = null;
            else if (!project.Archived)
                NotesDraft.ActiveProject = project.Id;
        });
    }

    private void ToggleNotesArchive()
    {
        if (!FlushNotes()) return;
        _notesSearch.Stop();
        _showNotesArchive = !_showNotesArchive;
        RenderNotes();
        if (_showNotesArchive) _notesScroll.ScrollToTop();
    }

    private void RenderNotesArchive()
    {
        _shutter.InvalidateSnapshot();
        _noteCards.Children.Clear();
        _notesCounts.Text = "归档 · 按原分类显示";
        _notesCounts.ToolTip = "归档整个分类会隐藏该分类及其中的便签，可从这里恢复。";
        _notesStatus.Text = "归档";
        foreach (var project in NotesDraft.Projects)
        {
            var records = NotesDraft.Notes.Where(note => note.ProjectId == project.Id && (project.Archived || note.Archived)).ToList();
            if (records.Count == 0 && !project.Archived) continue;

            var section = new DockPanel { Margin = new Thickness(0, 6, 0, 8) };
            if (project.Archived)
            {
                var menu = NotesButton("···", button => NoteProjectMenu(button, project));
                AutomationProperties.SetName(menu, "管理已归档分类：" + project.Name);
                DockPanel.SetDock(menu, Dock.Right);
                section.Children.Add(menu);
            }
            var title = NotesText(project.Archived ? project.Name + "（整个分类已归档）" : project.Name, 13);
            title.FontWeight = FontWeights.SemiBold;
            title.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(title);
            _noteCards.Children.Add(section);
            for (int i = 0; i < records.Count; i++)
                AddNoteCard(records[i], i + 1, readOnly: true);
        }
        if (_noteCards.Children.Count == 0)
            _noteCards.Children.Add(NotesText("暂无归档的便签或分类"));
    }

    private void ExportNotes(ProjectNote? note=null,NoteProject? project=null)
    {
        _interactions++;
        try
        {
            var picker=new Microsoft.Win32.SaveFileDialog{Title="导出便签",Filter="Markdown 文件 (*.md)|*.md",DefaultExt=".md",AddExtension=true,FileName="便签导出.md"};
            if(picker.ShowDialog(this)!=true)return;
            var text=NotesMarkdown.Export(NotesDraft,note,project);
            File.WriteAllText(picker.FileName,text);_notesStatus.Text="已导出当前内容";
        }
        catch(Exception ex){UiDialogs.Show(this,"导出失败："+ex.Message,"便签");}
        finally{_interactions--;}
    }
    private async Task<(string Helper,string Plan,string Version)> StageUpdateWithNotesAsync(string source,string target)
    {
        if(!FlushNotes())throw new IOException("便签尚未保存，更新已停止。");
        IsEnabled=false;
        try{return await Task.Run(()=>LocalUpdater.Stage(source,target,Store.DataDirectory,App.IsSandbox?App.SandboxRoot:null,options:App.Options,state:Store.State));}
        finally{IsEnabled=true;}
    }
    private bool _notesVisualsParked;
    private void ParkNotesVisuals()
    {
        if(_notesDirty||_notesComposing)return;
        _renderingNotes=true;try{_noteCards.Children.Clear();_notesVisualsParked=true;}finally{_renderingNotes=false;}
    }
    private void RestoreNotesVisuals()
    {
        if(!_notesVisualsParked)return;_notesVisualsParked=false;RenderNotes();
    }
    private void OnNotesDrag(object sender,DragEventArgs e){e.Effects=DragDropEffects.None;e.Handled=true;}
    private bool _notesSplitting;
    private double _notesSplitStartWidth,_notesSplitStartPointer,_notesSplitStartFraction;
    private double NotesSplitAvailable=>Math.Max(1,(SplitLayout.ActualWidth>0?SplitLayout.ActualWidth:Surface.Width-2)-5);
    private void OnNotesSplitStarted(object sender,DragStartedEventArgs e)
    {
        FinishPanelMotion();UiMotion.StopReveal(Icons);UiMotion.StopReveal(Surface);_shutter.ReleaseBuffer();
        _notesSplitting=true;_placementVersion++;_interactions++;
        _notesSplitStartWidth=LauncherColumn.ActualWidth;_notesSplitStartPointer=Mouse.GetPosition(SplitLayout).X;
        _notesSplitStartFraction=NotesDraft.LeftFraction;
    }
    private void OnNotesSplitDelta(object sender,DragDeltaEventArgs e)
    {
        if(!_notesSplitting)return;
        SetNotesSplitWidth(_notesSplitStartWidth+Mouse.GetPosition(SplitLayout).X-_notesSplitStartPointer);
    }
    private void SetNotesSplitWidth(double requested)
    {
        var available=NotesSplitAvailable;var minLeft=Math.Min(330,available*.52);var minRight=Math.Min(300,available*.48);
        var left=Math.Clamp(requested,minLeft,Math.Max(minLeft,available-minRight));
        bool changed=!LauncherColumn.Width.IsAbsolute||Math.Abs(LauncherColumn.Width.Value-left)>.01||!NotesColumn.Width.IsStar;
        LauncherColumn.Width=new GridLength(left);NotesColumn.Width=new GridLength(1,GridUnitType.Star);UpdateIconColumns();
        if(changed)_shutter.InvalidateSnapshot();
    }
    private void OnNotesSplitCompleted(object sender,DragCompletedEventArgs e)
    {
        if(!_notesSplitting)return;
        if(e.Canceled)NotesDraft.LeftFraction=_notesSplitStartFraction;
        else NotesDraft.LeftFraction=Math.Clamp(LauncherColumn.Width.Value/NotesSplitAvailable,.2,.8);
        _notesSplitting=false;_interactions=Math.Max(0,_interactions-1);
        UpdateSizingLayout();_shutter.InvalidateSnapshot();
        if(!e.Canceled)NotesChanged();
    }
    private void UpdateNotesColumns()
    {
        if(NotesDraft is null||LauncherColumn is null||_notesSplitting)return;
        SetNotesSplitWidth(NotesSplitAvailable*NotesDraft.LeftFraction);
    }
}
