// ── Theming/Contracts/ViewKeys.Liked.cs — the Liked Songs page's published view keys (themes plan 6.0, T9c) ──────────────────────
//
// T9c moves the pictures of the Liked Songs page into the structure: the generated cover (nine treatments, their palette-graded
// grounds and the ambient loops), the "Cover style" picker over it with its flyout of live miniatures, and the chip rail above the
// list. The page itself (`User.LikedPage`) keeps what DECIDES: the scope epoch, the demand rules, the identity, the chip memo, the
// table profiles and the verbs; the cover's hosts keep the snapshot gate (`LikedSnapshot`: style, tiles, count), the demand and the
// persisted style, and mount these keys with the values they read.
//
// What stays core: `LikedCoverRules` (styles, tile rule, site ladder, picker order, hue order), the snapshot and its equality gate,
// `User.LikedToneAnchorUrl` (the page tone follows the newest tile), the chip set, the lens header and the facts panel (shared with
// the playlist page: `User.Facts.UI.cs`), the empty state (`Controls.Vacancy`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The generated Liked Songs cover: the site ladder (stock PNG, flat mosaic, tone miniature or a full treatment), the nine
    /// treatments, the palette-graded leaves and the Wall / Marquee ambient loops. Mounted by the core cover host
    /// (<c>User.LikedCover</c>). Props: <see cref="LikedCoverProps"/>.</summary>
    public static readonly ViewKey<LikedCoverProps> LikedCover = new("Liked.Cover");

    /// <summary>The cover PLUS its "Cover style" affordance: the pill revealed on hover or focus and the flyout it opens. Mounted by
    /// <c>User.LikedCover</c> when the slot carries a picker. Props: <see cref="LikedCoverPickerProps"/>.</summary>
    public static readonly ViewKey<LikedCoverPickerProps> LikedCoverPicker = new("Liked.CoverPicker");

    /// <summary>The picker flyout's body: the eyebrow, the radio grid of live miniatures and the note. Mounted by the core flyout host
    /// that the picker opens. Props: <see cref="LikedCoverStyleFlyoutProps"/>.</summary>
    public static readonly ViewKey<LikedCoverStyleFlyoutProps> LikedCoverStyleFlyout = new("Liked.CoverStyleFlyout");

    /// <summary>The chip rail above the Liked list: "All" first, then the curated and derived chips (an un-evidenced chip disabled).
    /// Mounted by the core chip bar host. Props: <see cref="LikedChipBarProps"/>.</summary>
    public static readonly ViewKey<LikedChipBarProps> LikedChipBar = new("Liked.ChipBar");
}
