// ── Themes/WinUi/Structure/Rules/Tiers.Stage.cs — the fullscreen stage's tier records under WinUI (P6 W4b) ─────────────────────────────
//
// Read off the stage boards at every window tier (W-Stage, W-Lyrics: C 400 x 860, S 800 x 600, Std 1440 x 960, H 2560 x 1440, 4K 3840 x 2160;
// WinUI has no Huge 4K edge, so 4K reads Huge's record, P6 D3):
//
//   Compact   (W-Stage-Compact, W-Lyrics-Compact) Card only, in a SHEET: the r8 top sheet with its grabber, a 40 header (the exit chevron, the
//             glyph-only mode pill, "..."), the identity row (the 56 cover, the title, the heart), the column's lyrics, and the stacked transport at
//             the foot (the seek with its two times under it, the 96 row: shuffle · previous · the 64 disc · next · repeat, the 40 row: the device
//             button, "from ...", "..."). Every target is at least 40.
//   Small     (W-Stage-Small, W-Lyrics-Small) Card or Large art: the 48 bar (the wordmark, the pill, the layout glyph and exit), the 104 card at
//             16 (8 / 12 padding, the 400 centre column: the 48 disc over the seek; the heart and "Synced lyrics" left; the volume, device and
//             "..." glyphs right); the Card layout's 340 now-playing card at 16, 60 (the 96 cover); the lyrics at 22.
//   Standard  (W-Stage, W-Lyrics) the 48 bar (the wordmark and "Now playing · full screen", the pill, Romanization, options, exit), the 112
//             card at 24 (12 / 16, the 560 centre column; the IDENTITY in the card's left column: the 80 cover, the 20 title, the artists,
//             "Playing from", the chips; 56 and 14 outside Visualizer mode; heart · mute · the 96 volume · device · "..." right); no
//             now-playing card in the Card layout and no titles under the Large art cover (the card carries them); the lyrics at 28.
//   Huge      (W-Stage-Huge, W-Lyrics-Huge, W-Stage-Huge4K) the 56 bar (the face and layout words beside options), the 128 card at 32 (16 / 24,
//             the 720 centre column, the 56 disc, 44 targets; the heart, "Playing from" and the chips left; mute · the 128 volume · device ·
//             "..." right); the 420 now-playing card at 32, 72; the Up next column beside the lyrics; the lyrics at 36, grown on a tall stage.
//
// Every tier opens the gallery only on request (W-Tiers: the gallery lives behind the options; never open on a first entry) and keeps the bar
// and the card visible at rest (the views never gate them on the idle chrome). Public, so `Wavee.Tests` pins them. Literal numbers are allowed
// here (WVTH003 / WVTH013 exempt `Rules\**`).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's stage policy (the metrics the core allocator budgets from) and the stage views' per-window-tier record.</summary>
public static class WinUiStageTiers
{
    // ── the metrics the core allocator budgets from ──────────────────────────────────────────────────────────────────────

    /// <summary>Compact (W-Stage-Compact): the stacked transport (the seek and its times, the 96 row, the 40 row) flush in the sheet, the
    /// identity row under the header, the gallery on request; Card only (W-Tiers: "Compact forces Card").</summary>
    public static StageTierMetrics CompactMetrics { get; } = new(TransportH: 196f, TransportInset: 0f, LyricsTypeMin: 20f, LyricsTypeMax: 20f)
    {
        IdentityCard = true, GalleryOnDemand = true, Layouts = StageLayouts.Card,
    };

    /// <summary>Video mode's Theatre panel beside the picture (the Media Player app's play-queue pane: 360 at the 1440 stage) and the lyrics' type in it,
    /// per window tier: Small 320 at 20, Standard 360 at 22, Huge 440 at 26 (the stage's own ramp a step down, the panel's measure). A Compact window
    /// has no Theatre (the picture keeps the whole stage). Declared before the records that read them: static initializers run in textual order.</summary>
    public const float SmallTheatrePaneW = 320f, StandardTheatrePaneW = 360f, HugeTheatrePaneW = 440f;

    public const float SmallTheatreLyricsType = 20f, StandardTheatreLyricsType = 22f, HugeTheatreLyricsType = 26f;

