using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow
{
    private Thumb? _noteDragHandle;
    private NoteInsertionMark? _noteInsertionMark;
    private DispatcherTimer? _noteDragScroll;

    private List<ProjectNote> ActiveCategoryNotes() =>
        NotesDraft.Notes.Where(note => note.ProjectId == NoteProject?.Id && !note.Archived)
            .OrderBy(note => note.IsCompletedChecklist()).ToList();

    private bool CanReorderNotes =>
        !_showNotesArchive && !_notesComposing && NoteProject is { Archived: false };

    private FrameworkElement NoteOrderBar(ProjectNote note, int number, string metadata, bool readOnly)
    {
        if (readOnly || !CanReorderNotes)
        {
            return NotesText($"{number} · {metadata}", 10);
        }

        var handle = new Thumb
        {
            Style = (Style)FindResource("NoteOrderHandle"),
            Tag = number,
            DataContext = metadata,
            ToolTip = $"第 {number} 条 · 上下拖动这条排序栏"
        };
        Point start = default;
        bool moved = false;
        bool HasMoved()
        {
            var pointer = Mouse.GetPosition(_notesScroll);
            return Math.Abs(pointer.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
        }
        AutomationProperties.SetAutomationId(handle, "NoteOrder-" + note.Id);
        AutomationProperties.SetName(handle, $"第 {number} 条，拖动排序");
        handle.DragStarted += (_, _) =>
        {
            if (!CanReorderNotes) return;
            _noteDragHandle = handle;
            _interactions++;
            start = Mouse.GetPosition(_notesScroll);
            moved = false;
        };
        handle.DragDelta += (_, _) =>
        {
            if (_noteDragHandle != handle) return;
            if (!moved)
            {
                if (!HasMoved()) return;
                moved = true;
                _noteDragScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Input,
                    (_, _) => ScrollNoteDrag(note), Dispatcher);
                _noteDragScroll.Start();
            }
            UpdateNoteInsertionMark(NoteDropTarget(note));
        };
        handle.DragCompleted += (_, args) =>
        {
            if (_noteDragHandle != handle) return;
            _noteDragHandle = null;
            _noteDragScroll?.Stop();
            _noteDragScroll = null;
            _interactions--;
            UpdateNoteInsertionMark(null);
            if (args.Canceled || (!moved && !HasMoved()) || !CanReorderNotes || NoteDropTarget(note) is not { } drop)
                return;
            _notesSearch.Stop();
            ChangeNotes(() =>
            {
                NotesStore.ReorderNote(NotesDraft, note, (ProjectNote)drop.Card.Tag, drop.After);
                // Keep the list at the drop location instead of focusing the last text editor.
                NoteProject!.Scroll = _notesScroll.VerticalOffset;
                NoteProject.SelectedNote = null;
            });
            Dispatcher.BeginInvoke(() =>
                FindVisualChildren<Thumb>(_noteCards).FirstOrDefault(item =>
                    AutomationProperties.GetAutomationId(item) == "NoteOrder-" + note.Id)?.BringIntoView(),
                DispatcherPriority.Loaded);
        };
        return handle;
    }

    private (Border Card, bool After)? NoteDropTarget(ProjectNote source)
    {
        var pointer = Mouse.GetPosition(_notesScroll);
        if (pointer.X < 0 || pointer.X >= _notesScroll.ViewportWidth ||
            pointer.Y < 0 || pointer.Y >= _notesScroll.ViewportHeight) return null;

        var cards = _noteCards.Children.OfType<Border>()
            .Where(card => card.Tag is ProjectNote note && note.IsCompletedChecklist() == source.IsCompletedChecklist())
            .ToList();
        var bounds = _noteCards.Children.OfType<Border>()
            .FirstOrDefault(card => card.Tag is ProjectNote note && note.IsCompletedChecklist());
        if (bounds is not null && (Mouse.GetPosition(_noteCards).Y >= bounds.TranslatePoint(new Point(), _noteCards).Y)
            != source.IsCompletedChecklist()) return null;
        int sourceIndex = cards.FindIndex(card => card.Tag == source);
        if (cards.Count < 2 || sourceIndex < 0) return null;
        var y = Mouse.GetPosition(_noteCards).Y;
        for (int index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            double top = card.TranslatePoint(new Point(), _noteCards).Y;
            if (y < top)
                return index == sourceIndex || index == sourceIndex + 1 ? null : (card, false);
            if (y < top + card.ActualHeight)
                return index == sourceIndex ? null : (card, index > sourceIndex);
        }
        return sourceIndex == cards.Count - 1 ? null : (cards[^1], true);
    }

    private void ScrollNoteDrag(ProjectNote source)
    {
        if (_noteDragHandle is null) return;
        var pointer = Mouse.GetPosition(_notesScroll);
        if (pointer.X < 0 || pointer.X >= _notesScroll.ViewportWidth ||
            pointer.Y < 0 || pointer.Y >= _notesScroll.ViewportHeight) return;
        double delta = pointer.Y < 24 ? -18 : pointer.Y > _notesScroll.ViewportHeight - 24 ? 18 : 0;
        if (delta != 0)
        {
            _notesScroll.ScrollToVerticalOffset(_notesScroll.VerticalOffset + delta);
            UpdateNoteInsertionMark(NoteDropTarget(source));
        }
    }

    private void UpdateNoteInsertionMark((FrameworkElement Card, bool After)? destination, bool horizontal = false)
    {
        if (_noteInsertionMark is { } existing)
        {
            if (destination is { } same && existing.AdornedElement == same.Card && existing.After == same.After &&
                existing.Horizontal == horizontal) return;
            existing.Layer.Remove(existing);
            _noteInsertionMark = null;
        }
        if (destination is { } target && AdornerLayer.GetAdornerLayer(target.Card) is { } layer)
        {
            _noteInsertionMark = new NoteInsertionMark(target.Card, target.After, layer, horizontal) { IsHitTestVisible = false };
            layer.Add(_noteInsertionMark);
        }
    }

    private sealed class NoteInsertionMark(UIElement card, bool after, AdornerLayer layer, bool horizontal) : Adorner(card)
    {
        internal bool After { get; } = after;
        internal AdornerLayer Layer { get; } = layer;
        internal bool Horizontal { get; } = horizontal;

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            var pen = new Pen(Brush("AccentInkBrush"), 2);
            if (Horizontal)
            {
                double x = After ? size.Width - 1 : 1;
                drawingContext.DrawLine(pen, new Point(x, 3), new Point(x, size.Height - 3));
                return;
            }
            drawingContext.DrawRoundedRectangle(null, pen, new Rect(new Point(), size), 10, 10);
            double y = After ? size.Height - 2 : 2;
            drawingContext.DrawLine(pen, new Point(6, y), new Point(size.Width - 6, y));
        }
    }
}
