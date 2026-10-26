// ── Theming/Contracts/ViewKeys.ReleaseNotes.cs — the release-notes surfaces' published view keys (themes plan 6.0, T11c) ──────────────────────
//
// T11c publishes the three release-notes surfaces: the What's-new page (`Pages.WhatsNew`: the header, the hero, the highlight strip with its cards, the changelog
// sections with their rows, issue chips and avatars, the release rail, the since banner, the loading and empty states), the highlight viewer
// (`ReleaseNotes.Viewer`: the modal veil and plate, the band with its chrome, the pager and the text) and the after-update plate
// (`ReleaseNotes.AfterUpdatePlate`: the welcome, the highlight row and the footer).
//
// What stays core (`ReleaseNotes.PageHost`, a `[CoreHost]`, the overlay doors and `ReleaseNotesModel`): the load and its cancellation, the last-seen read before the
// mark-seen write, the issue-state republish, the "only the latest" and loaded state, the after-update gates, latch and load, the viewer's and the plate's overlay
// (anchor, dismiss behaviour, focus trap), the stepping rule (`HighlightPaging`), the highlight cap and visibility, and every verb (open a URL, the Store
// listing, a release, a deep link, the clipboard, the "don't show again" write). A theme draws what the props carry.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The What's-new page (route `whatsnew`, arg = the semver). Mounted by <c>ReleaseNotes.PageHost</c>. Props: <see cref="WhatsNewPageProps"/>.</summary>
    public static readonly ViewKey<WhatsNewPageProps> WhatsNewPage = new("Pages.WhatsNew");

    /// <summary>The modal one-highlight-at-a-time viewer. Mounted in the overlay <c>ReleaseNotes.OpenViewer</c> opens. Props: <see cref="HighlightViewerProps"/>.</summary>
    public static readonly ViewKey<HighlightViewerProps> ReleaseNotesViewer = new("ReleaseNotes.Viewer");

    /// <summary>The after-update plate (welcome, the highlight row, "Don't show again", "Full release notes", "Got it"). Mounted in the overlay
    /// <c>ReleaseNotes.OpenPlate</c> opens. Props: <see cref="AfterUpdatePlateProps"/>.</summary>
    public static readonly ViewKey<AfterUpdatePlateProps> ReleaseNotesAfterUpdatePlate = new("ReleaseNotes.AfterUpdatePlate");
}