    /// <summary>Small (W-Stage-Small, W-Lyrics-Small): the 104 card at 16, the lyrics at 22; Card or Large art (W-Tiers).</summary>
    public static StageTierMetrics SmallMetrics { get; } = new(TransportH: 104f, TransportInset: 16f, LyricsTypeMin: 22f, LyricsTypeMax: 22f)
    {
        GalleryOnDemand = true, Layouts = StageLayouts.Card | StageLayouts.LargeArt,
        TheatrePaneW = SmallTheatrePaneW, TheatreLyricsType = SmallTheatreLyricsType,
    };

    /// <summary>Standard (W-Stage, W-Lyrics): the 112 card at 24, the lyrics at 28 (32 on a tall stage).</summary>
    public static StageTierMetrics StandardMetrics { get; } = new(TransportH: 112f, TransportInset: 24f, LyricsTypeMin: 28f, LyricsTypeMax: 32f)
    {
        GalleryOnDemand = true,
        TheatrePaneW = StandardTheatrePaneW, TheatreLyricsType = StandardTheatreLyricsType,
    };

    /// <summary>The Up next column beside the lyrics at Huge (W-Lyrics-Huge draws 648 at 2560; 560 leaves the lyrics a reading measure beside it, the core keeps a 336 gap between them).</summary>
    public const float UpNextW = 560f;

    /// <summary>Huge and every width from 1920 (W-Lyrics-Huge, W-Stage-Huge4K): the 128 card at 32, the lyrics at 36 grown up to 52 on the 4K
    /// stage, the Up next column beside them.</summary>
    public static StageTierMetrics HugeMetrics { get; } = new(TransportH: 128f, TransportInset: 32f, LyricsTypeMin: 36f, LyricsTypeMax: 52f)
    {
        GalleryOnDemand = true, UpNextW = UpNextW,
        TheatrePaneW = HugeTheatrePaneW, TheatreLyricsType = HugeTheatreLyricsType,
    };

    /// <summary>WinUI's stage policy: the core's class edges and the metrics above. One instance, so a republish is not a change. Declared after
    /// the metrics it names: initializers run in textual order. WinUI draws the stage's Video mode (Cinema and Theatre, `Views\Stage\WinUiStage
    /// PictureChrome.cs`): one full screen while a video is on (F11 and "Video in fullscreen" open the stage on the picture).</summary>
    public static StagePolicy Policy { get; } = new(AspectThresholds.Default)
    {
        Metrics = StandardMetrics,
        Compact = CompactMetrics,
        Small = SmallMetrics,
        Huge = HugeMetrics,
        VideoStage = true,
    };

    /// <summary>Video mode per window tier (the stage views' record below): the Up next shelf's card width over the picture (Cinema; 0 = none, Compact),
    /// the end card's 16:9 thumbnail and its buttons' height (40 at Compact: the target rule; WinUI's 32 buttons above), and the command bar's inset
    /// from the picture's edges (the transport card's gutter a step in: the bar belongs to the picture). Declared before the records that read them.</summary>
    public const float SmallShelfCardW = 176f, StandardShelfCardW = 208f, HugeShelfCardW = 256f;

    public const float CompactEndThumbW = 240f, SmallEndThumbW = 288f, StandardEndThumbW = 384f, HugeEndThumbW = 480f, EndButtonH = 32f, CompactEndButtonH = 40f;

    public const float CompactBarInset = 8f, SmallBarInset = 16f, StandardBarInset = 16f, HugeBarInset = 24f;   // the wordmark's own inset (the bar's left padding): one left edge

    // ── the stage views' record (per window tier) ────────────────────────────────────────────────────────────────────────

