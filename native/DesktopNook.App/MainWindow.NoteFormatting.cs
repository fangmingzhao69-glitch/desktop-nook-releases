using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopNook.App;

public partial class MainWindow
{
    private NoteTextEditor? _activeNoteEditor;
    private Action? _updateNoteFormatButtons;

    private NoteTextEditor? FormattingEditor => !_showNotesArchive && _activeNoteEditor is { IsReadOnly: false } editor &&
        _noteCards is not null && _noteCards.IsAncestorOf(editor) ? editor : null;

    private FrameworkElement CreateNoteFormatToolbar()
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(tools, "NotesFormatToolbar");
        var bold = Tool(new TextBlock { Text = "B", FontWeight = FontWeights.Bold }, "NotesFormatBold");
        var strike = Tool(new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }, "NotesFormatStrike");
        var color = Tool(new TextBlock { Text = "颜色" }, "NotesFormatColor");
        bold.Click += (_, _) => Edit(editor => editor.ToggleBold());
        strike.Click += (_, _) => Edit(editor => editor.ToggleStrike());
        color.Click += (_, _) =>
        {
            if (FormattingEditor is not { Selection.IsEmpty: false } editor || _notesComposing) return;
            Popup(color, NoteHighlightOptions(editor));
        };
        tools.Children.Add(bold); tools.Children.Add(strike); tools.Children.Add(color);
        _updateNoteFormatButtons = Update;
        Update();
        return tools;

        Button Tool(object content, string id)
        {
            var button = new Button { Content = content, MinWidth = 30, Height = 30, FontSize = 12,
                Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(1, 0, 1, 0), BorderThickness = new Thickness(1) };
            button.SetValue(UiMotion.FeedbackProperty, null);
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetAutomationId(button, id);
            return button;
        }
        void Edit(Action<NoteTextEditor> edit)
        {
            if (FormattingEditor is not { Selection.IsEmpty: false } editor || _notesComposing) return;
            editor.Focus(); edit(editor); Update();
        }
        void Update()
        {
            var editor = FormattingEditor;
            bool enabled = !_notesComposing && editor is { Selection.IsEmpty: false };
            bool isBold = enabled && editor!.SelectionIsBold(), isStruck = enabled && editor!.SelectionIsStruck();
            foreach (var button in new[] { bold, strike, color }) button.IsEnabled = enabled;
            State(bold, isBold, isBold ? "取消加粗" : "字体加粗", "Ctrl+B");
            State(strike, isStruck, isStruck ? "取消删除线" : "添加删除线");
            color.ToolTip = enabled ? "为选中文字设置背景标注色" : "先选中正文或待办内容中的文字";
            AutomationProperties.SetName(color, "文字背景色");
        }
        void State(Button button, bool selected, string action, string? shortcut = null)
        {
            button.Background = selected ? Brush("SelectionBrush") : Brushes.Transparent;
            button.BorderBrush = selected ? Brush("AccentInkBrush") : Brushes.Transparent;
            button.ToolTip = button.IsEnabled ? action + (shortcut is null ? "" : "（" + shortcut + "）") : "先选中正文或待办内容中的文字";
            AutomationProperties.SetName(button, action);
            AutomationProperties.SetItemStatus(button, selected ? "已设置" : "未设置");
        }
    }

    private void ResetNoteFormatting()
    {
        _activeNoteEditor = null;
        _updateNoteFormatButtons?.Invoke();
    }

    private IEnumerable<object> NoteHighlightOptions(NoteTextEditor editor)
    {
        var palette = NamedColorPalette.Create(NamedColorPalette.Markers, 4, hex =>
        {
            if (LastMenu is { } menu) menu.IsOpen = false;
            editor.Focus(); editor.Highlight(hex);
        });
        palette.IsEnabled = !editor.IsReadOnly && !editor.Selection.IsEmpty;
        if (editor.Selection.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush selected)
            NamedColorPalette.Select(palette, $"#{selected.Color.R:X2}{selected.Color.G:X2}{selected.Color.B:X2}");
        yield return new MenuItem { Header = palette, HeaderTemplate = null, ToolTip = null, StaysOpenOnClick = true, Focusable = false };
        yield return new Separator();
        yield return Menu("清除背景色", () => { editor.Focus(); editor.Highlight(null); }, !editor.IsReadOnly && !editor.Selection.IsEmpty);
    }
}
