namespace Clicky.Core.Geometry;

/// <summary>One display as the OS reports it to a per-monitor-DPI-aware process.</summary>
/// <param name="DeviceName">GDI device name (e.g. \\.\DISPLAY1). Stable while the topology is unchanged; not across reboots.</param>
/// <param name="Bounds">Full monitor rectangle in physical pixels.</param>
/// <param name="WorkArea">Bounds minus taskbar and app bars.</param>
/// <param name="Scale">Effective DPI / 96: 1.0 at 100%, 1.5 at 150%.</param>
/// <param name="IsPrimary">The monitor whose top-left is the virtual-desktop origin.</param>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary)
{
    public double Scale { get; } = double.IsFinite(Scale) && Scale > 0
        ? Scale
        : throw new ArgumentOutOfRangeException(nameof(Scale), Scale, "Monitor scale must be a positive number.");

    public override string ToString() =>
        $"{DeviceName} {Bounds.Width}x{Bounds.Height} at ({Bounds.Left},{Bounds.Top}) {Scale:P0}{(IsPrimary ? " primary" : string.Empty)}";
}