    private static readonly WinUiStageLayout[] s_stage =
    [
        // Compact (W-Stage-Compact, W-Lyrics-Compact): the sheet.
        new()
        {
            Tier = WidthTier.Compact, Sheet = true,
            BarH = 52f, BarPad = new Edges4(4f, 8f, 4f, 0f), Wordmark = false, Subtitle = false,
            PillIcons = true, PillItemH = 40f, PillItemPadX = 12f, PillGap = 2f, PillTextSize = 14f, BarButtonW = 40f, BarButtonH = 40f,
            LookWords = false, RomanizationWord = false,
            Form = WinUiTransportForm.Stack, TransportPadding = new Edges4(16f, 0f, 16f, 12f),
            CenterW = 0f, ColumnGap = 0f, ButtonBox = 48f, Disc = 64f, ClusterGap = 0f, SeekGap = 2f, TimeW = 0f, TimeGap = 0f,
            VolumeW = 0f, DeviceH = 40f, DevicePadX = 12f, Glyph = 18f, PlayGlyph = 24f,
            LeadArtVisualizer = 0f, LeadArtOther = 0f, ChipH = 0f, ChipPadX = 0f, StackRowH = 96f, StackFootH = 40f,
            Card = null, HeroTitles = true, HeroTitle = 0f, HeroTitleLine = 0f, HeroMeta = 0f, HeroMetaLine = 0f,
            ShelfCardW = 0f, EndThumbW = CompactEndThumbW, EndButtonH = CompactEndButtonH, BarInset = CompactBarInset,   // no shelf: no Theatre either
        },
        // Small (W-Stage-Small, W-Lyrics-Small)
        new()
        {
            Tier = WidthTier.Small, Sheet = false,
            BarH = 48f, BarPad = new Edges4(16f, 0f, 8f, 0f), Wordmark = true, Subtitle = false,
            PillIcons = false, PillItemH = 38f, PillItemPadX = 12f, PillGap = 2f, PillTextSize = 14f, BarButtonW = 40f, BarButtonH = 36f,
            LookWords = false, RomanizationWord = false,
            Form = WinUiTransportForm.HeartLead, TransportPadding = new Edges4(12f, 8f, 12f, 8f),
            CenterW = 400f, ColumnGap = 12f, ButtonBox = 40f, Disc = 48f, ClusterGap = 12f, SeekGap = 4f, TimeW = 32f, TimeGap = 8f,
            VolumeW = 0f, DeviceH = 40f, DevicePadX = 10f, Glyph = 16f, PlayGlyph = 20f,
            LeadArtVisualizer = 0f, LeadArtOther = 0f, ChipH = 0f, ChipPadX = 0f, StackRowH = 0f, StackFootH = 0f,
            Card = new WinUiStageCard(X: 16f, Y: 60f, W: 340f, Pad: 8f, Art: 96f, Gap: 12f, Eyebrow: false),
            HeroTitles = true, HeroTitle = 34f, HeroTitleLine = 40f, HeroMeta = 14f, HeroMetaLine = 20f,
            ShelfCardW = SmallShelfCardW, EndThumbW = SmallEndThumbW, EndButtonH = EndButtonH, BarInset = SmallBarInset,
        },
        // Standard (W-Stage, W-Lyrics)
        new()
        {
            Tier = WidthTier.Standard, Sheet = false,
            BarH = 48f, BarPad = new Edges4(16f, 0f, 8f, 0f), Wordmark = true, Subtitle = true,
            PillIcons = false, PillItemH = 38f, PillItemPadX = 14f, PillGap = 4f, PillTextSize = 14f, BarButtonW = 40f, BarButtonH = 36f,
            LookWords = false, RomanizationWord = true,
            Form = WinUiTransportForm.Identity, TransportPadding = new Edges4(16f, 12f, 16f, 12f),
            CenterW = 560f, ColumnGap = 16f, ButtonBox = 40f, Disc = 48f, ClusterGap = 12f, SeekGap = 4f, TimeW = 36f, TimeGap = 10f,
            VolumeW = 96f, DeviceH = 32f, DevicePadX = 10f, Glyph = 16f, PlayGlyph = 20f,
            LeadArtVisualizer = 80f, LeadArtOther = 56f, ChipH = 20f, ChipPadX = 6f, StackRowH = 0f, StackFootH = 0f,
            Card = null, HeroTitles = false, HeroTitle = 0f, HeroTitleLine = 0f, HeroMeta = 0f, HeroMetaLine = 0f,
            ShelfCardW = StandardShelfCardW, EndThumbW = StandardEndThumbW, EndButtonH = EndButtonH, BarInset = StandardBarInset,
        },
        // Huge (W-Stage-Huge, W-Lyrics-Huge, W-Stage-Huge4K: 3840 is Huge with more room)
        new()
        {
            Tier = WidthTier.Huge, Sheet = false,
            BarH = 56f, BarPad = new Edges4(24f, 0f, 12f, 0f), Wordmark = true, Subtitle = true,
            PillIcons = false, PillItemH = 38f, PillItemPadX = 16f, PillGap = 4f, PillTextSize = 14f, BarButtonW = 40f, BarButtonH = 36f,
            LookWords = true, RomanizationWord = true,
            Form = WinUiTransportForm.HeartSource, TransportPadding = new Edges4(24f, 16f, 24f, 16f),
            CenterW = 720f, ColumnGap = 24f, ButtonBox = 44f, Disc = 56f, ClusterGap = 16f, SeekGap = 6f, TimeW = 40f, TimeGap = 12f,
            VolumeW = 128f, DeviceH = 36f, DevicePadX = 12f, Glyph = 18f, PlayGlyph = 22f,
            LeadArtVisualizer = 0f, LeadArtOther = 0f, ChipH = 24f, ChipPadX = 8f, StackRowH = 0f, StackFootH = 0f,
            Card = new WinUiStageCard(X: 32f, Y: 72f, W: 420f, Pad: 12f, Art: 96f, Gap: 16f, Eyebrow: true),
            HeroTitles = true, HeroTitle = 40f, HeroTitleLine = 52f, HeroMeta = 20f, HeroMetaLine = 28f,
            ShelfCardW = HugeShelfCardW, EndThumbW = HugeEndThumbW, EndButtonH = EndButtonH, BarInset = HugeBarInset,
        },
    ];

