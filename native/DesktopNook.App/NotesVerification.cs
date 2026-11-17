using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class NotesVerification
{
    internal static async Task Run(MainWindow window,string root)
    {
        var checks=new List<string>();string failure="";
        void Check(bool value,string text){if(!value)throw new Exception(text);checks.Add(text);}
        IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
        {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T t)yield return t;foreach(var item in Find<T>(child))yield return item;}}
        TextBox Field(string name)=>Find<TextBox>(window.NotesHost).First(b=>AutomationProperties.GetName(b)==name);
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);window.UpdateLayout();}
        void Snapshot(string name)
        {
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(root,name+".png"));encoder.Save(file);
        }
        try
        {
            if(!App.IsSandbox)throw new Exception("便签验证必须使用独立沙箱。");
            window.KeepOpenForVerification=true;window.PanelMotion.Stop();window.Opacity=1;await Layout();
            Check(!window.NotesDraft.Projects.Any(),"首次启动不创建演示项目");
            var p=NotesStore.AddProject(window.NotesDraft,"项目A");var other=NotesStore.AddProject(window.NotesDraft,"项目B");window.NotesStore.Commit(window.NotesDraft);
            window.SelectNoteProject(p.Id);window.NotesDraft.Notes.Add(new(){ProjectId=p.Id,Kind="text",Title="旧版文字记录"});window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();
            Field("记录标题").Text="需求沟通";var body=Field("文字记录正文");body.Focus();body.Text="今天确认了项目需求。\n下一步：整理方案并跟进。";
            await Task.Delay(750);
            Check(new NotesStore(App.Store.DataDirectory).State.Notes.Single().Text.Contains("下一步"),"中文多行文字在停止输入后自动持久保存");
            Check(ReferenceEquals(body,Field("文字记录正文")),"自动保存不重建编辑控件或打断正文焦点");
            window.SelectNoteProject(other.Id);await Layout();Check(!Find<TextBox>(window.NotesHost).Any(b=>AutomationProperties.GetName(b)=="文字记录正文"),"不同项目内容隔离");
            window.SelectNoteProject(p.Id);await Layout();Check(Field("文字记录正文").Text.Contains("下一步"),"切回项目保留正文");
            window.SelectCategory("office");await Layout();Check(Field("文字记录正文").Text.Contains("下一步"),"左侧分类切换不重建右侧内容");
            var remembered=Field("文字记录正文");remembered.Focus();remembered.Select(3,2);window.FlushNotes(false);
            window.SelectNoteProject(other.Id);await Layout();window.SelectNoteProject(p.Id);await Layout();
            Check(Field("文字记录正文").SelectionStart==3&&Field("文字记录正文").SelectionLength==2,"切换项目恢复编辑位置与选区");
            var addButton=Find<Button>(window.NotesHost).Single(b=>AutomationProperties.GetAutomationId(b)=="AddProjectNote");addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Layout();
            Check(window.NotesDraft.Notes.Last().Kind=="todo"&&window.LastMenu?.IsOpen!=true,"记一条直接新增待办，不弹出文字类型菜单");
            Check(Find<TextBlock>(window.NotesHost).Any(t=>t.Text=="标签"),"右侧标题显示标签");
            var taskBox=Find<TextBox>(window.NotesHost).Last();taskBox.Text="确认设计方案";
            taskBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});await Layout();
            Check(window.NotesDraft.Notes.Last().Tasks.Count==2,"待办回车新增一项");
            Find<TextBox>(window.NotesHost).Last().Text="整理项目资料";
            var check=Find<CheckBox>(window.NotesHost).First();check.IsChecked=true;check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.FlushNotes(false)&&new NotesStore(App.Store.DataDirectory).State.Notes.Last().Tasks[0].Done,"待办勾选及正文持久保存");
            Field("搜索当前分类的记录").Text="需求沟通";await Task.Delay(500);await Layout();
            Check(!Find<CheckBox>(window.NotesHost).Any(),"项目内搜索按记录内容过滤");
            window.AddProjectNote();await Layout();Check(Field("搜索当前分类的记录").Text=="","搜索状态下新建清除筛选使新记录可见");
            Field("文字记录正文").Text="保存失败仍需保留的草稿";
            var notes=window.NotesStore.DirectoryPath;var displaced=notes+"-held";Directory.Move(notes,displaced);File.WriteAllText(notes,"阻止创建目录");
            try{Check(!window.FlushNotes(false),"磁盘写入失败返回失败状态");Check(window.NotesDraft.Notes.Any(n=>n.Text.Contains("仍需保留")),"写入失败保留内存草稿");}
            finally{File.Delete(notes);Directory.Move(displaced,notes);}
            Check(window.FlushNotes(false)&&new NotesStore(App.Store.DataDirectory).State.Notes.Any(n=>n.Text.Contains("仍需保留")),"解除故障后重试成功提交草稿");
            var stats=Find<TextBlock>(window.NotesHost).Single(t=>AutomationProperties.GetName(t)=="分类记录统计");
            Check(!stats.Text.Contains("\n")&&stats.TextWrapping==TextWrapping.NoWrap&&stats.Text.Contains("待完成")&&stats.Text.Contains("已完成"),"分类记录数与待办完成统计保持单行");
            window.Width=1120;window.Height=627.2;await Layout();Snapshot("notes-preview");
            Check(window.IconColumns==6,"默认宽度保留左侧六列图标");
            var font=Field("文字记录正文").TransformToAncestor(window).TransformBounds(new Rect(0,0,12,12)).Width;
            window.Width=800;await Layout();Check(window.IconColumns>=3&&window.NotesHost.ActualWidth>=299,$"窄窗保留三列图标与可读便签宽度（{window.IconColumns}列，{window.NotesHost.ActualWidth:F1}，窗口{window.ActualWidth:F1}，表面{window.Surface.Width:F1}，内部{window.SplitLayout.ActualWidth:F1}）");
            Check(Math.Abs(font-Field("文字记录正文").TransformToAncestor(window).TransformBounds(new Rect(0,0,12,12)).Width)<.5,"缩窄窗口不缩小正文字号");Snapshot("notes-narrow");
            window.Width=1120;await Layout();window.BeginHidePanel();await Task.Delay(650);Check(window.WindowState==WindowState.Minimized,"便签与左侧整体收起");
            window.ReleaseIdleResources();window.ShowPanel();await Task.Delay(650);await Layout();Check(Field("文字记录正文").Text.Contains("草稿"),"闲置释放后恢复编辑内容");

            Check(Directory.Exists(Path.Combine(App.Store.DataDirectory,"Notes")),"便签数据位于独立 Notes 目录");
            var active=window.NotesDraft.Projects.Single(x=>x.Id==p.Id);active.Archived=true;window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();
            Check(Find<TextBox>(window.NotesHost).Where(b=>AutomationProperties.GetName(b)=="文字记录正文").All(b=>b.IsReadOnly),"已归档项目的记录只读");
            active.Archived=false;window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();
            var target=Path.Combine(root,"stage-target.exe");File.Copy(Environment.ProcessPath!,target);
            var staged=LocalUpdater.Stage(Environment.ProcessPath!,target,App.Store.DataDirectory,root,0,App.Options,App.Store.State);
            var backup=Path.Combine(Path.GetDirectoryName(staged.Plan)!,"metadata-backup","Notes","notes.json");
            Check(File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(window.NotesStore.FilePath)),"更新暂存备份逐字节包含便签正文");
            for(int i=1;i<=7;i++){var source=Path.Combine(App.Store.DesktopRoots[0],$"示例应用 {i}.url");File.WriteAllText(source,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");App.Store.Collect(source,"office");}
            window.NotesDraft.Notes.Clear();window.NotesDraft.Notes.Add(new(){ProjectId=p.Id,Kind="todo",Title="本周待办",Tasks=[new(){Text="整理需求资料"},new(){Text="确认设计方案"}]});
            window.NotesDraft.Notes.Add(new(){ProjectId=p.Id,Title="项目待办",Kind="todo",Tasks=[new(){Text="整理项目资料"},new(){Text="确认设计方案"},new(){Text="建立项目档案",Done=true}]});
            active.SelectedNote=null;active.Scroll=0;active.Search="";window.NotesStore.Commit(window.NotesDraft);window.Render();window.RenderNotes();await Layout();await NotesSplitVerification.Run(window,checks);window.NotesDraft.LeftFraction=.646;window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();Snapshot("notes-final-preview");
        }
        catch(Exception ex){failure=ex.ToString();}
        File.WriteAllText(Path.Combine(root,"notes-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks},new JsonSerializerOptions{WriteIndented=true}));
        Application.Current.Shutdown(failure.Length==0?0:1);
    }
}
