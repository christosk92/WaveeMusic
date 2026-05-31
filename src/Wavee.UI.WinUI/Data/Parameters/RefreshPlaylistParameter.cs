namespace Wavee.UI.WinUI.Data.Parameters;

/// <summary>
/// Navigation payload for <c>RefreshPlaylistPage</c>. Carries only the playlist identity — the page
/// loads the current tracks (and reconciles any saved session) fresh from cache on entry, so this is
/// a plain object passed through navigation (not JSON-persisted across app restart).
/// </summary>
public sealed record RefreshPlaylistParameter(string PlaylistId, string PlaylistName);
