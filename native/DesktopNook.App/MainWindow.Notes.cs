using System.IO;
using System.Text.Json;
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
    private bool _notesDirty, _notesComposing, _renderingNotes, _notesTrash;
    private StackPanel _projectTabs = null!, _noteCards = null!;
    private TextBox _noteSearch = null!;
    private TextBlock _notesSearchHint = null!;
    private TextBlock _notesStatus = null!, _notesCounts = null!;
    private Button _addNote = null!;
    private ScrollViewer _notesScroll = null!;
    private NoteProject? NoteProject => NotesDraft?.Projects.FirstOrDefault(p => p.Id == NotesDraft.ActiveProject && p.DeletedUtc is null);
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
        _addNote = NotesButton("＋ 记一条", b => Popup(b,[Menu("文字记录",()=>AddProjectNote("text")),Menu("待办清单",()=>AddProjectNote("todo"))]), true);
        AutomationProperties.SetAutomationId(_addNote,"AddProjectNote");
        DockPanel.SetDock(_addNote,Dock.Right); title.Children.Add(_addNote);
        title.Children.Add(NotesText("项目便签",17)); NotesHost.Children.Add(title);
        var projects = new DockPanel { Margin = new Thickness(14,0,12,0) }; Grid.SetRow(projects,1);
        var manage = NotesButton("⋯",b=>ProjectManagement(b));manage.ToolTip="项目管理、归档与回收站"; DockPanel.SetDock(manage,Dock.Right); projects.Children.Add(manage);
        var add = NotesButton("＋ 新建项目",_=>NewNoteProject()); add.ToolTip="新建项目"; AutomationProperties.SetName(add,"新建项目"); DockPanel.SetDock(add,Dock.Right); projects.Children.Add(add);
        _projectTabs = new StackPanel { Orientation=Orientation.Horizontal };
        var projectScroll = new ScrollViewer { Content=_projectTabs, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled };
        projects.Children.Add(projectScroll); NotesHost.Children.Add(projects);
        var body=new Grid { Margin=new Thickness(16,8,16,0) }; Grid.SetRow(body,2);
        body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new());
        _noteSearch=new TextBox { FontSize=12,Padding=new Thickness(9,7,9,7),Margin=new Thickness(0,0,0,8), Height=34 };
        AutomationProperties.SetName(_noteSearch,"搜索当前项目的记录");
        TrackComposition(_noteSearch);
        _noteSearch.TextChanged+=(_,_)=>{if(_renderingNotes||NoteProject is null)return;NoteProject.Search=_noteSearch.Text;_notesSearchHint.Visibility=_noteSearch.Text.Length==0?Visibility.Visible:Visibility.Collapsed;NotesChanged();_notesSearch.Stop();_notesSearch.Start();};
        var searchLayer=new Grid();searchLayer.Children.Add(_noteSearch);_notesSearchHint=NotesText("搜索当前项目的记录…",12);_notesSearchHint.Margin=new Thickness(10,0,10,8);_notesSearchHint.IsHitTestVisible=false;_notesSearchHint.Foreground=Brush("#8998A6");searchLayer.Children.Add(_notesSearchHint);body.Children.Add(searchLayer);
        _notesCounts=NotesText("",11);_notesCounts.Margin=new Thickness(0,0,0,8);Grid.SetRow(_notesCounts,1);body.Children.Add(_notesCounts);
        _noteCards=new StackPanel(); _notesScroll=new ScrollViewer { Content=_noteCards, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        _notesScroll.ScrollChanged+=(_,e)=>{if(e.VerticalChange==0)return;_shutter.InvalidateSnapshot();if(!_renderingNotes&&NoteProject is not null){NoteProject.Scroll=_notesScroll.VerticalOffset;NotesChanged();}};
        Grid.SetRow(_notesScroll,2);body.Children.Add(_notesScroll);NotesHost.Children.Add(body);
        var footer=new DockPanel { Margin=new Thickness(12,0,12,0) };Grid.SetRow(footer,3);
        var retry=NotesButton("重试",_=>FlushNotes());DockPanel.SetDock(retry,Dock.Right);footer.Children.Add(retry);
        var export=NotesButton("导出",_=>ExportNotes());DockPanel.SetDock(export,Dock.Right);footer.Children.Add(export);
        _notesStatus=NotesText("已保存",10);_notesStatus.TextTrimming=TextTrimming.CharacterEllipsis;footer.Children.Add(_notesStatus);NotesHost.Children.Add(footer);
        _notesSave.Tick+=(_,_)=>{_notesSave.Stop();FlushNotes(false);};
        _notesDeadline.Tick+=(_,_)=>{_notesDeadline.Stop();FlushNotes(false);};
        _notesSearch.Tick+=(_,_)=>{if(_notesComposing)return;_notesSearch.Stop();RenderNoteCards();};
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
        catch(Exception ex){NotesDraft=before;_notesDirty=false;RenderNotes();_interactions++;try{UiDialogs.Show(this,ex.Message,"项目便签");}finally{_interactions--;}}
    }
    private void NewNoteProject()
    {
        if(!FlushNotes())return;var name=Ask("新建项目","项目名称","");if(name is null)return;
        ChangeNotes(()=>{NotesStore.AddProject(NotesDraft,name);_notesTrash=false;});
    }
    internal void SelectNoteProject(string id)
    {
        if(!FlushNotes())return;
        ChangeNotes(()=>{NotesDraft.ActiveProject=id;_notesTrash=false;});
    }
    internal void AddProjectNote(string kind)
    {
        if(NoteProject is null||NoteProject.Archived||_notesTrash)return;
        ChangeNotes(()=>{
            var note=new ProjectNote{ProjectId=NoteProject.Id,Kind=kind,Title=kind=="todo"?"项目待办":"文字记录"};
            if(kind=="todo")note.Tasks.Add(new());NotesDraft.Notes.Add(note);NoteProject.Search="";NoteProject.SelectedNote=note.Id;
        });
        Dispatcher.BeginInvoke(()=>{var box=FindVisualChildren<TextBox>(_noteCards).LastOrDefault();box?.Focus();box?.BringIntoView();},DispatcherPriority.Loaded);
    }
    internal void RenderNotes()
    {
        if(NotesDraft is null)return;
        _renderingNotes=true;
        try
        {
            if(NoteProject is null)NotesDraft.ActiveProject=NotesDraft.Projects.FirstOrDefault(p=>p.DeletedUtc is null&&!p.Archived)?.Id;
            _projectTabs.Children.Clear();
            foreach(var project in NotesDraft.Projects.Where(p=>p.DeletedUtc is null&&!p.Archived || p.Id==NotesDraft.ActiveProject&&p.DeletedUtc is null))
            {
                var b=NotesButton(project.Name+(project.Archived?"（归档）":""),_=>SelectNoteProject(project.Id));b.Content=new TextBlock{Text=project.Name+(project.Archived?"（归档）":""),TextTrimming=TextTrimming.CharacterEllipsis};b.MaxWidth=140;b.ToolTip=project.Name+" · 右键管理项目";
                b.Background=project.Id==NotesDraft.ActiveProject&&!_notesTrash?Brush("#DDF2FF"):Brushes.Transparent;
                b.Foreground=Brush("#0877BE");b.Margin=new Thickness(0,0,4,0);
                AutomationProperties.SetAutomationId(b,"Project-"+project.Id);
                b.MouseRightButtonUp+=(_,e)=>{e.Handled=true;NoteProjectMenu(b,project);};_projectTabs.Children.Add(b);
            }
            _addNote.IsEnabled=NoteProject is {Archived:false}&&!_notesTrash;
            _noteSearch.IsEnabled=NoteProject is not null&&!_notesTrash;
            _noteSearch.Text=NoteProject?.Search??"";_notesSearchHint.Text="搜索"+(NoteProject?.Name??"当前项目")+"的记录…";_notesSearchHint.Visibility=_noteSearch.Text.Length==0?Visibility.Visible:Visibility.Collapsed;_noteSearch.ToolTip="搜索"+(NoteProject?.Name??"当前项目")+"的记录";
            RenderNoteCards();UpdateNotesColumns();
            _notesStatus.Text=(NoteProject?.Name??"便签")+(_notesDirty?" · 保存中…":" · 已保存");
        }
        finally{_renderingNotes=false;}
        var offset=NoteProject?.Scroll??0;
        Dispatcher.BeginInvoke(()=>{_renderingNotes=true;try{var p=NoteProject;var box=FindVisualChildren<TextBox>(_noteCards).FirstOrDefault(b=>b.Tag as string==p?.SelectedNote+":"+p?.EditorKey);if(box is not null&&p is not null){box.Focus();box.Select(Math.Min(p.Caret,box.Text.Length),Math.Min(p.SelectionLength,box.Text.Length-Math.Min(p.Caret,box.Text.Length)));}_notesScroll.ScrollToVerticalOffset(offset);}finally{_renderingNotes=false;}},DispatcherPriority.Loaded);
    }
    private void UpdateNoteCounts()
    {
        if(_notesTrash){_notesCounts.Text="回收站 · 通过项目管理恢复项目或记录";return;}
        var notes=NotesDraft.Notes.Where(n=>n.ProjectId==NoteProject?.Id&&n.DeletedUtc is null).ToList();
        var tasks=notes.SelectMany(n=>n.Tasks).Where(t=>!string.IsNullOrWhiteSpace(t.Text)).ToList();
        var search=NoteProject?.Search??"";
        _notesCounts.Text=(NoteProject?.Name??"尚未创建项目")+$" · {notes.Count} 条记录"+(search.Length>0?$" · 匹配 {notes.Count(MatchesNote)} 条":"")+
            (tasks.Count>0?$"\n项目待办：待完成 {tasks.Count(t=>!t.Done)} · 已完成 {tasks.Count(t=>t.Done)}":"");
    }
    private bool MatchesNote(ProjectNote note)
    {
        var search=NoteProject?.Search??"";return search.Length==0||(note.Title+"\n"+note.Text+"\n"+string.Join("\n",note.Tasks.Select(t=>t.Text))).Contains(search,StringComparison.OrdinalIgnoreCase);
    }
    private void RenderNoteCards()
    {
        if(_notesComposing)return;
        _shutter.InvalidateSnapshot();_noteCards.Children.Clear();UpdateNoteCounts();
        var notes=NotesDraft.Notes.Where(n=>_notesTrash?n.DeletedUtc is not null:n.ProjectId==NoteProject?.Id&&n.DeletedUtc is null&&MatchesNote(n)).OrderByDescending(n=>n.Favorite).ThenBy(n=>n.CreatedUtc).ToList();
        foreach(var note in notes)AddNoteCard(note);
        if(notes.Count==0)
        {
            var empty=NotesText(_notesTrash?"回收站为空":NoteProject is null?"先新建一个项目，再开始记录":!string.IsNullOrEmpty(NoteProject.Search)?"未找到匹配记录":NoteProject.Archived?"这个项目已归档":"这个项目还没有记录\n点击『记一条』开始",12);
            empty.Margin=new Thickness(8,36,8,20);_noteCards.Children.Add(empty);
            if(NoteProject is null&&!_notesTrash)_noteCards.Children.Add(NotesButton("＋ 新建项目",_=>NewNoteProject(),true));
        }
    }
    private TextBox NoteEditor(string text,ProjectNote note,Action<string> changed,bool multiline=false,string key="title")
    {
        var editor=new TextBox{Text=text,FontSize=12,Padding=new Thickness(4),BorderThickness=new Thickness(0),Background=Brushes.Transparent,
            AcceptsReturn=multiline,TextWrapping=TextWrapping.Wrap,IsReadOnly=_notesTrash||NoteProject?.Archived==true,MinHeight=26};
        editor.Tag=note.Id+":"+key;
        TrackComposition(editor);
        editor.SelectionChanged+=(_,_)=>{if(_renderingNotes||!editor.IsKeyboardFocused||NoteProject is null)return;NoteProject.EditorKey=key;NoteProject.Caret=editor.SelectionStart;NoteProject.SelectionLength=editor.SelectionLength;NotesChanged();};
        editor.TextChanged+=(_,_)=>{changed(editor.Text);NotesChanged(true,note);UpdateNoteCounts();};
        editor.GotKeyboardFocus+=(_,_)=>{if(NoteProject is not null){NoteProject.SelectedNote=note.Id;NoteProject.EditorKey=key;NotesChanged();}};
        return editor;
    }
    private void AddNoteCard(ProjectNote note)
    {
        var content=new StackPanel();
        var border=new Border { CornerRadius=new CornerRadius(10),BorderBrush=Brush("#CDE5F6"),BorderThickness=new Thickness(1),Padding=new Thickness(10),Margin=new Thickness(0,0,0,10),Background=Brush("#F5FAFE"),Child=content };
        var header=new DockPanel();var menu=NotesButton("···",b=>NoteCardMenu(b,note));DockPanel.SetDock(menu,Dock.Right);header.Children.Add(menu);
        var title=NoteEditor(note.Title,note,t=>note.Title=t);title.FontWeight=FontWeights.SemiBold;title.FontSize=14;AutomationProperties.SetName(title,"记录标题");header.Children.Add(title);content.Children.Add(header);
        content.Children.Add(NotesText((_notesTrash?NotesDraft.Projects.Single(p=>p.Id==note.ProjectId).Name+" · ":"")+note.ModifiedUtc.ToLocalTime().ToString("MM月dd日 HH:mm")+(note.Favorite?" · 常用":""),10));
        if(note.Kind=="text")
        {
            var body=NoteEditor(note.Text,note,t=>note.Text=t,true,"body");body.MinHeight=65;AutomationProperties.SetName(body,"文字记录正文");content.Children.Add(body);
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
                    var check=new CheckBox{IsChecked=task.Done,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(2,7,6,0),IsEnabled=!_notesTrash&&NoteProject?.Archived!=true};
                    AutomationProperties.SetName(check,"完成待办");DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
                    var input=NoteEditor(task.Text,note,t=>task.Text=t,key:"task-"+note.Tasks.IndexOf(task));input.TextDecorations=task.Done?TextDecorations.Strikethrough:null;input.Foreground=Brush(task.Done?"#8998A6":"#35566E");
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
            if(!_notesTrash&&NoteProject?.Archived!=true)content.Children.Add(NotesButton("＋ 添加待办",_=>{if(_notesComposing)return;note.Tasks.Add(new());NotesChanged(true,note);BuildRows(note.Tasks.Count-1);}));
        }
        _noteCards.Children.Add(border);
    }
    private bool ConfirmNotes(string message)
    {_interactions++;try{return UiDialogs.Show(this,message,"项目便签",MessageBoxButton.OKCancel)==MessageBoxResult.OK;}finally{_interactions--;}}
    private void NoteCardMenu(Button owner,ProjectNote note)
    {
        if(_notesTrash)
        {
            var items=new List<object>();var project=NotesDraft.Projects.Single(p=>p.Id==note.ProjectId);
            if(project.DeletedUtc is null&&!project.Archived)items.Add(Menu("恢复到原项目",()=>ChangeNotes(()=>{note.DeletedUtc=null;note.DeleteBatch=null;})));
            var restore=new MenuItem{Header="恢复到其他项目"};foreach(var p in NotesDraft.Projects.Where(p=>p.DeletedUtc is null&&!p.Archived))restore.Items.Add(Menu(p.Name,()=>ChangeNotes(()=>{note.ProjectId=p.Id;note.DeletedUtc=null;note.DeleteBatch=null;})));items.Add(restore);
            items.Add(Menu("导出记录…",()=>ExportNotes(note)));
            items.Add(Menu("永久删除…",()=>{if(ConfirmNotes("永久删除这条记录？此操作无法撤销。"))ChangeNotes(()=>NotesDraft.Notes.Remove(note));}));Popup(owner,items);return;
        }
        var move=new MenuItem{Header="移动到其他项目",IsEnabled=NoteProject?.Archived!=true};
        foreach(var p in NotesDraft.Projects.Where(p=>p.Id!=note.ProjectId&&p.DeletedUtc is null&&!p.Archived))move.Items.Add(Menu(p.Name,()=>ChangeNotes(()=>{note.ProjectId=p.Id;note.ModifiedUtc=DateTime.UtcNow;})));
        Popup(owner,[Menu(note.Favorite?"取消常用":"设为常用",()=>ChangeNotes(()=>note.Favorite=!note.Favorite),NoteProject?.Archived!=true),move,
            Menu("导出记录…",()=>ExportNotes(note)),Menu("移到回收站",()=>ChangeNotes(()=>{note.DeletedUtc=DateTime.UtcNow;note.DeleteBatch=null;}),NoteProject?.Archived!=true)]);
    }
    private void NoteProjectMenu(Button owner,NoteProject project)
    {
        var idx=NotesDraft.Projects.IndexOf(project);
        Popup(owner,[Menu("重命名",()=>{var name=Ask("重命名项目","项目名称",project.Name);if(name is not null)ChangeNotes(()=>NotesStore.RenameProject(NotesDraft,project,name));}),
            Menu("向前移动",()=>ChangeNotes(()=>{NotesDraft.Projects.Remove(project);NotesDraft.Projects.Insert(idx-1,project);}),idx>0),
            Menu("向后移动",()=>ChangeNotes(()=>{NotesDraft.Projects.Remove(project);NotesDraft.Projects.Insert(idx+1,project);}),idx<NotesDraft.Projects.Count-1),
            Menu(project.Archived?"恢复为活动项目":"归档项目",()=>ChangeNotes(()=>{project.Archived=!project.Archived;if(project.Archived)NotesDraft.ActiveProject=null;})),
            Menu("导出项目…",()=>ExportNotes(project:project)),Menu("删除项目…",()=>DeleteNoteProject(owner,project))]);
    }
    private void DeleteNoteProject(Button owner,NoteProject project)
    {
        var count=NotesDraft.Notes.Count(n=>n.ProjectId==project.Id&&n.DeletedUtc is null);
        var move=new MenuItem{Header=$"将 {count} 条记录移到其他项目后删除"};
        foreach(var p in NotesDraft.Projects.Where(p=>p.Id!=project.Id&&p.DeletedUtc is null&&!p.Archived))move.Items.Add(Menu(p.Name,()=>{if(ConfirmNotes($"删除项目“{project.Name}”，并将 {count} 条记录移至“{p.Name}”？"))ChangeNotes(()=>NotesStore.DeleteProject(NotesDraft,project,p.Id));}));
        Popup(owner,[move,Menu($"项目及 {count} 条记录移入回收站…",()=>{if(ConfirmNotes($"将项目“{project.Name}”及其 {count} 条记录移入回收站？可恢复。"))ChangeNotes(()=>NotesStore.DeleteProject(NotesDraft,project));})]);
    }
    private void ProjectManagement(Button owner)
    {
        var archived=new MenuItem{Header="已归档项目"};
        foreach(var p in NotesDraft.Projects.Where(p=>p.DeletedUtc is null&&p.Archived))archived.Items.Add(Menu(p.Name,()=>SelectNoteProject(p.Id)));
        var deleted=new MenuItem{Header="恢复已删除项目"};
        foreach(var p in NotesDraft.Projects.Where(p=>p.DeletedUtc is not null))deleted.Items.Add(Menu(p.Name,()=>{var name=Ask("恢复项目","项目名称（重名时请改名）",p.Name);if(name is not null)ChangeNotes(()=>{NotesStore.RestoreProject(NotesDraft,p,name);NotesDraft.ActiveProject=p.Id;_notesTrash=false;});}));
        Popup(owner,[Menu("新建项目…",NewNoteProject),archived,deleted,Menu("便签回收站",()=>{if(FlushNotes()){_notesTrash=true;RenderNotes();}}),Menu("返回活动项目",()=>{if(FlushNotes()){_notesTrash=false;NotesDraft.ActiveProject=null;RenderNotes();}}),Menu("导出全部草稿…",()=>ExportNotes())]);
    }
    private void ExportNotes(ProjectNote? note=null,NoteProject? project=null)
    {
        _interactions++;
        try
        {
            var picker=new Microsoft.Win32.SaveFileDialog{Title="导出便签",Filter=note is null?"JSON 文件 (*.json)|*.json":"文字文件 (*.txt)|*.txt",FileName=note is null?"项目便签导出.json":"便签记录.txt"};
            if(picker.ShowDialog(this)!=true)return;
            var text=note is null?JsonSerializer.Serialize(new{format=1,projects=NotesDraft.Projects.Where(p=>project is null||p.Id==project.Id),notes=NotesDraft.Notes.Where(n=>project is null||n.ProjectId==project.Id)},new JsonSerializerOptions{WriteIndented=true}):
                note.Title+"\n\n"+(note.Kind=="text"?note.Text:string.Join("\n",note.Tasks.Select(t=>(t.Done?"[x] ":"[ ] ")+t.Text)));
            File.WriteAllText(picker.FileName,text);_notesStatus.Text="已导出当前内容";
        }
        catch(Exception ex){UiDialogs.Show(this,"导出失败："+ex.Message,"项目便签");}
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
    private void OnNotesSplitStarted(object sender,DragStartedEventArgs e){FinishPanelMotion();_interactions++;}
    private void OnNotesSplitCompleted(object sender,DragCompletedEventArgs e)
    {
        _interactions=Math.Max(0,_interactions-1);NotesDraft.LeftFraction=Math.Clamp(LauncherColumn.ActualWidth/Math.Max(1,Surface.Width-7),.2,.8);NotesChanged();UpdateSizingLayout();
    }
    private void UpdateNotesColumns()
    {
        if(NotesDraft is null||LauncherColumn is null)return;
        var available=Math.Max(1,(SplitLayout.ActualWidth>0?SplitLayout.ActualWidth:Surface.Width-2)-5);
        // Reserve readable notes and at least three icon columns; permit clipping only below the supported minimum/work area.
        var minLeft=Math.Min(330,available*.52);var minRight=Math.Min(300,available*.48);
        var left=Math.Clamp(available*NotesDraft.LeftFraction,minLeft,Math.Max(minLeft,available-minRight));
        LauncherColumn.Width=new GridLength(left);NotesColumn.Width=new GridLength(1,GridUnitType.Star);
    }
}
