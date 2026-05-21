namespace Wavee.UI.WinUI.Controls.TabBar;

/// <summary>
/// Control-level contract for heavy GPU-resource holders — image surfaces,
/// Win2D swap chains / render targets, continuously-running shader animations —
/// that can shed those resources while their hosting page sits off-screen in
/// the <c>PageHost</c> cache, then re-hydrate when the page is shown again.
///
/// <para>
/// Distinct from <see cref="INavigationCacheMemoryParticipant"/>, which is
/// page-level (binding teardown, ViewModel hibernation). This contract is
/// control-level and is driven by a visual-tree walk (<see cref="NavCacheSurfaces"/>),
/// so a page never needs to know which of its descendants hold GPU resources.
/// </para>
///
/// <para>
/// Implementations MUST be idempotent: a second <see cref="ReleaseForNavCache"/>
/// with no intervening restore is a no-op that returns <c>false</c>, and
/// likewise <see cref="RestoreForNavCache"/> on a control that was never
/// released returns <c>false</c>. The walk relies on this so a page can be
/// re-classified (release → release, restore → restore) cheaply.
/// </para>
/// </summary>
public interface INavCacheSurfaceParticipant
{
    /// <summary>
    /// Sheds GPU resources. Returns <c>true</c> if it actually released,
    /// <c>false</c> if it was already in the released state.
    /// </summary>
    bool ReleaseForNavCache();

    /// <summary>
    /// Re-hydrates GPU resources shed by an earlier <see cref="ReleaseForNavCache"/>.
    /// Returns <c>true</c> if it actually restored, <c>false</c> if it was not
    /// currently released.
    /// </summary>
    bool RestoreForNavCache();

    /// <summary>
    /// Rough estimate of the native / GPU bytes this control is holding right
    /// now — 0 once released. Feeds the <c>[mem-attribution]</c> diagnostic
    /// only; accuracy is not load-bearing.
    /// </summary>
    long EstimatedSurfaceBytes { get; }
}
