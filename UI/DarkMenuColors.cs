namespace AttcksMergeTool.UI;

/// <summary>
/// The colours a <see cref="ToolStripProfessionalRenderer"/> paints a menu with, taken from
/// <see cref="Theme"/>.
/// </summary>
/// <remarks>
/// Setting the menu's own BackColor is not enough: the renderer draws the hover highlight, the
/// border and the separators from this table, and its defaults are a pale blue that leaves
/// white item text unreadable.
/// </remarks>
internal sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Toolbar;
    public override Color MenuBorder => Theme.SecondaryAction;
    public override Color MenuItemBorder => Theme.PrimaryAction;
    public override Color MenuItemSelected => Theme.PrimaryAction;
    public override Color SeparatorDark => Theme.SecondaryAction;
    public override Color SeparatorLight => Theme.Toolbar;

    // The strip down the left edge that holds item images. The menus here have none, but the
    // renderer still paints the margin unless it is switched off, so it matches the menu.
    public override Color ImageMarginGradientBegin => Theme.Toolbar;
    public override Color ImageMarginGradientMiddle => Theme.Toolbar;
    public override Color ImageMarginGradientEnd => Theme.Toolbar;
}
