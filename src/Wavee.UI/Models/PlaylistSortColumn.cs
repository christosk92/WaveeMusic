namespace Wavee.UI.Models;

/// <summary>
/// Sort column options for playlist tracks. <see cref="Custom"/> preserves the
/// playlist's authored order (server-defined original index); the rest sort by
/// the corresponding track field.
/// </summary>
public enum PlaylistSortColumn { Custom, Title, Artist, Album, AddedAt }
