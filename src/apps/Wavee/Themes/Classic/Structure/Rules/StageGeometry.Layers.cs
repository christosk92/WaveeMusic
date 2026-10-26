// ── Themes/Classic/Structure/Rules/StageGeometry.Layers.cs — the numbers of Classic's stage layout layers (themes plan 6.1, T7a2) ─
//
// What `Shell\Stage.UI.cs`, `Stage.Hero.UI.cs` and `Stage.Artist.UI.cs` spelled inline in the identity (the morphing cover and its titles),
// the chips, the now-playing card and the Artist layout's name, moved verbatim with the pictures. They are data, not decisions:
// `Views\Stage\ClassicStageIdentity.cs`, `ClassicStageLayers.cs`, `ClassicStageHero.cs` and `ClassicStageArtistPane.cs` read them
// (WVTH003 allows literals only here, in `Rules\**`).
//
// WHERE THE ALLOCATOR'S NUMBERS ARE. Which layout is drawn, the cover's size and corner of each look's rect, the titles' fonts and
// widths, the spectrum's strip and ring, the face's safe rect and the pane's rect are `Stage.Layout` (the allocator: they budget the
// face-safe rect, the caption and the pane from one set of extents) and `Stage.LayoutRules`; the views read them by name. What this
// file adds is the look of the parts inside those extents.

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's stage layout layers.</summary>
internal static class ClassicStageLayerGeometry
{
    /// <summary>The stage's display face (the titles, the artist's name, the section titles).</summary>
    public const string DisplayFace = "Segoe UI Variable Display";

    // ── the cover's corners per look ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cover's corner radius: Centered's, and the Artist hero's identity row without / with lyrics over the photo. Large art takes
    /// the card radius; Card keeps the Mode's (the thumb's control radius, the hero's card radius).</summary>
    public const float CenteredCorners = 18f, ArtistCorners = 8f, ArtistLyricsCorners = 6f;

    // ── the chips under the title ────────────────────────────────────────────────────────────────────────────────────

    public const float ChipHeight = 24f, ChipGap = 6f, ChipTextSize = 12f, ChipTextLine = 16f;

    // ── the now-playing card ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card drops in from this far above, after this delay.</summary>
    public const float CardEnterDy = -8f, CardEnterDelayMs = 150f;

    /// <summary>The card follows a tier crossing (the 460 card and the Compact window's identity card) on the cover's spring: response, damping.</summary>
    public const float CardMoveResponse = 0.45f, CardMoveDamping = 0.90f;

    // ── the face chip under the identity card (C-Stage-Compact: "Change visualizer: Bloom") ──────────────────────────────────

    /// <summary>The chip's side padding and gap, its glyph, its name (12/16 semibold) and its chevron. Its height and place are the
    /// allocator's (<c>Stage.Layout.FaceChipH</c>, <c>FaceChipY</c>): a 40 target.</summary>
    public const float FaceChipPadX = 12f, FaceChipGap = 6f, FaceChipGlyph = 14f, FaceChipText = 12f, FaceChipLine = 16f, FaceChipChevron = 10f;

    public const ushort FaceChipWeight = 600;

    // ── the Artist layout's name ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The "NOW PLAYING" eyebrow: size, line height, letter spacing.</summary>
    public const float EyebrowSize = 13f, EyebrowLine = 16f, EyebrowTracking = 2.34f;

    /// <summary>The huge name's letter spacing, as a fraction of its size.</summary>
    public const float NameTracking = -0.02f;
}
