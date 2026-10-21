using System.Windows;
using System.Windows.Media;
using Clicky.Core.Geometry;
using Clicky.Core.Ink;

namespace Clicky.App.Ink;

/// <summary>A stretch of trail drawn at one opacity, in physical pixels on the virtual desktop.</summary>
internal sealed record InkChunk(Point[] Points, double Opacity, Rect Bounds);

/// <summary>
/// Draws the part of the live trail that falls on one monitor, in that monitor's DIPs, in the M7
/// marker style (white halo under a round pen) but in the user's own colour. Immediate-mode, like
/// the annotation surface: a frame is a few frozen geometries and nothing runs between redraws.
/// </summary>
internal sealed class InkTrailSurface : FrameworkElement
{
    private const double InkThickness = 3;
    private const double HaloThickness = 6.5;

    private static readonly Pen InkPen = Freeze(RoundPen(Color.FromRgb(InkPainter.InkRed, InkPainter.InkGreen, InkPainter.InkBlue), InkThickness));
    private static readonly Pen HaloPen = Freeze(RoundPen(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF), HaloThickness));

    private readonly MonitorInfo _monitor;
    private readonly Rect _monitorBounds;
    private IReadOnlyList<InkChunk> _chunks = [];

    public InkTrailSurface(MonitorInfo monitor)
    {
        _monitor = monitor;

        // Generous: the pen reaches a few pixels past the points, at up to 3x scale.
        _monitorBounds = new Rect(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height);
        _monitorBounds.Inflate(HaloThickness * 3, HaloThickness * 3);
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>True if the last render drew anything.</summary>
    public bool HasInk { get; private set; }

    /// <summary>Takes the frame's chunks and redraws if this monitor shows any of them (or showed some last time).</summary>
    public void Show(IReadOnlyList<InkChunk> chunks)
    {
        _chunks = chunks;
        if (HasInk || chunks.Any(c => c.Bounds.IntersectsWith(_monitorBounds)))
        {
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var visible = new List<(StreamGeometry Geometry, double Opacity)>();
        foreach (var chunk in _chunks)
        {
            if (chunk.Opacity > 0 && chunk.Points.Length > 0 && chunk.Bounds.IntersectsWith(_monitorBounds))
            {
                visible.Add((BuildGeometry(chunk.Points), chunk.Opacity));
            }
        }

        // All halos first, so a halo never cuts across ink where the trail crosses itself or two chunks meet.
        foreach (var (geometry, opacity) in visible)
        {
            drawingContext.PushOpacity(opacity);
            drawingContext.DrawGeometry(null, HaloPen, geometry);
            drawingContext.Pop();
        }

        foreach (var (geometry, opacity) in visible)
        {
            drawingContext.PushOpacity(opacity);
            drawingContext.DrawGeometry(null, InkPen, geometry);
            drawingContext.Pop();
        }

        HasInk = visible.Count > 0;
    }

    private StreamGeometry BuildGeometry(Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToDip(points[0]), isFilled: false, isClosed: false);
            if (points.Length == 1)
            {
                context.LineTo(ToDip(points[0]), isStroked: true, isSmoothJoin: true);
            }
            else
            {
                var rest = new List<Point>(points.Length - 1);
                for (var i = 1; i < points.Length; i++)
                {
                    rest.Add(ToDip(points[i]));
                }

                context.PolyLineTo(rest, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private Point ToDip(Point physical)
    {
        var dip = CoordinateMapper.ToMonitorDip(physical.X, physical.Y, _monitor);
        return new Point(dip.X, dip.Y);
    }

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
}