    /// <summary>The stage views' record for window tier <paramref name="window"/> (Huge's from 1920 up: WinUI has no Huge 4K record).</summary>
    public static WinUiStageLayout Stage(WidthTier window) => s_stage[Math.Clamp((int)window, 0, s_stage.Length - 1)];

    /// <summary>The stage views' record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static WinUiStageLayout StageOf(in TierFacts facts) => Stage(facts.Policy is null ? WidthTier.Standard : facts.Window);
}

/// <summary>How the transport card seats its three columns in a window tier.</summary>
public enum WinUiTransportForm : byte
{
    /// <summary>Compact: no card; the seek and its times, the 96 row (shuffle · previous · disc · next · repeat), the 40 row (device · "from ..." · "...").</summary>
    Stack = 0,

    /// <summary>Small: the heart and one quiet word (or the translation toggle) left; the volume, device and "..." glyphs right.</summary>
    HeartLead = 1,

    /// <summary>Standard: the identity left (the cover, the title, the artists, "Playing from", the chips); heart · mute · volume · device · "..." right.</summary>
    Identity = 2,

    /// <summary>Huge: the heart, "Playing from" and the chips left; mute · volume · device · "..." right.</summary>
    HeartSource = 3,
}

/// <summary>The now-playing card of the Card layout in Visualizer mode (W-Stage-Small, W-Stage-Huge): its place, padding, cover and the gap to the
/// titles; <paramref name="Eyebrow"/> adds "Now playing" over the title and "Next: ..." under the artists (Huge).</summary>
public sealed record WinUiStageCard(float X, float Y, float W, float Pad, float Art, float Gap, bool Eyebrow)
{
    /// <summary>The card's height: the cover and its padding.</summary>
    public float H => Art + 2f * Pad;
}

/// <summary>What WinUI's stage views draw in one window tier (the allocator's geometry is <c>Stage.Layout</c>'s).</summary>
public sealed record WinUiStageLayout
{
    /// <summary>The window tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The stage is drawn as a sheet (Compact): the r8 top, the grabber, the glyph-only pill, no card behind the transport.</summary>
    public required bool Sheet { get; init; }

    // ── the top bar ──

    /// <summary>The bar's height (48; 56 at Huge) and padding.</summary>
    public required float BarH { get; init; }

    public required Edges4 BarPad { get; init; }

    /// <summary>The wordmark leads the bar (not in the sheet: the exit chevron does); <see cref="Subtitle"/> adds "Now playing · full screen".</summary>
    public required bool Wordmark { get; init; }

    public required bool Subtitle { get; init; }

