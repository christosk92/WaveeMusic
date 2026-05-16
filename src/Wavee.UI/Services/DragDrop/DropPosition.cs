namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Where the drop landed relative to the target row.
/// Maps from the WinUI sidebar's Top/Center/Bottom indicator.
/// </summary>
public enum DropPosition
{
    Before,
    After,
    Inside,
}
