using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow
{
    private sealed class NoteProjectDrag(Button button, NoteProject project, Point start)
    {
        public Button Button { get; } = button;
        public NoteProject Project { get; } = project;
        public Point Start { get; } = start;
        public DispatcherTimer? ScrollTimer { get; set; }
        public bool Started => ScrollTimer is not null;
    }

    private NoteProjectDrag? _noteProjectDrag;

    private void EnableNoteProjectDrag(Button button, NoteProject project)
    {
        button.Tag = project;
        button.ToolTip = $"{project.Name} · 左右拖动排序 · 右键管理分类";
        button.PreviewMouseLeftButtonDown += (_, args) =>
        {
            _noteProjectDrag = new NoteProjectDrag(button, project, args.GetPosition(_projectScroll));
            // Let the button capture the mouse and handle a normal click.
        };
        button.PreviewMouseMove += (_, args) =>
        {
            if (_noteProjectDrag is not { } drag || drag.Button != button || args.LeftButton != MouseButtonState.Pressed)
                return;
            if (!drag.Started)
            {
                if (_notesComposing || Math.Abs(args.GetPosition(_projectScroll).X - drag.Start.X) < SystemParameters.MinimumHorizontalDragDistance)
                    return;
                _interactions++;
                button.Cursor = Cursors.SizeWE;
                drag.ScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(45)
                };
                drag.ScrollTimer.Tick += (_, _) => ScrollNoteProjectDrag(drag);
                drag.ScrollTimer.Start();
            }
            UpdateNoteInsertionMark(NoteProjectDropTarget(drag), horizontal: true);
            args.Handled = true;
        };
        button.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (_noteProjectDrag is not { } drag || drag.Button != button) return;
            if (!drag.Started)
            {
                _noteProjectDrag = null;
                return;
            }

            var destination = NoteProjectDropTarget(drag);
            args.Handled = true;
            EndNoteProjectDrag(drag);
            if (destination is not { } drop || _notesComposing) return;
            var target = (NoteProject)drop.Tab.Tag;
            double offset = _projectScroll.HorizontalOffset;
            ChangeNotes(() => NotesStore.ReorderProject(NotesDraft, drag.Project, target, drop.After));
            Dispatcher.BeginInvoke(() =>
            {
                _projectScroll.ScrollToHorizontalOffset(offset);
                _projectTabs.Children.OfType<Button>().FirstOrDefault(tab =>
                    (tab.Tag as NoteProject)?.Id == drag.Project.Id)?.BringIntoView();
            }, DispatcherPriority.Loaded);
        };
        button.LostMouseCapture += (_, _) =>
        {
            if (_noteProjectDrag is { } drag && drag.Button == button) EndNoteProjectDrag(drag);
        };
    }

    private void EndNoteProjectDrag(NoteProjectDrag drag)
    {
        _noteProjectDrag = null;
        if (!drag.Started) return;
        drag.ScrollTimer?.Stop();
        drag.Button.ClearValue(CursorProperty);
        _interactions--;
        UpdateNoteInsertionMark(null);
        if (drag.Button.IsMouseCaptured) drag.Button.ReleaseMouseCapture();
    }

    private (FrameworkElement Tab, bool After)? NoteProjectDropTarget(NoteProjectDrag drag)
    {
        Point pointer = Mouse.GetPosition(_projectScroll);
        if (!IsOverProjectTabs(pointer)) return null;
        var tabs = _projectTabs.Children.OfType<Button>().Where(tab => tab.Tag is NoteProject).ToList();
        int source = tabs.IndexOf(drag.Button);
        if (tabs.Count < 2 || source < 0) return null;

        double x = Mouse.GetPosition(_projectTabs).X;
        for (int index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            double left = tab.TranslatePoint(new Point(), _projectTabs).X;
            if (x < left)
                return index == source || index == source + 1 ? null : (tab, false);
            if (x < left + tab.ActualWidth)
                return index == source ? null : (tab, index > source);
        }
        return source == tabs.Count - 1 ? null : (tabs[^1], true);
    }

    private bool IsOverProjectTabs(Point pointer) =>
        pointer.X >= 0 && pointer.X < _projectScroll.ViewportWidth &&
        pointer.Y >= 0 && pointer.Y < _projectScroll.ViewportHeight;

    private void ScrollNoteProjectDrag(NoteProjectDrag drag)
    {
        Point pointer = Mouse.GetPosition(_projectScroll);
        if (!IsOverProjectTabs(pointer)) return;
        double edge = Math.Min(22, _projectScroll.ViewportWidth / 4);
        double delta = pointer.X < edge ? -12 : pointer.X > _projectScroll.ViewportWidth - edge ? 12 : 0;
        if (delta == 0) return;
        _projectScroll.ScrollToHorizontalOffset(_projectScroll.HorizontalOffset + delta);
        UpdateNoteInsertionMark(NoteProjectDropTarget(drag), horizontal: true);
    }
}
