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

    // Caption bubble's gap from the arrow tip, and its widest line.
    private const double CaptionOffset = 44;
    public const double CaptionMaxWidth = 300;

    private BuddyState _state = BuddyState.Idle;
    private float _level;
    private bool _captionOnLeft;

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
            ApplyLevel();
        }
    }

    public float Level
    {
        get => _level;
        set
        {
            if (_level != value)
            {
                _level = value;
                ApplyLevel();
            }
        }
    }

    public string? Caption
    {
        get => CaptionText.Text.Length == 0 ? null : CaptionText.Text;
        set
        {
            CaptionText.Text = value ?? string.Empty;
            CaptionBubble.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public bool CaptionOnLeft
    {
        get => _captionOnLeft;
        set
        {
            if (_captionOnLeft == value)
            {
                return;
            }

            _captionOnLeft = value;

            // Canvas.Right counts from the 64 DIP box's right edge, so this mirrors the bubble about the arrow.
            if (value)
            {
                CaptionBubble.ClearValue(Canvas.LeftProperty);
                Canvas.SetRight(CaptionBubble, CaptionOffset);
            }
            else
            {
                CaptionBubble.ClearValue(Canvas.RightProperty);
                Canvas.SetLeft(CaptionBubble, CaptionOffset);
            }
        }
    }

    public void MoveTo(DipPoint topLeft)
    {
        Translate.X = topLeft.X;
        Translate.Y = topLeft.Y;
    }

    private void ApplyState(bool useTransitions) =>
        VisualStateManager.GoToElementState(LayoutRoot, _state.ToString(), useTransitions);

    /// <summary>
    /// Listening: a solid disc behind the arrow that swells with the voice; the pulsing ring keeps
    /// going regardless. Speaking: the glow brightens and the arrow grows a little with the voice.
    /// </summary>
    private void ApplyLevel()
    {
        var listening = _state == BuddyState.Listening ? _level : 0f;
        LevelDisc.Opacity = listening < 0.05f ? 0 : 0.15 + (0.35 * listening);
        LevelScale.ScaleX = LevelScale.ScaleY = 0.6 + (0.6 * listening);

        var speaking = _state == BuddyState.Speaking;
        var voice = speaking ? _level : 0f;
        Glow.Opacity = speaking ? 0.3 + (0.6 * voice) : 0;
        ArrowScale.ScaleX = ArrowScale.ScaleY = 1 + (0.1 * voice);
    }
}
