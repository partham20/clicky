using System.Windows;
using Clicky.Core.Buddy;
using Clicky.Core.Geometry;

namespace Clicky.App.Buddy;

/// <summary>Art for the buddy. M1 ships the blue pointer; M14's Clickys can bring their own.</summary>
public interface IBuddySkin
{
    string Name { get; }

    /// <summary>A new visual; each overlay and the Home dock get their own. UI thread only.</summary>
    IBuddyVisual Create();
}

public interface IBuddyVisual
{
    FrameworkElement Element { get; }

    /// <summary>Layout size in DIPs, including room for state animations.</summary>
    Size Extent { get; }

    /// <summary>Where the element's top-left sits relative to the pointer hotspot, in DIPs.</summary>
    Vector Offset { get; }

    /// <summary>
    /// Setting a state starts its animation. Visuals that are off screen are kept Idle so their
    /// looping storyboards do not keep WPF rendering frames.
    /// </summary>
    BuddyState State { get; set; }

    /// <summary>Positions the element's top-left in its parent canvas, in DIPs.</summary>
    void MoveTo(DipPoint topLeft);
}
