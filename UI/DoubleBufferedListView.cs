namespace AttcksMergeTool.UI;

/// <summary>
/// A <see cref="ListView"/> that paints itself off-screen before it paints itself on screen.
/// </summary>
/// <remarks>
/// The video list draws its own rows, and moving the mouse across it repaints a whole row at a
/// time - background first, then the text back over it. Undoubled, that shows as a flicker
/// following the pointer down the list. <see cref="Control.DoubleBuffered"/> is protected, so
/// switching it on means deriving; there is nothing to set from outside.
/// </remarks>
internal sealed class DoubleBufferedListView : ListView
{
    public DoubleBufferedListView() => DoubleBuffered = true;
}
