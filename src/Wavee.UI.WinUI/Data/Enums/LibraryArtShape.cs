namespace Wavee.UI.WinUI.Data.Enums;

/// <summary>
/// Shape of the artwork rendered by the shared <c>LibraryDetailPanel</c> hero and
/// by library grid cards. One template handles both by flipping this flag instead
/// of forking per-entity panels: albums / shows are <see cref="Square"/>, artists
/// are <see cref="Circle"/>.
/// </summary>
public enum LibraryArtShape
{
    Square,
    Circle,
}