    /// <summary>The mode pill: glyph-only items (Compact), the items' height, side padding and gap, the label size.</summary>
    public required bool PillIcons { get; init; }

    public required float PillItemH { get; init; }

    public required float PillItemPadX { get; init; }

    public required float PillGap { get; init; }

    public required float PillTextSize { get; init; }

    /// <summary>A glyph button of the bar (options, exit, the AI-timing mark).</summary>
    public required float BarButtonW { get; init; }

    public required float BarButtonH { get; init; }

    /// <summary>The bar names the look in Visualizer mode: the face and layout words beside options (Huge).</summary>
    public required bool LookWords { get; init; }

    /// <summary>Lyrics mode shows the "Romanization" word button in the bar (Standard, Huge; Small's toggle sits in the card).</summary>
    public required bool RomanizationWord { get; init; }

    // ── the transport card ──

    /// <summary>How the card seats its columns.</summary>
    public required WinUiTransportForm Form { get; init; }

    /// <summary>The card's padding.</summary>
    public required Edges4 TransportPadding { get; init; }

    /// <summary>The centre column's width (the buttons over the seek) and the gap between the three columns.</summary>
    public required float CenterW { get; init; }

    public required float ColumnGap { get; init; }

    /// <summary>A plateless glyph button, the play disc, the gap between the centred buttons, the gap between the buttons and the seek row.</summary>
    public required float ButtonBox { get; init; }

    public required float Disc { get; init; }

    public required float ClusterGap { get; init; }

    public required float SeekGap { get; init; }

    /// <summary>The seek row's times: their width and their gap to the bar.</summary>
    public required float TimeW { get; init; }

    public required float TimeGap { get; init; }

    /// <summary>The volume slider's length; 0: the volume is a glyph (Small).</summary>
    public required float VolumeW { get; init; }

    /// <summary>The device button's height and side padding.</summary>
    public required float DeviceH { get; init; }

    public required float DevicePadX { get; init; }

    /// <summary>The transport glyphs and the disc's glyph.</summary>
    public required float Glyph { get; init; }

    public required float PlayGlyph { get; init; }

    /// <summary>The identity column's cover (Standard: 80 in Visualizer mode, 56 otherwise); 0: the form has none.</summary>
    public required float LeadArtVisualizer { get; init; }

    public required float LeadArtOther { get; init; }

    /// <summary>The chips' height and side padding; 0: no chips.</summary>
    public required float ChipH { get; init; }

    public required float ChipPadX { get; init; }

    /// <summary>The stacked transport's control row and foot row (Compact).</summary>
    public required float StackRowH { get; init; }

    public required float StackFootH { get; init; }

    // ── the identity ──

    /// <summary>The Card layout's now-playing card in Visualizer mode; null: none (Standard: the card carries the identity; Compact: the row).</summary>
    public required WinUiStageCard? Card { get; init; }

    /// <summary>The titles under the cover in Lyrics, Up next and the Large art layout (Standard's transport card carries them instead).</summary>
    public required bool HeroTitles { get; init; }

    /// <summary>The hero titles' type under the Lyrics / Large art cover (W-Lyrics-Small 34 / 40 over 14 / 20, W-Lyrics-Huge 40 / 52 over 20 / 28);
    /// 0: the allocator's.</summary>
    public required float HeroTitle { get; init; }

    public required float HeroTitleLine { get; init; }

    public required float HeroMeta { get; init; }

    public required float HeroMetaLine { get; init; }

    // ── Video mode (Cinema and Theatre) ──

    /// <summary>The Up next shelf over the picture (Cinema): one card's width (a video's 16:9 still); 0 where the tier has no shelf (Compact).</summary>
    public required float ShelfCardW { get; init; }

    /// <summary>The end card's 16:9 thumbnail (Cinema; Theatre fits it to the picture's box) and its buttons' height.</summary>
    public required float EndThumbW { get; init; }

    public required float EndButtonH { get; init; }

    /// <summary>The command bar's inset from the picture's sides and foot (Cinema: the stage's; Theatre takes the smaller of this and
    /// <c>WinUiStageVideoBarGeometry.TheatreInset</c>).</summary>
    public required float BarInset { get; init; }
}
