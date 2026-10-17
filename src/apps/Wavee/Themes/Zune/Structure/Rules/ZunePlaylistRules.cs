// ── Themes/Zune/Structure/Rules/ZunePlaylistRules.cs — the numbers and records of Zune's playlist and Liked Songs views (themes plan 6.3; P5 Z6b1) ──
//
// Boards Z-Playlist-Owned-Followed (+ -Small, -Compact, -Huge), Z-Playlist-Followed (-Small, -Huge), Playlist-Page (the early study), Z-Liked
// (+ -Small, -Compact, -Huge), Liked (the early study) and Z-State-Empty-Playlist (+ -Compact).
//
// THE FRAME IS Z6a1's. A playlist and Liked Songs are detail pages: the panorama, the identity column, the vertical hero, the list's "songs"
// heading and the aside are `Themes\Zune\Structure\Views\Detail\*`. These rules cover only what a playlist hands the frame as SLOTS and what it
// mounts under its own keys (`Playlist.*`, `Liked.*`):
//
//   cover          the playlist's face (a mosaic of four, the art, or the neutral tile) at the Cover shape; the editable cover wears a strip along its
//                  foot ("change cover", 28 at Compact and 36 elsewhere, Z-Playlist-Owned-Followed) and a dashed "choose a cover" tile when empty
//                  (Z-State-Empty-Playlist). Flat: no saturation boost, no camera glyph, no hover-only affordance (a touch window has no hover).
//   editor         the title and the description are the editor (the board: "hover underlines them"): the read arm is the text with a hairline under
//                  it that rises on hover, the edit arm is the engine's field over two words, "save" in the accent and "cancel"; the status
//                  ("saving", "saved") stands BELOW the box so the measure never moves.
//   owner          the avatar and the name in the artist voice (a link), the collaborator pile where the playlist is collaborative, and the invite
//                  ring at the row's end; the row reserves the taller arm's height so flipping "collaborative" moves nothing.
//   access / tune  flat panels in the heading and label voices: words, rules and the engine's switch; the current tuning is marked, not disabled.
//   daylist        the countdown in the numeral voices (HH:MM:SS, each digit in a fixed cell so a tick never moves the strip), or the ring while rolling.
//   recommended    the section's heading and its "refresh" word over rows with a 40 cover and an outlined "add" pill. An EMPTY playlist leads the
//                  section with the board's sentence, a line and a field that finds songs to add (Z-State-Empty-Playlist), in a box that reserves
//                  its height so the rows under it never jump.
//   liked          the page has no cover on the wide arm (the head is the title and the meta); the vertical hero's cover and the Home / library
//                  tiles draw FLAT: the bundled stock cover, a flat mosaic of the newest likes, or the accent plate with the heart (no ambient
//                  loops). The chip rail is the engine's chips (Zune's style makes them flat rectangles): 32 tall, 40 at Compact.
//
// ONE RECORD PER CONTENT TIER (`ZunePlaylistTiers.Of`), the same discipline as the detail frame: a crossing re-renders and nothing is keyed on it.
// Public, so `Wavee.Tests` can pin the rules (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The numbers of Zune's playlist and Liked Songs views.</summary>
public static class ZunePlaylistRules
{
    // ── the node keys (the tests find them) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The editable cover, the cover's strip, the inline editor's read arm and edit arm, the owner row, the access panel, the tune panel.</summary>
    public const string CoverEditorKey = "zune.playlist.cover-editor", CoverStripKey = "zune.playlist.cover-strip", EditorReadKey = "zune.playlist.read",
        EditorEditKey = "zune.playlist.edit", OwnerKey = "zune.playlist.owner", AccessKey = "zune.playlist.access", TuneKey = "zune.playlist.tune";

    /// <summary>The recommended section, its heading row, its lead (an empty playlist's), and a row's prefix (<c>zune.playlist.rec:&lt;slot&gt;</c>).</summary>
    public const string RecsKey = "zune.playlist.recs", RecsHeadKey = "zune.playlist.recs-head", LeadKey = "zune.playlist.lead",
        RecRowPrefix = "zune.playlist.rec:";

    /// <summary>The Liked cover, its picker's strip, the chip rail.</summary>
    public const string LikedCoverKey = "zune.liked.cover", LikedPickerKey = "zune.liked.picker", LikedChipsKey = "zune.liked.chips";

    // ── the cover ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The decode bucket of a cover face handed no bucket (the face carries its own; the shelf's 512 is the floor of a warm texture).</summary>
    public const int CoverDecodePx = 512;

    /// <summary>The strip along the editable cover's foot (36; 28 at Compact), its word's size, the dashed tile's stroke and dash, its plus glyph and gap.</summary>
    public const float StripWordSize = 13f, EmptyDash = 4f, EmptyStroke = 1f, EmptyGlyph = 24f, EmptyGap = 6f, EmptyWordSize = 13f;

    /// <summary>A saving cover: the strip's status ring.</summary>
    public const float StripRing = 14f;

