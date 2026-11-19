using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

internal enum PanelEffect
{
    Shutters, Doors, Scatter, Wave, Relay, Curtain, Capsule, Rail, Split, Radial, Aperture
}

internal sealed class PanelEffectFrame : FrameworkElement
{
    internal sealed record Choice(PanelEffect Effect, string Label, int OpenMs, int CloseMs);
    internal static IReadOnlyList<Choice> Choices { get; } = Array.AsReadOnly(new[]
    {
        new Choice(PanelEffect.Shutters, "百叶翻转", 500, 500),
        new Choice(PanelEffect.Doors, "双门对开", 345, 370),
        new Choice(PanelEffect.Scatter, "图标列队", 380, 400),
        new Choice(PanelEffect.Wave, "波浪逐行", 385, 410),
        new Choice(PanelEffect.Relay, "三段接力", 405, 430),
        new Choice(PanelEffect.Curtain, "垂幕落定", 345, 375),
        new Choice(PanelEffect.Capsule, "胶囊生长", 380, 415),
        new Choice(PanelEffect.Rail, "滑轨推入", 360, 390),
        new Choice(PanelEffect.Split, "对向错层", 420, 440),
        new Choice(PanelEffect.Radial, "放射绽放", 395, 420),
        new Choice(PanelEffect.Aperture, "窗孔开合", 365, 395)
    });

    internal readonly record struct Pose(double X = 0, double Y = 0, double Scale = 1, double Opacity = 1,
        double InsetX = 0, double InsetTop = 0, double InsetBottom = 0);
    private sealed record Part(Rect Bounds, Geometry Mask, Point Pivot, Pose From, Pose To, double Delay, double End);
    private static readonly Pose Rest = new(0, 0, 1, 1);
    private readonly RenderTargetBitmap _image;
    private readonly Brush _background;
    private readonly Rect _area;
    private readonly Geometry _outline;
    private readonly List<Part> _parts = [];
    private readonly bool _opening;

    internal PanelEffect Preset { get; }
    internal int PartCount => _parts.Count;

    internal static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(PanelEffectFrame),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    internal double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    internal PanelEffectFrame(RenderTargetBitmap image, Size size, Brush background, double radius, PanelEffect effect,
        bool opening, Rect[] sections, Rect[] icons, Pose[]? from)
    {
        _image = image;
        _background = background;
        _area = new Rect(new Point(), size);
        _outline = Freeze(new RectangleGeometry(_area, radius, radius));
        Preset = effect;
        _opening = opening;
        Width = size.Width;
        Height = size.Height;
        IsHitTestVisible = false;
        ClipToBounds = true;

        if (effect is PanelEffect.Relay or PanelEffect.Split)
            BuildSections(sections);
        else if ((effect is PanelEffect.Scatter or PanelEffect.Wave or PanelEffect.Radial) && icons.Length > 0)
            BuildIcons(icons);
        else
            BuildPanel();

        if (from?.Length == _parts.Count)
        {
            for (int i = 0; i < _parts.Count; i++)
                _parts[i] = _parts[i] with { From = from[i], Delay = 0, End = 1 };
        }
    }

    private void Add(Rect bounds, Pose folded, double delay = 0, double end = 1, Point? pivot = null, Geometry? mask = null)
    {
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _parts.Add(new(bounds, mask ?? Freeze(new RectangleGeometry(bounds)), pivot ?? center,
            _opening ? folded : Rest, _opening ? Rest : folded, delay, end));
    }

    private void BuildPanel()
    {
        var folded = Preset switch
        {
            PanelEffect.Doors => Rest with { InsetX = .5, Opacity = 0 },
            PanelEffect.Curtain => Rest with { InsetBottom = 1, Y = _opening ? -8 : 0, Opacity = 0 },
            PanelEffect.Capsule => new Pose(Y: _opening ? 50 : 62, Scale: _opening ? .5 : .46, Opacity: 0),
            PanelEffect.Rail => Rest with { Y = _opening ? 110 : 128, Opacity = 0 },
            PanelEffect.Aperture => new Pose(Scale: .94, Opacity: 0,
                InsetTop: _opening ? .24 : .46, InsetBottom: _opening ? .24 : .46),
            PanelEffect.Wave => Rest with { X = _opening ? -26 : 30, Opacity = 0 },
            PanelEffect.Scatter => new Pose(Y: 30, Scale: .93, Opacity: 0),
            _ => new Pose(Scale: .86, Opacity: 0)
        };
        Point? pivot = Preset == PanelEffect.Capsule ? new Point(_area.Width / 2, _area.Bottom) : null;
        Add(_area, folded, pivot: pivot);
    }

