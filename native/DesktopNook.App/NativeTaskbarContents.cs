using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace DesktopNook.App;

internal static class NativeTaskbarContents
{
    internal readonly record struct Measurement(int PackedWidth, double Left, double Right, bool HasOverflow,
        Rect[] AppButtons, double? TrayGap, double AppSlot, int ChromeCount, double ChromeWidth,
        double? TrayStart, double? LeftEnd, Rect OverflowButton);

    // This runs on a background thread: an Explorer automation call can wait on its UI thread.
    internal static Measurement? Measure(nint taskbar)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            var walker = TreeWalker.RawViewWalker;
            var cache = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition };
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.NameProperty);
            var repeater = FindRepeater(root, walker, cache, 0);
            if (repeater is null) return null;

            var items = new List<Rect>();
            var tray = new List<Rect>();
            var appButtons = new List<Rect>();
            Rect overflowButton = Rect.Empty;
            bool overflow = false;
            int visited = 0;
            for (var item = walker.GetFirstChild(repeater, cache); item is not null && visited++ < 128;
                 item = walker.GetNextSibling(item, cache))
            {
                var info = item.Cached;
                var rect = info.BoundingRectangle;
                if (info.IsOffscreen || rect.IsEmpty || rect.Width < 2 || rect.Height < 4) continue;
                if (info.ClassName.StartsWith("SystemTray.", StringComparison.Ordinal))
                {
                    tray.Add(rect);
                    if (info.ClassName.Contains("Chevron", StringComparison.OrdinalIgnoreCase) ||
                        info.AutomationId.Contains("Chevron", StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Contains("显示隐藏的图标", StringComparison.Ordinal) ||
                        info.Name.Contains("顯示隱藏的圖示", StringComparison.Ordinal) ||
                        info.Name.Contains("show hidden icons", StringComparison.OrdinalIgnoreCase))
                        overflowButton = rect;
                    continue;
                }
                if (info.ControlType != ControlType.Button && info.ControlType != ControlType.ListItem) continue;
                overflow |= info.ClassName.Contains("Overflow", StringComparison.OrdinalIgnoreCase) ||
                    info.AutomationId.Contains("Overflow", StringComparison.OrdinalIgnoreCase);
                if (info.ClassName.Contains("TaskListButton", StringComparison.Ordinal))
                    appButtons.Add(rect);
                else items.Add(rect);
            }
            if (items.Count + appButtons.Count == 0 || (tray.Count == 0 && root.Current.ClassName == "Shell_TrayWnd")) return null;
            // Under pressure Explorer can report a clipped last app button. Reserve a normal slot
            // immediately so opening one app does not first produce a partially grown capsule.
            var widths = appButtons.Select(rect => rect.Width).Order().ToArray();
            double slot = widths.Length > 2 ? widths[widths.Length / 2] : 0;
            double apps = widths.Sum(width => width < slot * .88 ? slot : width);
            double chromeWidth = PackedWidth(items);
            int width = (int)Math.Ceiling(chromeWidth + apps + PackedWidth(tray));
            var all = items.Concat(appButtons).Concat(tray).ToArray();
            double? trayStart = tray.Count > 0 ? tray.Min(rect => rect.Left) : null;
            double? leftEnd = items.Count + appButtons.Count > 0 ? items.Concat(appButtons).Max(rect => rect.Right) : null;
            if (overflowButton.IsEmpty && tray.Count > 0)
                overflowButton = tray.MinBy(rect => rect.Left);
            double? trayGap = trayStart - leftEnd;
            return new(width, all.Min(rect => rect.Left), all.Max(rect => rect.Right), overflow, appButtons.ToArray(), trayGap,
                widths.Length == 0 ? 0 : widths[widths.Length / 2], items.Count, chromeWidth, trayStart, leftEnd, overflowButton);
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static AutomationElement? FindRepeater(AutomationElement parent, TreeWalker walker, CacheRequest cache, int depth)
    {
        if (depth >= 5) return null;
        int visited = 0;
        for (var item = walker.GetFirstChild(parent, cache); item is not null && visited++ < 16;
             item = walker.GetNextSibling(item, cache))
        {
            if (item.Cached.AutomationId == "TaskbarFrameRepeater") return item;
            if (FindRepeater(item, walker, cache, depth + 1) is { } found) return found;
        }
        return null;
    }

    private static double PackedWidth(List<Rect> rectangles)
    {
        double total = 0, right = double.NegativeInfinity;
        foreach (var rect in rectangles.OrderBy(rect => rect.Left))
        {
            // Include native spacing between nearby buttons, but not the flexible gap between groups.
            double start = rect.Left > right + 12 ? rect.Left : right;
            total += Math.Max(0, rect.Right - start);
            right = Math.Max(right, rect.Right);
        }
        return total;
    }
}
