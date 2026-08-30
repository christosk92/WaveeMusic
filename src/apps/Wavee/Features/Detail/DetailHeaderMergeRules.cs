using Wavee.Core;

namespace Wavee;

/// <summary>
/// The PURE rule behind DetailPage's initial-load merge (cause 1 of the stale-daylist-header defect — see
/// docs/plans/wavee/architecture.md's hydration facade + the OpenPolicy/LibrarySync/XmCatalogFetch fixes for causes
/// 2-5). Engine-free by construction so it is pinned by <c>DetailHeaderMergeRulesTests</c> directly rather than
/// through a Component harness.
///
/// <para>The problem this answers: a container whose composed store row has not been revalidated THIS open (causes
/// 2-5 make that revalidation eventually land, but it is asynchronous) paints instantly from whatever the LAST
/// revalidation wrote. For a ROLLING-IDENTITY container (a daylist and its future siblings) that can be the PREVIOUS
/// edition, while the nav preview the page opened with is independently fresh — for a daylist it is Home's own
/// Pathfinder read, a wholly different pipeline from this store composition. A non-rolling container has no such
/// guarantee (its preview is just as likely to be the stale side — e.g. a rename made from this very page, then
/// re-opened later through a stale Home/sidebar card), so the preference only applies when rolling.</para>
/// </summary>
public static class DetailHeaderMergeRules
{
    /// <summary>Does this container's identity roll over on its own clock? Read off the SAME field both the loaded
    /// model and the nav preview carry (<c>DetailModel.ExpiresAtMs</c> / <c>DetailPreview.FromPlaylist</c>) — either
    /// one being positive is enough, so a preview that arrived before the full load populated its own window still
    /// counts.</summary>
    public static bool IsRollingIdentity(long loadedExpiresAtMs, long previewExpiresAtMs)
        => loadedExpiresAtMs > 0 || previewExpiresAtMs > 0;

    /// <summary>The title to publish for this load: the preview's, when the container is rolling and the preview
    /// actually carries one; the loaded (store-composed) title otherwise.</summary>
    public static string ResolveTitle(bool rollingIdentity, string loadedTitle, string? previewTitle)
        => rollingIdentity && !string.IsNullOrEmpty(previewTitle) ? previewTitle : loadedTitle;

    /// <summary>The cover <c>ImageSource.PreferVisible</c> should treat as "incoming": the preview's, when rolling and
    /// the preview has one. Without this a rolled-over container's stale store cover reads as DIFFERENT art from the
    /// correct preview cover and would win PreferVisible's own "different art ⇒ take incoming" branch — exactly
    /// backwards for this case. The loaded cover otherwise.</summary>
    public static Image? ResolveIncomingCover(bool rollingIdentity, Image? loadedCover, Image? previewCover)
        => rollingIdentity && previewCover is not null ? previewCover : loadedCover;
}
