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
            if (target.Text.Length == 0 && !target.Done) note.Tasks.Remove(target);
            note.Tasks.InsertRange(0, tasks);
            RefreshRows(wasCompleted, 0);
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
                    if (FindNoteEditor(note.Id, $"task-{focus}") is NoteTextEditor editor)
                    {
                        editor.Focus();
                        editor.SelectCharacters(0, 0);
                        editor.BringIntoView();
                    }
                }, DispatcherPriority.Loaded);
        }

        void BuildRows()
        {
            ResetNoteFormatting();
            rows.Children.Clear();
            foreach (var (task, index) in note.Tasks.Select((task, index) => (task, index)).OrderBy(entry => entry.task.Done))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                var check = new CheckBox
                {
                    IsChecked = task.Done,
                    Style = (Style)FindResource("FoldTaskCheckBox"),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(2, 0, 6, 0),
                    Padding = new Thickness(6, 3.5, 6, 12.5),
                    MinHeight = 32,
                    IsEnabled = !readOnly
                };
                AutomationProperties.SetName(check, "完成待办");
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);

                // Tasks are inserted at the front. Count back from the end so
                // the oldest item is 1 and newly added items get the next number.
                int number = note.Tasks.Count - index;
                var sequence = new TextBlock
                {
                    Text = number.ToString(System.Globalization.CultureInfo.CurrentCulture) + ".",
                    MinWidth = Math.Max(18, note.Tasks.Count.ToString().Length * 8 + 6),
                    TextAlignment = TextAlignment.Right,
                    Foreground = Brush("TextSecondaryBrush"),
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 4, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                AutomationProperties.SetAutomationId(sequence, $"NoteTaskNumber-{note.Id}-{index}");
                AutomationProperties.SetName(sequence, $"第 {number} 项待办");
                DockPanel.SetDock(sequence, Dock.Left);
                row.Children.Add(sequence);

                var editor = new NoteTextEditor(task.Text, task.Strikes, batch => PasteTasks(task, batch), task.Highlights, task.Bolds)
                {
                    IsReadOnly = readOnly,
                    IsInactiveSelectionHighlightEnabled = !readOnly,
                    Foreground = Brush(task.Done ? "TextSecondaryBrush" : "TextPrimaryBrush"),
                    ToolTip = readOnly ? null : "粘贴多行文字可自动拆成多条待办"
                };
                editor.SetDone(task.Done);
                AutomationProperties.SetName(editor, "待办内容");
                ConfigureNoteEditor(editor, note, $"task-{index}", () => ChangeNotes(() =>
                {
                    note.Tasks.Remove(task);
                    note.ModifiedUtc = DateTime.UtcNow;
                }));
                editor.TextChanged += (_, _) =>
                {
                    var (text, strikes) = editor.ReadContent();
                    var highlights = editor.ReadHighlights();
                    var bolds = editor.ReadBolds();
                    if (task.Text == text && (task.Strikes ?? []).SequenceEqual(strikes ?? []) &&
                        (task.Highlights ?? []).SequenceEqual(highlights ?? []) && (task.Bolds ?? []).SequenceEqual(bolds ?? [])) return;
                    task.Text = text;
                    task.Strikes = strikes;
                    task.Highlights = highlights;
                    task.Bolds = bolds;
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
                    editor.Foreground = Brush(task.Done ? "TextSecondaryBrush" : "TextPrimaryBrush");
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
