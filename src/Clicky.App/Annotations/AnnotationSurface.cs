using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Clicky.Core.Actions;
using Clicky.Core.Annotations;
using Clicky.Core.Geometry;

namespace Clicky.App.Annotations;

/// <summary>
/// Draws the board's marks that fall on one monitor, in that monitor's DIPs. Immediate-mode
/// (OnRender) so a frame costs a few small geometries and no element churn; WPF keeps the last
/// frame on screen, so nothing runs between redraws.
/// </summary>
internal sealed class AnnotationSurface : FrameworkElement
{
    private const double InkThickness = 3.5;
    private const double HaloThickness = 7;
    private const double RingThickness = 2.5;
    private const double GlowThickness = 12;
    private const double GlyphScale = 1.3;
    private const double LabelFontSize = 14;
    private const double LabelMaxWidth = 280;
    private const double LabelPaddingX = 9;
    private const double LabelPaddingY = 4;
    private const double ScreenMargin = 6;

    // Room a label or the pointer glyph may take beyond a mark's ink, in DIPs; used to decide
    // which monitors a mark touches.
    private const double Reach = LabelMaxWidth + 40;

    // Same silhouette as the buddy (PointerBuddy.xaml) with the tip at (0,0).
    private static readonly Geometry Glyph = Freeze(Geometry.Parse("M0,0 L0,17 L4.6,12.8 L7.6,19.6 L10.4,18.4 L7.4,11.6 L13,11.6 Z"));
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe Print, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush LabelText = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen HaloPen = Freeze(RoundPen(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF), HaloThickness));
    private static readonly Pen RingHaloPen = Freeze(RoundPen(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF), RingThickness + 3));
    private static readonly Pen GlyphEdge = Freeze(RoundPen(Colors.White, 1.5 / GlyphScale));
    private static readonly Dictionary<DrawColor, Ink> Inks = Enum.GetValues<DrawColor>().ToDictionary(c => c, c => new Ink(ColorOf(c)));

    private readonly MonitorInfo _monitor;
    private readonly IReadOnlyList<MonitorInfo> _allMonitors;
    private readonly AnnotationBoard _board;
    private readonly SketchBounds _monitorBounds;
    private readonly Dictionary<long, FormattedText> _labels = [];

    /// <param name="allMonitors">Every overlay's monitor, to tell which one owns a label.</param>
    public AnnotationSurface(MonitorInfo monitor, IReadOnlyList<MonitorInfo> allMonitors, AnnotationBoard board)
    {
        _monitor = monitor;
        _allMonitors = allMonitors;
        _board = board;
        _monitorBounds = new SketchBounds(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Right, monitor.Bounds.Bottom);
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>True if the last render drew anything, so the next one must run to clear or update it.</summary>
    public bool HasInk { get; private set; }

    /// <summary>True if any mark (with its label and pointer flight) may fall on this monitor.</summary>
    public bool Touches(IReadOnlyList<AnnotationEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (Touches(entry))
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var drew = false;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var entry in _board.Entries)
        {
            var frame = entry.Frame;
            if (frame.Opacity <= 0 || !Touches(entry))
            {
                continue;
            }

            drew = true;
            drawingContext.PushOpacity(frame.Opacity);
            DrawMark(drawingContext, entry, frame, pixelsPerDip);
            drawingContext.Pop();
        }

        // Forget labels of marks that are gone.
        if (_labels.Count > 0)
        {
            foreach (var id in _labels.Keys.Where(id => !_board.Entries.Any(e => e.Id == id)).ToList())
            {
                _labels.Remove(id);
            }
        }

        HasInk = drew;
    }

    private void DrawMark(DrawingContext dc, AnnotationEntry entry, AnnotationFrame frame, double pixelsPerDip)
    {
        var annotation = entry.Annotation;
        var ink = Inks[annotation.Color];

        if (annotation.Fill is { } fill && frame.Draw > 0)
        {
            var topLeft = ToDip(new SketchPoint(fill.Left, fill.Top));
            var bottomRight = ToDip(new SketchPoint(fill.Right, fill.Bottom));
            dc.PushOpacity(frame.Draw);
            dc.DrawRoundedRectangle(ink.Tint, null, new Rect(topLeft, bottomRight), 6, 6);
            dc.Pop();
        }

        if (frame.Draw > 0 && annotation.Strokes.Count > 0)
        {
            var geometry = BuildGeometry(SketchStroke.Reveal(annotation.Strokes, frame.Draw));
            switch (annotation.Kind)
            {
                case AnnotationKind.Point:
                    dc.DrawGeometry(null, RingHaloPen, geometry);
                    dc.DrawGeometry(null, ink.Ring, geometry);
                    break;
                case AnnotationKind.Highlight:
                    dc.DrawGeometry(null, ink.Glow, geometry);
                    dc.DrawGeometry(null, HaloPen, geometry);
                    dc.DrawGeometry(null, ink.Pen, geometry);
                    break;
                default:
                    dc.DrawGeometry(null, HaloPen, geometry);
                    dc.DrawGeometry(null, ink.Pen, geometry);
                    break;
            }
        }

        if (entry.PointerPosition is { } tip)
        {
            var at = ToDip(tip);
            dc.PushTransform(new TranslateTransform(at.X, at.Y));
            dc.PushTransform(new ScaleTransform(GlyphScale, GlyphScale));
            dc.DrawGeometry(ink.Brush, GlyphEdge, Glyph);
            dc.Pop();
            dc.Pop();
        }

        if (annotation.Label is { } label && frame.LabelOpacity > 0)
        {
            DrawLabel(dc, entry, label, ink, frame.LabelOpacity, pixelsPerDip);
        }
    }

    private void DrawLabel(DrawingContext dc, AnnotationEntry entry, string label, Ink ink, double opacity, double pixelsPerDip)
    {
        if (!_labels.TryGetValue(entry.Id, out var text))
        {
            text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, LabelText, pixelsPerDip)
            {
                MaxTextWidth = LabelMaxWidth,
                MaxLineCount = 3,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            _labels[entry.Id] = text;
        }

        var size = new Size(text.WidthIncludingTrailingWhitespace + (2 * LabelPaddingX), text.Height + (2 * LabelPaddingY));
        var anchor = ToDip(entry.Annotation.LabelAnchor);
        var origin = entry.Annotation.LabelSide switch
        {
            LabelSide.Left => new Point(anchor.X - size.Width, anchor.Y - (size.Height / 2)),
            LabelSide.Above => new Point(anchor.X, anchor.Y - size.Height),
            LabelSide.Below => new Point(anchor.X, anchor.Y),
            _ => new Point(anchor.X, anchor.Y - (size.Height / 2)),
        };

        // Keep the bubble on screen, but only on the monitor nearest its anchor: clamping on a
        // neighbour too would draw a second copy at its edge.
        var anchorPixel = new PixelPoint((int)Math.Floor(entry.Annotation.LabelAnchor.X), (int)Math.Floor(entry.Annotation.LabelAnchor.Y));
        if (ReferenceEquals(CoordinateMapper.NearestMonitor(_allMonitors, anchorPixel), _monitor))
        {
            origin.X = Math.Clamp(origin.X, ScreenMargin, Math.Max(ScreenMargin, ActualWidth - size.Width - ScreenMargin));
            origin.Y = Math.Clamp(origin.Y, ScreenMargin, Math.Max(ScreenMargin, ActualHeight - size.Height - ScreenMargin));
        }

        dc.PushOpacity(opacity);
        dc.DrawRoundedRectangle(LabelBackground, ink.LabelBorder, new Rect(origin, size), 9, 9);
        dc.DrawText(text, new Point(origin.X + LabelPaddingX, origin.Y + LabelPaddingY));
        dc.Pop();
    }

    private bool Touches(AnnotationEntry entry)
    {
        var bounds = entry.Annotation.Bounds.Union(Dot(entry.Annotation.LabelAnchor));
        if (entry.FlyFrom is { } from)
        {
            bounds = bounds.Union(Dot(from));
        }

        return bounds.Inflate(Reach * entry.Annotation.Unit).Intersects(_monitorBounds);

        static SketchBounds Dot(SketchPoint p) => new(p.X, p.Y, p.X, p.Y);
    }

    private Point ToDip(SketchPoint p)
    {
        var dip = CoordinateMapper.ToMonitorDip(p.X, p.Y, _monitor);
        return new Point(dip.X, dip.Y);
    }

    private StreamGeometry BuildGeometry(IReadOnlyList<IReadOnlyList<SketchPoint>> strokes)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var stroke in strokes)
            {
                context.BeginFigure(ToDip(stroke[0]), isFilled: false, isClosed: false);
                if (stroke.Count == 1)
                {
                    // A dot: a zero-length segment still gets round caps.
                    context.LineTo(ToDip(stroke[0]), isStroked: true, isSmoothJoin: true);
                    continue;
                }

                var points = new List<Point>(stroke.Count - 1);
                for (var i = 1; i < stroke.Count; i++)
                {
                    points.Add(ToDip(stroke[i]));
                }

                context.PolyLineTo(points, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Color ColorOf(DrawColor color) => color switch
    {
        DrawColor.Red => Color.FromRgb(0xDC, 0x26, 0x26),
        DrawColor.Green => Color.FromRgb(0x16, 0xA3, 0x4A),
        DrawColor.Yellow => Color.FromRgb(0xEA, 0xB3, 0x08),
        DrawColor.Blue => Color.FromRgb(0x02, 0x84, 0xC7),

        // The buddy's blue (Assets/clicky.ico).
        _ => Color.FromRgb(0x25, 0x63, 0xEB),
    };

    private static Pen RoundPen(Color color, double thickness) => new(new SolidColorBrush(color), thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Frozen brushes and pens for one colour.</summary>
    private sealed class Ink(Color color)
    {
        public Brush Brush { get; } = Freeze(new SolidColorBrush(color));

        public Brush Tint { get; } = Freeze(new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B)));

        public Pen Pen { get; } = Freeze(RoundPen(color, InkThickness));

        public Pen Ring { get; } = Freeze(RoundPen(Color.FromArgb(0xD0, color.R, color.G, color.B), RingThickness));

        public Pen Glow { get; } = Freeze(RoundPen(Color.FromArgb(0x38, color.R, color.G, color.B), GlowThickness));

        public Pen LabelBorder { get; } = Freeze(RoundPen(Color.FromArgb(0x55, color.R, color.G, color.B), 1.2));
    }
}
