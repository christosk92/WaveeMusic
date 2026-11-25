// ── Theming/Contracts/ViewKeys.LocalFiles.cs — the Local Files list's own views (#240 UX S4-S6) ───────────────────────────────────────
//
// Four keys, mounted by the core's Local Files arm of the playlist page (`Entities\LocalFiles.List.cs`). One picture in every theme through
// `CommonBlocks.Register` (Classic uses the WinUI layout); a structure that speaks in its own voice registers its own template (Zune's empty state).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Local Files list's body with no music folder and nothing listed (S4). Props: <see cref="LocalFilesEmptyProps"/>.</summary>
    public static readonly ViewKey<LocalFilesEmptyProps> LocalFilesEmpty = new("LocalFiles.Empty");

    /// <summary>The first scan's line and bar under the page head's facts (S4), in the head's page-supplied status slot. Props: <see cref="LocalFilesScanProps"/>.</summary>
    public static readonly ViewKey<LocalFilesScanProps> LocalFilesScan = new("LocalFiles.Scan");

    /// <summary>The "4 new songs · 1 moved" pill over the list (S5). Props: <see cref="LocalFilesPillProps"/>.</summary>
    public static readonly ViewKey<LocalFilesPillProps> LocalFilesPill = new("LocalFiles.Pill");

    /// <summary>The missing songs' review: the body of its dialog (S6). Props: <see cref="LocalFilesReviewProps"/>.</summary>
    public static readonly ViewKey<LocalFilesReviewProps> LocalFilesReview = new("LocalFiles.Review");

    /// <summary>"Find music on this PC" (#240, 0dm): the Music and Downloads folders with their counts, in the empty Local Files / On this PC state and
    /// Settings' empty Music folders group. Props: <see cref="LocalFindProps"/>.</summary>
    public static readonly ViewKey<LocalFindProps> LocalFilesFind = new("LocalFiles.Find");
}
