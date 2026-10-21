namespace Clicky.App.Buddy;

/// <summary>The default buddy: a blue pointer that matches the tray icon.</summary>
internal sealed class PointerBuddySkin : IBuddySkin
{
    public string Name => "Pointer";

    public IBuddyVisual Create() => new PointerBuddy();
}
