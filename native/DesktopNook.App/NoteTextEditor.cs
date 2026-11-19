using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class NoteTextEditor : RichTextBox
{
    public NoteTextEditor(string text, NoteStrikeRange[]? strikes, Action<List<NoteTask>>? pasteTasks = null)
    {
        FontSize = 12;
        Padding = new Thickness(4);
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        MinHeight = 26;
        AcceptsReturn = pasteTasks is null;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

        var paragraph = NewParagraph();
        Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
        int start = 0;
        foreach (var mark in strikes ?? [])
        {
            AddText(paragraph, text[start..mark.Start], false);
            AddText(paragraph, text.Substring(mark.Start, mark.Length), true);
            start = mark.Start + mark.Length;
        }
        AddText(paragraph, text[start..], false);
        DataObject.AddPastingHandler(this, (_, args) =>
        {
            string format = args.DataObject.GetDataPresent(DataFormats.UnicodeText) ? DataFormats.UnicodeText : DataFormats.Text;
            if (!args.DataObject.GetDataPresent(format))
            {
                args.CancelCommand();
                return;
            }
            if (pasteTasks is not null && !IsReadOnly && args.DataObject.GetData(format) is string pasted &&
                NotesTaskPaste.ParseMultiline(pasted) is { Count: > 0 } batch)
            {
                args.CancelCommand();
                // Let the paste command finish before moving or replacing editors in its route.
                Dispatcher.BeginInvoke(() => pasteTasks(batch), DispatcherPriority.Input);
                return;
            }
            args.FormatToApply = format;
        });
    }

    internal string PlainText => ReadContent().Text;

    internal (int Start, int Length) CharacterSelection()
    {
        int start = CharacterOffset(Selection.Start);
        return (start, CharacterOffset(Selection.End) - start);
    }

    internal void SelectCharacters(int start, int length)
    {
        var textLength = PlainText.Length;
        start = Math.Clamp(start, 0, textLength);
        length = Math.Clamp(length, 0, textLength - start);
        Selection.Select(PositionAt(start), PositionAt(start + length));
    }

    internal void SetDone(bool done)
    {
        foreach (var paragraph in Document.Blocks.OfType<Paragraph>())
            paragraph.TextDecorations = done ? TextDecorations.Strikethrough : null;
    }

    internal bool SelectionIsStruck()
    {
        var (start, length) = CharacterSelection();
        if (length == 0) return false;
        var (text, marks) = ReadContent();
        if (marks is null) return false;
        int end = Math.Min(start + length, text.Length);
        int cursor = start;
        foreach (var mark in marks)
        {
            int markEnd = mark.Start + mark.Length;
            if (markEnd <= cursor) continue;
            if (mark.Start >= end) break;
            if (mark.Start > cursor && !text.AsSpan(cursor, mark.Start - cursor).Trim("\r\n").IsEmpty) return false;
            cursor = Math.Min(end, markEnd);
            if (cursor == end) return true;
        }
        return cursor > start && text.AsSpan(cursor, end - cursor).Trim("\r\n").IsEmpty;
    }

    internal void ToggleStrike()
    {
        if (IsReadOnly || Selection.IsEmpty) return;
        Selection.ApplyPropertyValue(Inline.TextDecorationsProperty,
            SelectionIsStruck() ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    internal (string Text, NoteStrikeRange[]? Strikes) ReadContent()
    {
        var text = new StringBuilder();
        var marks = new List<NoteStrikeRange>();
        foreach (var part in Segments())
        {
            if (part.Struck && part.Text.Length > 0)
            {
                if (marks.Count > 0 && marks[^1].Start + marks[^1].Length == text.Length)
                    marks[^1] = marks[^1] with { Length = marks[^1].Length + part.Text.Length };
                else
                    marks.Add(new(text.Length, part.Text.Length));
            }
            text.Append(part.Text);
        }
        return (text.ToString(), marks.Count == 0 ? null : marks.ToArray());
    }

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0) };

    private static void AddText(Paragraph paragraph, string text, bool struck)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            if (lines[i].Length == 0) continue;
            var run = new Run(lines[i]);
            if (struck) run.TextDecorations = TextDecorations.Strikethrough;
            paragraph.Inlines.Add(run);
        }
    }

    private int CharacterOffset(TextPointer pointer)
    {
        var end = Document.Blocks.LastBlock?.ContentEnd ?? Document.ContentEnd;
        if (pointer.CompareTo(end) > 0) pointer = end;
        return new TextRange(Document.ContentStart, pointer).Text.ReplaceLineEndings("\n").Length;
    }

    private TextPointer PositionAt(int offset)
    {
        foreach (var segment in Segments())
        {
            if (offset <= segment.Text.Length)
                return segment.IsRun
                    ? segment.Start.GetPositionAtOffset(offset, LogicalDirection.Forward) ?? segment.End
                    : offset == 0 ? segment.Start : segment.End;
            offset -= segment.Text.Length;
        }
        return Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    private readonly record struct Segment(string Text, TextPointer Start, TextPointer End, bool Struck, bool IsRun);

    private IEnumerable<Segment> Segments()
    {
        Paragraph? previous = null;
        foreach (var paragraph in Document.Blocks.OfType<Paragraph>())
        {
            if (previous is not null)
                yield return new("\n", previous.ContentEnd, paragraph.ContentStart, false, false);

            foreach (var segment in Segments(paragraph.Inlines, false))
                yield return segment;
            previous = paragraph;
        }
    }

    private static IEnumerable<Segment> Segments(InlineCollection inlines, bool inheritedStrike)
    {
        foreach (var inline in inlines)
        {
            bool struck = inline.ReadLocalValue(Inline.TextDecorationsProperty) is TextDecorationCollection marks
                ? marks.Any(mark => mark.Location == TextDecorationLocation.Strikethrough)
                : inheritedStrike;
            if (inline is Run run)
                yield return new(run.Text, run.ContentStart, run.ContentEnd, struck, true);
            else if (inline is Span span)
            {
                foreach (var segment in Segments(span.Inlines, struck))
                    yield return segment;
            }
            else if (inline is LineBreak)
                yield return new("\n", inline.ElementStart, inline.ElementEnd, false, false);
        }
    }
}
