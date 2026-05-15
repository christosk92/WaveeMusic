using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Wavee.UI.WinUI.Helpers.Playback;

/// <summary>
/// Coordinates the visual morph between the floating
/// <c>MiniVideoPlayer</c> and the fullscreen <c>VideoPlayerPage</c>. Uses
/// <see cref="ConnectedAnimationService"/> to capture the source element's
/// bounds at navigation time and replay an interpolated scale + translate
/// on the destination element once it realizes.
///
/// Both directions are supported with named keys:
/// <list type="bullet">
///   <item><c>MiniToFullVideo</c> — Mini's video surface → Full page's surface.</item>
///   <item><c>FullToMiniVideo</c> — Full page's surface → Mini's surface.</item>
/// </list>
/// </summary>
public static class VideoSurfaceMorph
{
    public const string MiniToFullKey = "MiniToFullVideo";
    public const string FullToMiniKey = "FullToMiniVideo";
    public const string MiniToGripperKey = "MiniToGripperVideo";
    public const string GripperToMiniKey = "GripperToMiniVideo";

    /// <summary>
    /// Capture <paramref name="source"/> as the Mini → Full animation source.
    /// Call BEFORE navigating to the fullscreen page.
    /// </summary>
    public static void PrepareMiniToFull(FrameworkElement source)
        => Prepare(MiniToFullKey, source);

    /// <summary>
    /// Replay the Mini → Full animation onto <paramref name="target"/>.
    /// Call from the destination page's <c>Loaded</c> handler (NOT
    /// <c>OnNavigatedTo</c> — the target element must be in the visual tree).
    /// </summary>
    public static void TryStartMiniToFull(FrameworkElement target)
        => TryStart(MiniToFullKey, target);

    /// <summary>
    /// Capture <paramref name="source"/> as the Full → Mini animation source.
    /// Call BEFORE the fullscreen page navigates back.
    /// </summary>
    public static void PrepareFullToMini(FrameworkElement source)
        => Prepare(FullToMiniKey, source);

    /// <summary>
    /// Replay the Full → Mini animation onto <paramref name="target"/>.
    /// Call from Mini's Loaded handler (or when its visibility flips back on).
    /// </summary>
    public static void TryStartFullToMini(FrameworkElement target)
        => TryStart(FullToMiniKey, target);

    /// <summary>
    /// Capture <paramref name="source"/> as the Mini → Gripper animation
    /// source. Call BEFORE the user clicks X on Mini (the floating player
    /// collapses to the right-edge tab).
    /// </summary>
    public static void PrepareMiniToGripper(FrameworkElement source)
        => Prepare(MiniToGripperKey, source);

    /// <summary>
    /// Replay the Mini → Gripper animation onto <paramref name="target"/>.
    /// Call from the gripper's Loaded handler or its visibility change.
    /// </summary>
    public static void TryStartMiniToGripper(FrameworkElement target)
        => TryStart(MiniToGripperKey, target);

    /// <summary>
    /// Capture <paramref name="source"/> as the Gripper → Mini animation
    /// source. Call BEFORE the user clicks the gripper (expands back to
    /// floating Mini).
    /// </summary>
    public static void PrepareGripperToMini(FrameworkElement source)
        => Prepare(GripperToMiniKey, source);

    /// <summary>
    /// Replay the Gripper → Mini animation onto <paramref name="target"/>.
    /// Call from Mini's Loaded handler / visibility change.
    /// </summary>
    public static void TryStartGripperToMini(FrameworkElement target)
        => TryStart(GripperToMiniKey, target);

    private static void Prepare(string key, FrameworkElement source)
    {
        if (source is null) return;
        try
        {
            var animation = ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate(key, source);
            // Slight scale animation so the morph reads as the video "growing"
            // into / shrinking out of the page chrome — Fluent's default
            // configuration here yields a natural, slightly bouncy feel.
            animation.Configuration = new DirectConnectedAnimationConfiguration();
            animation.IsScaleAnimationEnabled = true;
        }
        catch
        {
            // ConnectedAnimationService can throw if no XamlRoot is associated
            // (e.g. control hasn't been Loaded yet). Safe to swallow — the
            // morph is non-essential.
        }
    }

    private static void TryStart(string key, FrameworkElement target)
    {
        if (target is null) return;
        try
        {
            var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation(key);
            animation?.TryStart(target);
        }
        catch
        {
            // No active animation or target not yet measured. Non-fatal.
        }
    }
}
