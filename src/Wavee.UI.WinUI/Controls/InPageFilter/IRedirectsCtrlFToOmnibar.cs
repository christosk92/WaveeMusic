namespace Wavee.UI.WinUI.Controls.InPageFilter;

/// <summary>
/// Marker interface for pages where Ctrl+F should NOT open the in-page
/// filter overlay but instead focus the global Omnibar in the toolbar.
/// Used by pages that don't have a single obvious "primary list" to
/// filter (shelf-based pages, multi-collection detail pages) where the
/// most useful Ctrl+F semantic is "let me type a search query that
/// targets the Spotify catalog" rather than "narrow the current view".
/// </summary>
public interface IRedirectsCtrlFToOmnibar
{
}
