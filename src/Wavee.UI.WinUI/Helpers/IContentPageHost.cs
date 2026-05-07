using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Contract a content page (Album / Playlist / Show / Episode) exposes to its
/// <see cref="ContentPageController"/>. Lets the controller drive the shared
/// crossfade state machine and warm-cache trigger without owning page-specific
/// XAML, navigation, or connected-animation logic.
/// </summary>
public interface IContentPageHost
{
    /// <summary>
    /// The named loading-skeleton root. May be null when <see cref="ShimmerLoadGate.IsLoaded"/>
    /// is false (x:Load has unrealised the subtree). The controller's call sites all tolerate
    /// null here.
    /// </summary>
    FrameworkElement? ShimmerContainer { get; }

    /// <summary>
    /// The named content root. Always realised — the page's constructor sets its composition
    /// opacity to 0 so the crossfade can fade it in.
    /// </summary>
    FrameworkElement ContentContainer { get; }

    /// <summary>
    /// Layer the crossfade animation runs on. AlbumPage / PlaylistPage use Composition;
    /// ShowPage / EpisodePage need Xaml because their shimmer subtree drives layout that
    /// composition-only opacity won't capture.
    /// </summary>
    FrameworkLayer CrossfadeLayer { get; }

    /// <summary>
    /// Per-page id used in <c>[xfade][album:0xCAFE]</c> diagnostic logs. Format is
    /// <c>"album:{tag}"</c> / <c>"playlist:{tag}"</c> / etc.
    /// </summary>
    string PageIdForLogging { get; }

    /// <summary>
    /// Mirror of <c>ViewModel.IsLoading</c>. The controller's warm-cache check reads this
    /// rather than coupling to a specific VM type.
    /// </summary>
    bool IsLoading { get; }

    /// <summary>
    /// True once the VM has enough data to show content (e.g. AlbumName / PlaylistName /
    /// ShowName / EpisodeTitle is non-empty). Guards <see cref="ContentPageController.TryShowContentNow"/>
    /// against firing before the VM has populated.
    /// </summary>
    bool HasContent { get; }
}