    private void BuildSections(Rect[] sections)
    {
        for (int i = 0; i < sections.Length; i++)
        {
            var folded = Preset == PanelEffect.Relay
                ? Rest with { Y = _opening ? 24 : 30, Opacity = 0 }
                : i switch
                {
                    0 => Rest with { X = _opening ? -38 : -46, Opacity = 0 },
                    1 => Rest with { X = _opening ? 38 : 46, Opacity = 0 },
                    _ => Rest with { Y = _opening ? 28 : 34, Opacity = 0 }
                };
            int rank = _opening ? i : sections.Length - 1 - i;
            double delay = rank * .20;
            Add(sections[i], folded, delay, delay + .6);
        }
    }

    private void BuildIcons(Rect[] icons)
    {
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(_area));
        foreach (var icon in icons) mask.Children.Add(new RectangleGeometry(icon));
        Add(_area, Rest with { Opacity = 0 }, _opening ? 0 : .55, _opening ? .38 : 1, mask: Freeze(mask));
        var rows = icons.Select(icon => Math.Round(icon.Top)).Distinct().Order().ToArray();
        var left = icons.Min(icon => icon.Left);
        var right = icons.Max(icon => icon.Right);
        var top = icons.Min(icon => icon.Top);
        var bottom = icons.Max(icon => icon.Bottom);
        for (int i = 0; i < icons.Length; i++)
        {
            var rect = icons[i];
            int rank = Preset == PanelEffect.Wave ? Array.IndexOf(rows, Math.Round(rect.Top)) : i;
            int last = (Preset == PanelEffect.Wave ? rows.Length : icons.Length) - 1;
            if (!_opening) rank = last - rank;
            double delay = last > 0 ? .35 * rank / last : 0;
            var folded = Preset switch
            {
                PanelEffect.Wave => Rest with { X = _opening ? -28 : 32, Opacity = 0 },
                PanelEffect.Radial => new Pose(
                    X: 38 * (2 * (rect.X + rect.Width / 2 - left) / (right - left) - 1),
                    Y: 36 * (2 * (rect.Y + rect.Height / 2 - top) / (bottom - top) - 1),
                    Scale: .86, Opacity: 0),
                _ => new Pose(Y: _opening ? 28 : 34, Scale: .93, Opacity: 0)
            };
            Add(rect, folded, delay, delay + .65);
        }
    }

    internal Pose[] CapturePoses() => _parts.Select(ValueAt).ToArray();

    private Pose ValueAt(Part part)
    {
        double t = Math.Clamp((Progress - part.Delay) / (part.End - part.Delay), 0, 1);
        double ease = _opening ? 1 - (1 - t) * (1 - t)
            : t < .5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t);
        double fade = _opening ? ease : Math.Clamp((t - .38) / .62, 0, 1);
        double Mix(double a, double b) => a + (b - a) * ease;
        return new(Mix(part.From.X, part.To.X), Mix(part.From.Y, part.To.Y),
            Mix(part.From.Scale, part.To.Scale), part.From.Opacity + (part.To.Opacity - part.From.Opacity) * fade,
            Mix(part.From.InsetX, part.To.InsetX), Mix(part.From.InsetTop, part.To.InsetTop),
            Mix(part.From.InsetBottom, part.To.InsetBottom));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        for (int i = 0; i < _parts.Count; i++)
        {
            var part = _parts[i];
            var pose = ValueAt(part);
            if (pose.Opacity < .001) continue;
            var bounds = part.Bounds;
            var crop = new Rect(bounds.X + bounds.Width * pose.InsetX, bounds.Y + bounds.Height * pose.InsetTop,
                Math.Max(0, bounds.Width * (1 - 2 * pose.InsetX)),
                Math.Max(0, bounds.Height * (1 - pose.InsetTop - pose.InsetBottom)));
            if (crop.Width < .01 || crop.Height < .01) continue;
            drawing.PushOpacity(pose.Opacity);
            double remaining = 1 - pose.Scale;
            drawing.PushTransform(new MatrixTransform(pose.Scale, 0, 0, pose.Scale,
                pose.X + part.Pivot.X * remaining, pose.Y + part.Pivot.Y * remaining));
            if (part.Mask is GeometryGroup)
            {
                drawing.PushClip(_outline);
                drawing.DrawRectangle(_background, null, _area);
                drawing.Pop();
            }
            drawing.PushClip(crop == bounds ? part.Mask : new RectangleGeometry(crop));
            drawing.DrawImage(_image, _area);
            drawing.Pop();
            drawing.Pop();
            drawing.Pop();
        }
    }

    private static T Freeze<T>(T geometry) where T : Geometry
    {
        geometry.Freeze();
        return geometry;
    }
}