    // ── the editor ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The read arm's hairline (1) and its resting opacity; the hover raises it to full. The pencil's glyph and box.</summary>
    public const float UnderlineH = 1f, UnderlineRest = 0.35f, PencilGlyph = 14f, PencilBox = 24f, UnderlineGap = 4f;

    /// <summary>The title field's extra height over its font size, the description field's height, and the description's size and clamp (13/20).</summary>
    public const float TitleFieldExtra = 16f, DescriptionFieldH = 72f, DescriptionSize = 13f;

    /// <summary>The description editor's clamp in its read arm.</summary>
    public const int DescriptionLines = 3;

    /// <summary>The status row: the ring and the word 6 apart, the air under the box, the edit words' gap and padding (a word is a flat target).</summary>
    public const float StatusGap = 6f, StatusRowGap = 4f, EditWordsGap = 16f, EditWordPad = 8f;

    // ── the owner ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The owner's avatar, the gap beside it, the pile's chevron and its gap to the faces.</summary>
    public const float OwnerAvatar = 24f, OwnerGap = 8f, PileChevron = 12f, PileGap = 6f;

    // ── the access panel and the tune panel ──────────────────────────────────────────────────────────────────────────

    /// <summary>The panel's padding and the gap between its blocks; the hint's clamp; the call to action's height and its glyph; the rule's inset.</summary>
    public const float PanelPad = 20f, PanelGap = 14f, CtaH = 40f, CtaGlyph = 14f, RuleGap = 2f;

    /// <summary>The hint's lines.</summary>
    public const int HintLines = 3;

    /// <summary>A switch row's label gap, and the tune panel's row (44), its pad, its current mark (a 2-dip bar) and the inset under the header.</summary>
    public const float SwitchLabelGap = 2f, TuneRowH = 44f, TuneRowPad = 16f, TuneMark = 2f, TuneHeaderPad = 16f, TuneWidthFallback = 320f;

    // ── the daylist ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strip's reserved heights (the compact cells in the rail; the hero's), a digit's cell width, a colon's, and the ring beside the caption.</summary>
    public const float DaylistCompactH = 28f, DaylistHeroH = 40f, DigitCompactW = 16f, DigitHeroW = 24f, ColonCompactW = 8f, ColonHeroW = 12f, DaylistGap = 8f;

    // ── the recommended section ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The section's air over its heading (the album's trailing sections: 32), its heading row (40), the heading's inset (the rows' 8) and the gap to the first row.</summary>
    public const float RecsTop = 32f, RecsHeadH = 40f, RecsInset = 8f, RecsGap = 6f;

    /// <summary>A recommendation's cover (40), the "add" pill (28 high, 2-dip outline), its side padding, its word, the duration lane and the spinner box.</summary>
    public const float RecArt = 40f, PillH = 28f, PillStroke = 2f, PillPadX = 12f, PillWord = 13f, DurationW = 48f, RecTrailGap = 8f;

    /// <summary>The "no suggestions" note's size.</summary>
    public const float NoteSize = 13f;

    // ── the empty playlist's lead (Z-State-Empty-Playlist) ───────────────────────────────────────────────────────────

    /// <summary>The air over the lead (40; 24 at Compact is the record's), the gap under its title (4), the gap before its field (18), and the field's width at the
    /// wide arms (560).</summary>
    public const float LeadLineGap = 4f, LeadFieldGap = 18f, LeadFieldW = 560f, LeadBottom = 8f;

    // ── the Liked cover ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tone plate's heart as a fraction of the cover, the picker strip's height and word, the flyout's width, its row and its pad.</summary>
    public const float HeartFraction = 0.4f, PickerStripH = 32f, PickerWord = 13f, FlyoutW = 280f, FlyoutRowH = 44f, FlyoutPad = 8f, FlyoutNote = 12f, FlyoutNoteLine = 18f;

    /// <summary>A look's miniature in the flyout (32), its gap to the word, and the opacity of a look the library cannot feed yet (it stays selectable, as the
    /// engine's picker has it: an unfed choice lights up by itself when the library reaches its floor).</summary>
    public const float MiniEdge = 32f, MiniGap = 12f, UnfedOpacity = 0.45f;

    /// <summary>The cover is the stock PNG below this many distinct likes for a mosaic, and the tone plate needs none (the host's effective style has applied the floor).</summary>
    public const int MosaicFloor = LikedCoverRules.MosaicCells;

    /// <summary>The chip rail's edge fade where the chips ride inline (24, the lists' fade) and a chip's side padding (12).</summary>
    public const float InlineChipFade = 24f, ChipPadX = 12f;

    /// <summary>Zune draws three Liked cover looks (the engine's picker offers nine, and nine ambient loops are not flat): these are their indices in
    /// <c>LikedCoverRules.PickerOrder</c> (Mosaic, Tone, Stock). A persisted look Zune does not draw reads as the mosaic.</summary>
    public static readonly LikedCoverStyle[] ZuneLooks = [LikedCoverStyle.Mosaic, LikedCoverStyle.Tone, LikedCoverStyle.Stock];

