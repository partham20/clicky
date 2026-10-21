using System.Windows;
using System.Windows.Controls;
using Clicky.Core.Buddy;
using Clicky.Core.Geometry;

namespace Clicky.App.Buddy;

public partial class PointerBuddy : UserControl, IBuddyVisual
{
    // Arrow tip inside the 64x64 box, and where the tip sits relative to the system pointer's
    // hotspot: right of and below the standard arrow so the two never overlap.
    private static readonly Point TipInBox = new(24, 24);
    private static readonly Vector TipFromHotspot = new(18, 14);

    private BuddyState _state = BuddyState.Idle;

    public PointerBuddy()
    {
        InitializeComponent();

        // Storyboard targets resolve only once the element is in a tree; re-apply when it gets there.
        Loaded += (_, _) => ApplyState(useTransitions: false);
    }

    public FrameworkElement Element => this;

    public Size Extent => new(Width, Height);

    public Vector Offset => TipFromHotspot - (Vector)TipInBox;

    public BuddyState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            ApplyState(useTransitions: true);
        }
    }

    public void MoveTo(DipPoint topLeft)
    {
        Translate.X = topLeft.X;
        Translate.Y = topLeft.Y;
    }

    private void ApplyState(bool useTransitions) =>
        VisualStateManager.GoToElementState(LayoutRoot, _state.ToString(), useTransitions);
}
