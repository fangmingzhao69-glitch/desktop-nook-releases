using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow
{
    private void AddNoteTasks(ProjectNote note, StackPanel content, DockPanel header, bool readOnly)
    {
        var rows = new StackPanel();
        content.Children.Add(rows);
        if (!readOnly)
        {
            var add = NotesButton("＋ 添加", _ => AddTask());
            AutomationProperties.SetName(add, "添加待办");
            add.ToolTip = "在清单第一行添加待办";
            DockPanel.SetDock(add, Dock.Right);
            header.Children.Insert(header.Children.Count - 1, add);
        }
        BuildRows();

        void AddTask()
        {
            if (_notesComposing) return;
            bool wasCompleted = note.IsCompletedChecklist();
            note.Tasks.Insert(0, new());
            RefreshRows(wasCompleted, 0);
        }

        void PasteTasks(NoteTask target, List<NoteTask> tasks)
        {
            if (_notesComposing || _showNotesArchive || !NotesDraft.Notes.Contains(note) ||
                NoteProject?.Id != note.ProjectId || !note.Tasks.Contains(target)) return;

            bool wasCompleted = note.IsCompletedChecklist();
            if (string.IsNullOrWhiteSpace(target.Text) && !target.Done) note.Tasks.Remove(target);
            note.Tasks.InsertRange(0, tasks);
            int firstPending = tasks.FindIndex(task => !task.Done);
            RefreshRows(wasCompleted, Math.Max(0, firstPending));
        }

        void RefreshRows(bool wasCompleted, int focus = -1)
        {
            NotesChanged(true, note);
            if (wasCompleted != note.IsCompletedChecklist()) RenderNoteCards();
            else
            {
                BuildRows();
                UpdateNoteCounts();
            }

            if (focus >= 0)
                Dispatcher.BeginInvoke(() =>
                {
                    if (FindNoteEditor(note.Id, $"task-{focus}") is NoteTaskEditor editor)
                    {
                        editor.Focus();
                        editor.SelectCharacters(0, 0);
                        editor.BringIntoView();
                    }
                }, DispatcherPriority.Loaded);
        }

        void BuildRows()
        {
            rows.Children.Clear();
            foreach (var (task, index) in note.Tasks.Select((task, index) => (task, index)).OrderBy(entry => entry.task.Done))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                var check = new CheckBox
                {
                    IsChecked = task.Done,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(2, 7, 6, 0),
                    IsEnabled = !readOnly
                };
                AutomationProperties.SetName(check, "完成待办");
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);

                var editor = new NoteTaskEditor(task, batch => PasteTasks(task, batch))
                {
                    IsReadOnly = readOnly,
                    Foreground = Brush(task.Done ? "#8998A6" : "#35566E"),
                    ToolTip = readOnly ? null : "粘贴多行文字可自动拆成多条待办"
                };
                AutomationProperties.SetName(editor, "待办内容");
                ConfigureNoteEditor(editor, note, $"task-{index}", () => ChangeNotes(() =>
                {
                    note.Tasks.Remove(task);
                    note.ModifiedUtc = DateTime.UtcNow;
                }));
                editor.TextChanged += (_, _) =>
                {
                    var (text, strikes) = editor.ReadContent();
                    if (task.Text == text && (task.Strikes ?? []).SequenceEqual(strikes ?? [])) return;
                    task.Text = text;
                    task.Strikes = strikes;
                    NotesChanged(true, note);
                    UpdateNoteCounts();
                };
                check.Click += (_, _) =>
                {
                    bool wasCompleted = note.IsCompletedChecklist();
                    task.Done = check.IsChecked == true;
                    if (wasCompleted != note.IsCompletedChecklist())
                    {
                        RefreshRows(wasCompleted);
                        return;
                    }

                    editor.SetDone(task.Done);
                    editor.Foreground = Brush(task.Done ? "#8998A6" : "#35566E");
                    int position = note.TasksInDisplayOrder().TakeWhile(other => other != task).Count();
                    if (rows.Children.IndexOf(row) != position)
                    {
                        bool hadFocus = check.IsKeyboardFocused;
                        rows.Children.Remove(row);
                        rows.Children.Insert(position, row);
                        if (hadFocus) check.Focus();
                    }
                    NotesChanged(true, note);
                    UpdateNoteCounts();
                };
                editor.PreviewKeyDown += (_, args) =>
                {
                    if (_notesComposing || readOnly || args.Key == Key.ImeProcessed) return;
                    if (args.Key == Key.Enter)
                    {
                        args.Handled = true;
                        AddTask();
                    }
                    else if (args.Key == Key.Back && task.Text.Length == 0 && note.Tasks.Count > 1)
                    {
                        args.Handled = true;
                        bool wasCompleted = note.IsCompletedChecklist();
                        note.Tasks.RemoveAt(index);
                        RefreshRows(wasCompleted, Math.Max(0, index - 1));
                    }
                };
                row.Children.Add(editor);
                rows.Children.Add(row);
            }
        }
    }
}