    /// <summary>The look Zune draws for a persisted <paramref name="style"/>: Stock stays Stock, Tone stays Tone, anything else is the mosaic.</summary>
    public static LikedCoverStyle LookOf(LikedCoverStyle style) => style switch
    {
        LikedCoverStyle.Stock => LikedCoverStyle.Stock,
        LikedCoverStyle.Tone => LikedCoverStyle.Tone,
        _ => LikedCoverStyle.Mosaic,
    };

    /// <summary>The index in <c>LikedCoverRules.PickerOrder</c> of a Zune look (the verb the flyout's row runs).</summary>
    public static int PickerIndexOf(LikedCoverStyle look) => Array.IndexOf(LikedCoverRules.PickerOrder, look);
}

/// <summary>What Zune's playlist and Liked views lay out in one content tier.</summary>
public sealed record ZunePlaylistLayout : ITierLayoutRecord
{
    /// <summary>The content tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>A flat command's hit edge: the chip, a word link, the edit words (40 at Compact and Huge 4K).</summary>
    public required float Target { get; init; }

    /// <summary>The cover strip's height (28 at Compact, 36 elsewhere).</summary>
    public required float CoverStrip { get; init; }

    /// <summary>The empty lead's title is the heading voice (Compact) or the display step (40/48).</summary>
    public required bool LeadLarge { get; init; }

    /// <summary>The lead's reserved height (it grows with a wrapping message, never shrinks under this) and its air above.</summary>
    public required float LeadMinHeight { get; init; }

    /// <summary>See <see cref="LeadMinHeight"/>.</summary>
    public required float LeadTop { get; init; }

    /// <summary>The lead's field: full width with the pill's corner at Compact, else the board's measure with a square corner; its height.</summary>
    public required bool LeadFieldFull { get; init; }

    /// <summary>See <see cref="LeadFieldFull"/>.</summary>
    public required float LeadFieldH { get; init; }

    /// <summary>See <see cref="LeadFieldFull"/>.</summary>
    public required float LeadFieldRadius { get; init; }

    /// <summary>The duration lane of a recommendation (Compact draws none: the row is the cover, the words and the pill).</summary>
    public required bool RecDuration { get; init; }

    /// <summary>The detail record the playlist's pieces read (the rings' size, the artist voice, the label size): Compact reads the vertical hero's.</summary>
    public required ZuneDetailLayout Detail { get; init; }

    /// <summary>None: every region fits at every size its tier lays out (the list yields, the lead grows).</summary>
    public ImmutableArray<TierRegion> Regions => ImmutableArray<TierRegion>.Empty;
}

/// <summary>The playlist and Liked records per content tier.</summary>
public static class ZunePlaylistTiers
{
    /// <summary>Compact (Z-Playlist-Owned-Followed-Compact, Z-State-Empty-Playlist-Compact): 40 targets, the 28 strip, the 26/32 lead and a pill field.</summary>
    public static ZunePlaylistLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact, Target = 40f, CoverStrip = 28f, LeadLarge = false, LeadMinHeight = 148f, LeadTop = 24f,
        LeadFieldFull = true, LeadFieldH = 44f, LeadFieldRadius = 22f, RecDuration = false, Detail = ZuneDetailTiers.Hero,
    };

    /// <summary>Small (Z-Playlist-Owned-Followed-Small): the 160 column, the 36 strip.</summary>
    public static ZunePlaylistLayout Small { get; } = new()
    {
        Tier = WidthTier.Small, Target = 32f, CoverStrip = 36f, LeadLarge = true, LeadMinHeight = 188f, LeadTop = 32f,
        LeadFieldFull = false, LeadFieldH = 40f, LeadFieldRadius = 0f, RecDuration = true, Detail = ZuneDetailRules.Small,
    };

    /// <summary>Standard (Z-Playlist-Owned-Followed, Z-State-Empty-Playlist): the 40/48 lead over a 560 field.</summary>
    public static ZunePlaylistLayout Standard { get; } = Small with { Tier = WidthTier.Standard, LeadMinHeight = 200f, LeadTop = 40f, Detail = ZuneDetailRules.Standard };

    /// <summary>Huge (Z-Playlist-Owned-Followed-Huge): Standard's, in the Huge detail record.</summary>
    public static ZunePlaylistLayout Huge { get; } = Standard with { Tier = WidthTier.Huge, Detail = ZuneDetailRules.Huge };

    /// <summary>Huge 4K (decision 2 caps pages at 2400): Huge's with the 40 targets.</summary>
    public static ZunePlaylistLayout Huge4K { get; } = Huge with { Tier = WidthTier.Huge4K, Target = 40f, Detail = ZuneDetailRules.Huge4K };

    /// <summary>The record of content tier <paramref name="tier"/>.</summary>
    public static ZunePlaylistLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        WidthTier.Huge => Huge,
        _ => Huge4K,
    };

    /// <summary>The record the playlist and Liked views read for <paramref name="facts"/> (Standard's where no frame resolved the tiers: a view mounted alone).</summary>
    public static ZunePlaylistLayout Of(in TierFacts facts) => facts.Policy is null ? Standard : For(facts.Content);
}
