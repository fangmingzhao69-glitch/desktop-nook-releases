using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
        NoteTextEditor Body(ProjectNote? note = null) => Find<NoteTextEditor>(window.NotesHost).First(editor =>
            AutomationProperties.GetName(editor) == "文字记录正文" && (note is null || Equals(editor.Tag, note.Id + ":body")));
        Button ProjectTab(NoteProject project) => Find<Button>(window.NotesHost)
            .Single(button => AutomationProperties.GetAutomationId(button) == "Project-" + project.Id);
        void ClickMenu(MenuItem item)
        {
            if (window.LastMenu is { } menu) menu.IsOpen = false;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        MenuItem OpenLastCardMenu(string item)
        {
            var menuButton = Find<Button>(window.NotesHost).Last(b => AutomationProperties.GetName(b) == "便签操作");
            menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return window.LastMenu!.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, item));
        }
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);window.UpdateLayout();}
        NoteTextEditor[] TaskEditors(ProjectNote note) => Find<NoteTextEditor>(window.NotesHost)
            .Where(editor => editor.Tag is string key && key.StartsWith(note.Id + ":task-", StringComparison.Ordinal)).ToArray();
        ContextMenu OpenEditorMenu(NoteTextEditor editor)
        {
            editor.RaiseEvent((ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                new object[] { editor, true }, null)!);
            return window.LastMenu!;
        }
        bool PasteText(NoteTextEditor editor, string text)
        {
            var data = new DataObject(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Rtf, @"{\rtf1\ansi\strike sample}");
            var args = new DataObjectPastingEventArgs(data, false, DataFormats.Rtf);
            editor.RaiseEvent(args);
            return args.CommandCancelled;
        }
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
            window.RenderNotes();
            await Layout();
            await NotesSplitVerification.MoveNoteProject(window, ProjectTab(p));
            Check(window.NotesDraft.ActiveProject == p.Id, "真实鼠标单击仍能切换便签分类");
            await NotesSplitVerification.MoveNoteProject(window, ProjectTab(other), ProjectTab(p), cancel: true);
            Check(window.NotesDraft.Projects.First().Id == p.Id && window.NotesDraft.ActiveProject == p.Id,
                "Esc 取消分类拖动，顺序和当前分类保持");
            await NotesSplitVerification.MoveNoteProject(window, ProjectTab(other), ProjectTab(p));
            var storedProjects = new NotesStore(App.Store.DataDirectory).State;
            Check(storedProjects.Projects.Select(project => project.Id).SequenceEqual(new[] { other.Id, p.Id }) &&
                storedProjects.ActiveProject == p.Id && ProjectTab(other).TranslatePoint(new Point(), window).X <
                ProjectTab(p).TranslatePoint(new Point(), window).X, "拖动未选中的分类向左排序，保存并保持当前分类");
            await NotesSplitVerification.MoveNoteProject(window, ProjectTab(other), ProjectTab(p));
            Check(new NotesStore(App.Store.DataDirectory).State.Projects.First().Id == p.Id,
                "向右拖动分类恢复次序，重读顺序一致");

            var extraProjects = Enumerable.Range(1, 6).Select(number => NotesStore.AddProject(window.NotesDraft, $"滚动测试分类 {number}")).ToList();
            window.NotesDraft.ActiveProject = p.Id;
            window.NotesStore.Commit(window.NotesDraft);
            window.RenderNotes();
            await Layout();
            var tabsScroll = Find<ScrollViewer>(window.NotesHost).Single(scroll => AutomationProperties.GetAutomationId(scroll) == "NoteProjectTabs");
            tabsScroll.ScrollToLeftEnd();
            await Layout();
            await NotesSplitVerification.MoveNoteProject(window, ProjectTab(p), tabsScroll,
                dropPoint: new Point(tabsScroll.ViewportWidth - 7, 15), holdMilliseconds: 840);
            storedProjects = new NotesStore(App.Store.DataDirectory).State;
            Check(storedProjects.Projects.FindIndex(project => project.Id == p.Id) >= 2 &&
                storedProjects.ActiveProject == p.Id, "分类标签在横向滚动后仍能跨多项保存排序");
            window.NotesDraft.Projects.RemoveAll(project => extraProjects.Contains(project));
            NotesStore.ReorderProject(window.NotesDraft, p, other, after: false);
            window.NotesStore.Commit(window.NotesDraft);
            window.SelectNoteProject(p.Id);window.NotesDraft.Notes.Add(new(){ProjectId=p.Id,Kind="text",Title="旧版文字记录"});window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();
            Field("记录标题").Text="需求沟通";var body=Body();body.Focus();
            string textRecord = "今天确认了项目需求。\n下一步：整理方案并跟进。";
            body.Selection.Text = textRecord;
            await Task.Delay(750);
            Check(new NotesStore(App.Store.DataDirectory).State.Notes.Single().Text.Contains("下一步"),"中文多行文字在停止输入后自动持久保存");
            Check(ReferenceEquals(body,Body()),"自动保存不重建编辑控件或打断正文焦点");
            var textNote = window.NotesDraft.Notes.Single();
            var bodyStrike = Find<Button>(window.NotesHost).Single(button =>
                AutomationProperties.GetAutomationId(button) == "NoteTextStrike-" + textNote.Id);
            int newline = textRecord.IndexOf('\n');
            body.SelectCharacters(newline - 2, 5);
            bodyStrike.Focus();
            bodyStrike.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NoteStrikeRange[] expectedTextStrikes = [new(newline - 2, 2), new(newline + 1, 2)];
            Check(body.PlainText == textRecord && textNote.BodyStrikes?.SequenceEqual(expectedTextStrikes) == true,
                "标题旁删除线按钮支持正文跨行选区并保持原文");
            Check(window.FlushNotes(false) && new NotesStore(App.Store.DataDirectory).State.Notes.Single()
                .BodyStrikes?.SequenceEqual(expectedTextStrikes) == true &&
                NotesMarkdown.Export(window.NotesDraft, textNote).Contains("~~求。~~\n~~下一~~"),
                "文字记录删除线可保存重读并导出为 Markdown");
            window.SelectNoteProject(other.Id);await Layout();Check(!Find<NoteTextEditor>(window.NotesHost).Any(b=>AutomationProperties.GetName(b)=="文字记录正文"),"不同项目内容隔离");
            window.SelectNoteProject(p.Id);await Layout();
            Check(Body().PlainText.Contains("下一步") && Body().ReadContent().Strikes?.SequenceEqual(expectedTextStrikes) == true,
                "切回项目保留正文和删除线");
            var reloadedBody = Body();
            reloadedBody.Focus();
            reloadedBody.SelectCharacters(newline - 2, 5);
            ClickMenu(OpenEditorMenu(reloadedBody).Items.OfType<MenuItem>().Single(item => Equals(item.Header, "取消删除线")));
            Check(reloadedBody.ReadContent().Strikes is null && textNote.BodyStrikes is { Length: 0 },
                "正文右键可以取消跨行删除线");
            window.SelectCategory("office");await Layout();Check(Body().PlainText.Contains("下一步"),"左侧分类切换不重建右侧内容");
            var remembered=Body();remembered.Focus();remembered.SelectCharacters(3,2);window.FlushNotes(false);
            window.SelectNoteProject(other.Id);await Layout();window.SelectNoteProject(p.Id);await Layout();
            Check(Body().CharacterSelection()==(3,2),"切换项目恢复编辑位置与选区");
            var addButton=Find<Button>(window.NotesHost).Single(b=>AutomationProperties.GetAutomationId(b)=="AddProjectNote");addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Layout();
            Check(window.NotesDraft.Notes.Last().Kind=="todo"&&window.LastMenu?.IsOpen!=true,"记一条直接新增待办，不弹出文字类型菜单");
            Check(Find<TextBlock>(window.NotesHost).Any(t=>t.Text=="便签"),"右侧标题显示便签");
            var launcherHeading = Find<TextBlock>(window).First(text => text.Text == "轻匣");
            var notesHeading = Find<TextBlock>(window.NotesHost).Single(text => text.Text == "便签");
            Check(notesHeading.FontFamily.Equals(launcherHeading.FontFamily) && notesHeading.FontWeight == launcherHeading.FontWeight &&
                notesHeading.FontSize == launcherHeading.FontSize && Field("记录标题").FontFamily.Equals(launcherHeading.FontFamily),
                "便签标题和编辑框共用轻匣字体，面板标题字号和字重一致");
            var customTitle=Find<TextBox>(window.NotesHost).Last(b=>AutomationProperties.GetName(b)=="记录标题");customTitle.Text="可编辑清单名称";
            Check(window.FlushNotes(false)&&new NotesStore(App.Store.DataDirectory).State.Notes.Last().Title=="可编辑清单名称","自定义清单名称持久保存");
            var checklist = window.NotesDraft.Notes.Last();
            var taskBox = TaskEditors(checklist).Single();
            string[] pasted = ["1、事项一", "~~已核对~~，继续处理", "", "  保留尾部空格  ", "- [x] 原文符号"];
            Check(PasteText(taskBox, string.Join("\r\n", pasted)), "多行剪贴板走批量粘贴");
            await Layout();
            Check(TaskEditors(checklist).Select(editor => editor.PlainText).SequenceEqual(pasted) &&
                checklist.Tasks.All(task => !task.Done && task.Strikes is null),
                "有编号和无编号原样拆分，空行、空格和标记符号均保留");
            Check(!PasteText(TaskEditors(checklist)[0], "追加几个字"), "单行仍走原来的文字粘贴");
            Check(PasteText(TaskEditors(checklist)[0], "- [ ] 补充一项\n- [x] 已核对一项"), "现有清单可以继续批量粘贴");
            await Layout();
            Check(checklist.Tasks.Count == pasted.Length + 2 && checklist.Tasks[0].Text == "- [ ] 补充一项" &&
                !checklist.Tasks[1].Done && checklist.Tasks[2].Text == "1、事项一", "新增批次保留原符号及已有事项");
            Check(window.FlushNotes(false) && new NotesStore(App.Store.DataDirectory).State.Notes.Last().Tasks.Count == pasted.Length + 2,
                "批量拆分结果可以正常保存和重读");
            checklist.Tasks.Clear();
            checklist.Tasks.Add(new NoteTask());
            window.NotesStore.Commit(window.NotesDraft);
            window.RenderNotes();
            await Layout();
            taskBox = TaskEditors(checklist).Single();
            taskBox.Selection.Text = "确认设计方案";
            taskBox.Focus();
            taskBox.SelectCharacters(0, 0);
            var strike = Find<Button>(window.NotesHost).Single(button =>
                AutomationProperties.GetAutomationId(button) == "NoteTaskStrike-" + checklist.Id);
            Check(!strike.IsEnabled && ReferenceEquals(strike.Parent, Find<TextBox>(window.NotesHost).Last(b =>
                    AutomationProperties.GetName(b) == "记录标题").Parent), "删除线按钮和清单标题同行");
            taskBox.SelectCharacters(2, 2);
            Check(strike.IsEnabled && AutomationProperties.GetName(strike) == "添加删除线", "选中文字可从标题栏加删除线");
            strike.Focus();
            strike.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(taskBox.ReadContent().Strikes?.SequenceEqual([new NoteStrikeRange(2, 2)]) == true &&
                taskBox.PlainText == "确认设计方案" && !checklist.Tasks.Single().Done, "所选文字加删除线且待办仍未完成");
            taskBox.SelectCharacters(2, 2);
            Check(AutomationProperties.GetName(strike) == "取消删除线", "标题栏显示当前操作");
            strike.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(taskBox.ReadContent().Strikes is null, "已标记文字可用同一个标题栏按钮取消删除线");
            taskBox.SelectCharacters(2, 2);
            ClickMenu(OpenEditorMenu(taskBox).Items.OfType<MenuItem>().Single(item => Equals(item.Header, "添加删除线")));
            Check(window.FlushNotes(false) && new NotesStore(App.Store.DataDirectory).State.Notes.Last().Tasks.Single()
                .Strikes?.SequenceEqual([new NoteStrikeRange(2, 2)]) == true, "部分删除线独立保存");
            window.RenderNotes();
            await Layout();
            taskBox = TaskEditors(checklist).Single();
            Check(taskBox.ReadContent().Strikes?.SequenceEqual([new NoteStrikeRange(2, 2)]) == true &&
                taskBox.FontFamily.Equals(launcherHeading.FontFamily), "重开编辑器重现部分删除线并沿用字体");
            var addTask = Find<Button>(window.NotesHost).Single(button => AutomationProperties.GetName(button) == "添加待办");
            Check(ReferenceEquals(addTask.Parent, Find<TextBox>(window.NotesHost).Last(b =>
                AutomationProperties.GetName(b) == "记录标题").Parent), "添加待办与清单标题同一行");
            addTask.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout();
            Check(TaskEditors(checklist).Select(editor => editor.PlainText).SequenceEqual(["", "确认设计方案"]) &&
                TaskEditors(checklist)[0].IsKeyboardFocused, "点击添加在第一行创建并聚焦新待办");
            TaskEditors(checklist)[0].Selection.Text = "整理项目资料";
            var firstCheck = Find<CheckBox>(window.NotesHost).First();
            firstCheck.IsChecked = true;
            firstCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout();
            var reordered = TaskEditors(checklist);
            Check(reordered.Select(editor => editor.PlainText).SequenceEqual(["确认设计方案", "整理项目资料"]) &&
                ((Paragraph)reordered[1].Document.Blocks.FirstBlock!).TextDecorations.Count > 0,
                "勾选的待办移到底部并显示整行删除线");
            var firstRow = (DockPanel)reordered[0].Parent;
            var checkbox = firstRow.Children.OfType<CheckBox>().Single();
            var boxBorder = (Border)checkbox.Template.FindName("Box", checkbox);
            double boxTop = boxBorder.TranslatePoint(new Point(), firstRow).Y;
            // The 12-DIP editor font leaves four DIPs above the visible glyphs in its first line box.
            double textTop = reordered[0].TranslatePoint(new Point(0, reordered[0].Padding.Top + 4), firstRow).Y;
            Check(Math.Abs(boxTop - textTop) <= 1 && checkbox.VerticalAlignment == VerticalAlignment.Top,
                "待办首行文字与勾选框上缘对齐");
            Snapshot("notes-checklist-complete-order");
            var lastCheck = Find<CheckBox>(window.NotesHost).Last();
            lastCheck.IsChecked = false;
            lastCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout();
            Check(TaskEditors(checklist).Select(editor => editor.PlainText).SequenceEqual(["整理项目资料", "确认设计方案"]),
                "取消勾选恢复未完成区和原先相对顺序");
            taskBox = TaskEditors(checklist)[0];
            taskBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});await Layout();
            Check(TaskEditors(checklist).Select(editor => editor.PlainText).SequenceEqual(["", "整理项目资料", "确认设计方案"]),
                "待办回车也在第一行新增一项");
            var disposable = TaskEditors(checklist)[0];
            var editMenu = OpenEditorMenu(disposable);
            Check(editMenu.Items.OfType<MenuItem>().Select(m=>m.Header?.ToString()).SequenceEqual(
                ["剪切","复制","粘贴","添加删除线","删除此项"]), "明细右键保持统一菜单并包含删除线和删除");
            ClickMenu(editMenu.Items.OfType<MenuItem>().Last()); await Layout();
            ClickMenu(OpenEditorMenu(TaskEditors(checklist)[0]).Items.OfType<MenuItem>().Last()); await Layout();
            Check(window.FlushNotes(false)&&new NotesStore(App.Store.DataDirectory).State.Notes.Last().Tasks.Count==1,"右键删除实际移除细项并保存");
            var later = new ProjectNote { ProjectId = checklist.ProjectId, Title = "后面的文字记录" };
            window.NotesDraft.Notes.Add(later);
            window.NotesStore.Commit(window.NotesDraft);
            window.RenderNotes();
            await Layout();
            ProjectNote[] Cards() => Find<Border>(window.NotesHost).Select(border => border.Tag).OfType<ProjectNote>().ToArray();
            Check(Array.IndexOf(Cards(), checklist) < Array.IndexOf(Cards(), later), "待办清单未完成时保持手动顺序");
            var check=Find<CheckBox>(window.NotesHost).First();check.IsChecked=true;check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout();
            Check(ReferenceEquals(Cards().Last(), checklist), "最后一项勾选后整份清单移到未完成记录之后");
            window.NotesDraft.Notes.Remove(later);
            window.NotesStore.Commit(window.NotesDraft);
            window.RenderNotes();
            await Layout();
            Check(window.FlushNotes(false) && new NotesStore(App.Store.DataDirectory).State.Notes.Last().Tasks.Single() is
                { Done: true, Strikes.Length: 1 }, "整项勾选时仍保留原部分删除线");
            var typeMenu = OpenLastCardMenu("便签类型");
            ClickMenu(typeMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "文字记录")));
            await Layout();
            var converted = window.NotesDraft.Notes.Last();
            Check(converted.Kind == "text" && Body(converted).AcceptsReturn &&
                Body(converted).PlainText == "确认设计方案" &&
                Body(converted).ReadContent().Strikes?.SequenceEqual([new NoteStrikeRange(2, 2)]) == true,
                "菜单切换为多行文字，保留待办原文与删除线");
            typeMenu = OpenLastCardMenu("便签类型");
            ClickMenu(typeMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "待办清单")));
            await Layout();
            Check(window.NotesDraft.Notes.Last().Tasks.Single() is { Done: true, Strikes.Length: 1 },
                "切回待办后保留未修改条目的勾选状态和部分删除线");
            Field("搜索当前分类的记录").Text="需求沟通";await Task.Delay(500);await Layout();
            Check(!Find<CheckBox>(window.NotesHost).Any(),"项目内搜索按记录内容过滤");
            window.AddProjectNote();await Layout();Check(Field("搜索当前分类的记录").Text=="","搜索状态下新建清除筛选使新记录可见");
            Body().SelectAll(); Body().Selection.Text="保存失败仍需保留的草稿";
            var notes=window.NotesStore.DirectoryPath;var displaced=notes+"-held";Directory.Move(notes,displaced);File.WriteAllText(notes,"阻止创建目录");
            try{Check(!window.FlushNotes(false),"磁盘写入失败返回失败状态");Check(window.NotesDraft.Notes.Any(n=>n.Text.Contains("仍需保留")),"写入失败保留内存草稿");}
            finally{File.Delete(notes);Directory.Move(displaced,notes);}
            Check(window.FlushNotes(false)&&new NotesStore(App.Store.DataDirectory).State.Notes.Any(n=>n.Text.Contains("仍需保留")),"解除故障后重试成功提交草稿");
            var stats=Find<TextBlock>(window.NotesHost).Single(t=>AutomationProperties.GetName(t)=="分类记录统计");
            Check(!stats.Text.Contains("\n")&&stats.TextWrapping==TextWrapping.NoWrap&&stats.Text.Contains("待完成")&&stats.Text.Contains("已完成"),"分类记录数与待办完成统计保持单行");
            window.Width=1120;window.Height=627.2;await Layout();Snapshot("notes-preview");
            Check(window.IconColumns==6,"默认宽度保留左侧六列图标");
            var font=Body().TransformToAncestor(window).TransformBounds(new Rect(0,0,12,12)).Width;
            window.Width=800;await Layout();Check(window.IconColumns>=3&&window.NotesHost.ActualWidth>=299,$"窄窗保留三列图标与可读便签宽度（{window.IconColumns}列，{window.NotesHost.ActualWidth:F1}，窗口{window.ActualWidth:F1}，表面{window.Surface.Width:F1}，内部{window.SplitLayout.ActualWidth:F1}）");
            Check(Math.Abs(font-Body().TransformToAncestor(window).TransformBounds(new Rect(0,0,12,12)).Width)<.5,"缩窄窗口不缩小正文字号");Snapshot("notes-narrow");
            window.Width=1120;await Layout();window.BeginHidePanel();await Task.Delay(650);Check(window.WindowState==WindowState.Minimized,"便签与左侧整体收起");
            window.ReleaseIdleResources();window.ShowPanel();await Task.Delay(650);await Layout();Check(Body().PlainText.Contains("草稿"),"闲置释放后恢复编辑内容");

            Check(Directory.Exists(Path.Combine(App.Store.DataDirectory,"Notes")),"便签数据位于独立 Notes 目录");
            var active=window.NotesDraft.Projects.Single(x=>x.Id==p.Id);active.Archived=true;window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();
            Check(Body().IsReadOnly,"已归档项目的记录只读");
            active.Archived=false;window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();
            var target=Path.Combine(root,"stage-target.exe");File.Copy(Environment.ProcessPath!,target);
            var staged=LocalUpdater.Stage(Environment.ProcessPath!,target,App.Store.DataDirectory,root,0,App.Options,App.Store.State);
            var backup=Path.Combine(Path.GetDirectoryName(staged.Plan)!,"metadata-backup","Notes","notes.json");
            Check(File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(window.NotesStore.FilePath)),"更新暂存备份逐字节包含便签正文");
            var longProject = NotesStore.AddProject(window.NotesDraft, "完整的归档分类名称用于检查窄窗口时的换行和滚动展示");
            longProject.Archived = true;
            var archivedTodo = new ProjectNote { ProjectId = other.Id, Title = "单独归档的便签", Archived = true };
            window.NotesDraft.Notes.Add(archivedTodo);
            window.NotesDraft.Notes.Add(new() { ProjectId = longProject.Id, Title = "整个分类的只读便签", Text = "归档内容" });
            window.NotesDraft.ActiveProject = p.Id;
            window.NotesStore.Commit(window.NotesDraft);
            window.RenderNotes();
            var archiveButton = Find<Button>(window.NotesHost).Single(button => AutomationProperties.GetAutomationId(button) == "NotesArchive");
            Check(Equals(archiveButton.Content, "查看归档"), "便签归档入口显示查看归档");
            archiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout();
            var archiveGroup = Find<TextBlock>(window.NotesHost).Single(text => text.Text.StartsWith(longProject.Name, StringComparison.Ordinal));
            Check(archiveGroup.Text == longProject.Name + "（整个分类已归档）" && archiveGroup.TextWrapping == TextWrapping.Wrap &&
                Find<TextBlock>(window.NotesHost).Any(text => text.Text == other.Name), "归档页同时显示单独归档的原分类与整个归档分类，长名称可完整换行");
            Check(Find<NoteTextEditor>(window.NotesHost).Any(box => box.PlainText == "归档内容" && box.IsReadOnly),
                "整个归档分类的正文可只读查看");
            Snapshot("notes-archive-preview");
            var categoryMenuButton = Find<Button>(window.NotesHost).Single(button => AutomationProperties.GetName(button) == "管理已归档分类：" + longProject.Name);
            categoryMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickMenu(window.LastMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "恢复整个分类")));
            await Layout();
            Check(!window.NotesDraft.Projects.Single(project => project.Id == longProject.Id).Archived &&
                window.NotesDraft.Notes.Single(note => note.Id == archivedTodo.Id).Archived, "恢复整个分类保留其他单独归档的便签");
            archiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Equals(archiveButton.Content, "查看归档"), "从归档返回后入口仍显示查看归档");
            window.SelectNoteProject(p.Id);
            await Layout();
            for(int i=1;i<=7;i++){var source=Path.Combine(App.Store.DesktopRoots[0],$"示例应用 {i}.url");File.WriteAllText(source,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");App.Store.Collect(source,"office");}
            window.NotesDraft.Notes.Clear();window.NotesDraft.Notes.Add(new(){ProjectId=p.Id,Kind="todo",Title="本周待办",Tasks=[new(){Text="整理需求资料"},new(){Text="确认设计方案"}]});
            window.NotesDraft.Notes.Add(new(){ProjectId=p.Id,Title="项目待办",Kind="todo",Tasks=[new(){Text="整理项目资料"},new(){Text="确认设计方案"},new(){Text="建立项目档案",Done=true}]});
            active.SelectedNote=null;active.Scroll=0;active.Search="";window.NotesStore.Commit(window.NotesDraft);window.Render();window.RenderNotes();await Layout();
            var cards = Find<Border>(window.NotesHost).Where(border => border.Tag is ProjectNote).ToList();
            var handles = Find<Thumb>(window.NotesHost).Where(thumb => AutomationProperties.GetAutomationId(thumb).StartsWith("NoteOrder-")).ToList();
            Check(handles.Select(handle => handle.Tag).SequenceEqual(new object[] { 1, 2 }), "分类内便签显示连续序号");
            Check(handles[0].ActualWidth >= cards[0].ActualWidth - 24, "排序栏覆盖卡片宽度，可从远离序号的位置拖动");
            var firstId = ((ProjectNote)cards[0].Tag).Id;
            var secondId = ((ProjectNote)cards[1].Tag).Id;
            await NotesSplitVerification.DragNote(window, handles[1], cards[0], cancel: true);
            Check(window.NotesDraft.Notes.First().Id == firstId, "Esc 取消便签拖动，顺序不变");
            await NotesSplitVerification.DragNote(window, handles[1], cards[0]);
            Check(new NotesStore(App.Store.DataDirectory).State.Notes.First().Id == secondId &&
                Find<Thumb>(window.NotesHost).First(thumb => AutomationProperties.GetAutomationId(thumb).StartsWith("NoteOrder-")).Tag.Equals(1),
                "真实鼠标拖动改变便签顺序及序号，重读顺序一致");
            ClickMenu(OpenLastCardMenu("上移一位"));
            await Layout();
            Check(new NotesStore(App.Store.DataDirectory).State.Notes.First().Id == firstId, "右键上移立即保存");
            cards = Find<Border>(window.NotesHost).Where(border => border.Tag is ProjectNote).ToList();
            handles = Find<Thumb>(window.NotesHost).Where(thumb => AutomationProperties.GetAutomationId(thumb).StartsWith("NoteOrder-")).ToList();
            await NotesSplitVerification.DragNote(window, handles[0], cards[1]);
            Check(new NotesStore(App.Store.DataDirectory).State.Notes.First().Id == secondId,
                "向下拖到下一张便签的上半部，松手后排到下一张便签之后");
            var hidden = new ProjectNote { ProjectId = p.Id, Title = "其他文字" };
            window.NotesDraft.Notes.Insert(1, hidden);
            window.NotesStore.Commit(window.NotesDraft);
            window.Width = 800;
            Field("搜索当前分类的记录").Text = "待办";
            await Task.Delay(450);
            await Layout();
            cards = Find<Border>(window.NotesHost).Where(border => border.Tag is ProjectNote).ToList();
            handles = Find<Thumb>(window.NotesHost).Where(thumb => AutomationProperties.GetAutomationId(thumb).StartsWith("NoteOrder-")).ToList();
            Check(handles.Select(handle => handle.Tag).SequenceEqual(new object[] { 1, 3 }), "搜索结果保留原位序号并可拖动");
            await NotesSplitVerification.DragNote(window, handles[1], cards[0]);
            var filteredOrder = new NotesStore(App.Store.DataDirectory).State.Notes;
            Check(filteredOrder.Count == 3 && filteredOrder[0].Id == firstId && filteredOrder.Any(note => note.Id == hidden.Id),
                "窄窗搜索时拖动并保存，保留未匹配便签");
            window.NotesDraft.Notes.Remove(hidden);
            window.NotesStore.Commit(window.NotesDraft);
            Field("搜索当前分类的记录").Text = "";
            window.Width = 1120;
            await Task.Delay(450);
            await Layout();
            var temporaryNotes = Enumerable.Range(1, 6)
                .Select(number => new ProjectNote { ProjectId = p.Id, Title = $"滚动验收 {number}", Text = "临时正文" })
                .ToList();
            window.NotesDraft.Notes.AddRange(temporaryNotes);
            window.NotesStore.Commit(window.NotesDraft);
            window.RenderNotes();
            await Layout();
            var cardsScroll = Find<ScrollViewer>(window.NotesHost).Single(scroll =>
                scroll.Content is StackPanel panel && panel.Children.OfType<Border>().Any(border => border.Tag is ProjectNote));
            cardsScroll.ScrollToTop();
            await Layout();
            var firstHandle = Find<Thumb>(window.NotesHost).First(thumb => AutomationProperties.GetAutomationId(thumb) == "NoteOrder-" + firstId);
            await NotesSplitVerification.DragNote(window, firstHandle, cardsScroll,
                dropPoint: new Point(24, cardsScroll.ViewportHeight - 8), holdMilliseconds: 720);
            var scrolledOrder = new NotesStore(App.Store.DataDirectory).State.Notes;
            Check(scrolledOrder.FindIndex(note => note.Id == firstId) >= 2,
                "拖到列表下缘会自动滚动，并可跨过多张便签保存新顺序");
            window.NotesDraft.Notes.RemoveAll(note => temporaryNotes.Contains(note));
            window.NotesStore.Commit(window.NotesDraft);
            window.RenderNotes();
            await Layout();
            await NotesSplitVerification.Run(window,checks);window.NotesDraft.LeftFraction=.646;window.NotesStore.Commit(window.NotesDraft);window.RenderNotes();await Layout();Snapshot("notes-final-preview");
        }
        catch(Exception ex){failure=ex.ToString();}
        File.WriteAllText(Path.Combine(root,"notes-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks},new JsonSerializerOptions{WriteIndented=true}));
        Application.Current.Shutdown(failure.Length==0?0:1);
    }
}
