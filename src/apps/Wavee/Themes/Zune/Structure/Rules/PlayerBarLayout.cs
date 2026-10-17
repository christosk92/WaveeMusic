// ── Themes/Zune/Structure/Rules/PlayerBarLayout.cs — the look numbers of Zune's player family (P5 Z4a) ──────────────────────────────
//
// The player family's views (`Views\Player\*`, `Views\Panel\*` but the panel's frame, `Views\Queue\ZuneQueuePanel.cs`) read their numbers
// here (WVTH003 allows literals only in `Rules\**`). What a level of the bar carries and where it starts is `Tiers.Player.cs`; this file is
// the look of the parts:
//
//   ZunePlayerLayout     the bar's motion (a level crossing FLIPs every structural node, a line enters or leaves with a fade, a tool word
//                        lights by opacity only, the face cross-fades), the marquee's cadence, the solid transport glyphs and the parts
//                        a level does not size (the words' inset, the volume line and its popup, the podcast speed panel)
//   ZuneTransportLayout  the seek rail (the 2-px line in a 12 hit box, the 12 round thumb on hover), the chapter ticks and tip, the scrub
//                        readout and the time labels (Z-PlayBar, Z-Part-PlayBar)
//   ZunePanelBodies      the panel's bodies (Z-Part-Panel): their insets, the lyrics header's word row, "next up", the lyrics peek, the
//                        friends rows, the now-playing hero, the device picker (Z-PlayBar-Huge's "play on") and the deck style flyout
//   ZuneQueueRules       the queue body (Z-Part-Panel queue, Queue): the mode words, "playing from", the now-playing row, the sections'
//                        headers, the rows and "show more"; the lane's slot extents are computed here, exact
//   ZuneOnMediaLayout    the strip over a video (built from Zune's parts: no board)

using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The constants of Zune's play bar.</summary>
public static class ZunePlayerLayout
{
    // ── node keys (the mounted tests find them; no key names a level, a form or a tier) ─────────────────────────────

    /// <summary>The bar inside its slot, its seek row, the seek line's box, the transport row, the disc, the identity column and the tools.</summary>
    public const string BarKey = "zune-bar", SeekRowKey = "zune-bar-seek", SeekKey = "zune-bar-seek-line", RowKey = "zune-bar-row",
        DiscKey = "zune-bar-disc", LeftKey = "zune-bar-left", RightKey = "zune-bar-right";

    /// <summary>The panel bodies': the lyrics header's meta row, the queue's mode row, the hero's deck slot and meta, the device picker's column.</summary>
    public const string LyricsMetaKey = "zune-lyrics-meta", QueueModesKey = "zune-queue-modes", HeroSlotKey = "zune-hero-slot",
        HeroMetaKey = "zune-hero-meta", PickerKey = "zune-device-picker";

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A level crossing: every structural node FLIPs to its new rect over 167 ms (the slot's 72 ↔ 88 at Huge 4K too).</summary>
    public static readonly LayoutTransition MoveMotion = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(Design.Motion.Fast, Easing.FluentPopOpen), SizeMode.Reveal);

    /// <summary>A line entering or leaving (the "playing on" line, a time label, the separator): fade + size, neighbours slide over.</summary>
    public static readonly LayoutTransition ItemMotion = new(
        TransitionChannels.Bounds | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut),
        SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Motion.Fast, Easing.SmoothOut));

    /// <summary>A reserved tool lights or dims: opacity only, so nothing around it moves.</summary>
    public static readonly LayoutTransition FaceMotion = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut), SizeMode.Auto);

    /// <summary>The identity's text cross-fades when the face swaps to another playable (opacity only; the art dissolves itself).</summary>
    public static readonly MotionTokenDef FaceSwap =
        MotionTokenDef.Eased(Design.Motion.Standard, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade);

    /// <summary>The marquee's cadence (the production one: a slow pass every 14 s, 3 s rests, 2 s before the first).</summary>
    public const float MarqueeCycleMs = 14_000f, MarqueeEndPauseMs = 3_000f, MarqueeStartDelayMs = 2_000f, MarqueeSpeed = 18f;

    // ── the identity ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the cover and the text column (the board's 12), and between the "playing on" label and the name.</summary>
    public const float IdGap = 12f, FromGap = 4f;

    /// <summary>The title's weight (the board's regular Segoe UI Variable Text).</summary>
    public const ushort TitleWeight = 400;

    // ── the transport ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Segoe Fluent Icons' solid play and pause: the disc is filled, so its glyph is a filled shape too.</summary>
    public const string PlaySolid = "", PauseSolid = "";

    /// <summary>The disc's glyph is drawn at this fraction of its glyph box (20 → 16, the board's), the play triangle nudged right by a
    /// twelfth of it (its optical centre).</summary>
    public const float DiscGlyphScale = 0.8f, PlayNudgeDivisor = 12f;

    /// <summary>The ±10 s steps skip this far.</summary>
    public const int SeekStepMs = 10_000;

    /// <summary>A step word's inset (the board's 0 6), the speed word's width (48).</summary>
    public const float StepPadX = 6f, SpeedW = 48f;


    // ── the tools ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A tool word's inset (the board's 0 8) and the video word's chevron.</summary>
    public const float WordPadX = 8f, ChevronGlyph = 10f, ChevronGap = 4f;

    /// <summary>The gap between the tools (the board's 2).</summary>
    public const float ToolGap = 2f;

    /// <summary>The inline volume line: the 2-px track inside a 32 hit box, its 12 round thumb, 2 before it and 10 after it.</summary>
    public const float VolumeTrack = 2f, VolumeThickness = 32f, VolumeThumb = 12f, VolumeThumbInner = 12f;

    public static readonly Edges4 VolumeMargin = new(2f, 0f, 10f, 0f);

    /// <summary>The rail's ink alpha under the value (the board's white at 0.18), disabled.</summary>
    public const float VolumeRailA = 0.18f, VolumeRailDisabledA = 0.08f;

    /// <summary>The volume popup of the levels without the inline line (Z-PlayBar: 52 x 168, a 128 rail).</summary>
    public const float VolumePopupW = 52f, VolumePopupH = 168f, VolumePopupRail = 128f;

    // ── the podcast speed panel (Z-PlayBar: "playback speed") ──────────────────────────────────────────────────────────

    /// <summary>The panel's width, inset and gap; its heading (20/28 light); its slider's length.</summary>
    public const float SpeedPanelW = 256f, SpeedPanelPad = 16f, SpeedPanelGap = 12f, SpeedTitleSize = 20f, SpeedTitleLine = 28f, SpeedSliderW = 224f;

    public const ushort SpeedTitleWeight = 300;

    /// <summary>The speed slider's range and steps (the model clamps to the same band) and the preset words under it.</summary>
    public const float SpeedMin = .5f, SpeedMax = 3f, SpeedStep = .05f, SpeedLargeStep = .25f;

    public static readonly float[] SpeedPresets = [1f, 1.5f, 2f, 3f];
}

/// <summary>The constants of Zune's seek rail, chapter ticks, readout and time labels (the bar's, the stage's and the on-media strip's).</summary>
public static class ZuneTransportLayout
{
    /// <summary>The rail's hit box (12) around the 2-px line; the thumb (12, round, the accent) shows on hover and while scrubbing.</summary>
    public const float HitH = 12f, LineH = 2f, Thumb = 12f;

    /// <summary>The line's top inside the hit box.</summary>
    public const float LineTop = (HitH - LineH) / 2f;

    /// <summary>The rail's ink alpha under the value (the board's white at 0.12).</summary>
    public const float RailA = 0.12f;

    /// <summary>The margin that seats a 12 hit box on a row <paramref name="rowH"/> tall with the line on the row's centre: negative on the
    /// bar's 2-px row, so the hit box reaches 5 above and below the line.</summary>
    public static Edges4 RailMargin(float rowH) => new(0f, (rowH - HitH) / 2f, 0f, 0f);

    /// <summary>The DVR live edge.</summary>
    public const float LiveEdgeW = 2f;

    /// <summary>The live line's breath (0.55 ↔ 1 over 3 s while playing) and the flat track that replaces it.</summary>
    public static readonly Keyframe[] LiveLineBreathe = [new(0f, 0.55f), new(0.5f, 1f), new(1f, 0.55f)];

    public static readonly Keyframe[] LiveLineFlat = [new(0f, 1f), new(1f, 1f)];

    public const float LiveLineBreatheMs = 3000f, LiveLineFlatMs = 1f;

    /// <summary>A chapter tick: 1 x 8, the ink at 0.6, hanging from the line.</summary>
    public const float TickW = 1f, TickH = 8f, TickA = 0.6f;

    /// <summary>The chapter tip: width, cap, inset, gap, its time (12) and title (13) lines.</summary>
    public const float TipW = 260f, TipMaxW = 320f, TipGap = 2f, TipTimeSize = 12f, TipTimeLine = 16f, TipTitleSize = 13f, TipTitleLine = 18f;

    public static readonly Edges4 TipPadding = new(12f, 8f, 12f, 10f);

    public const int TipTitleLines = 2;

    /// <summary>The scrub readout: its inset and its digits (13/18).</summary>
    public static readonly Edges4 ReadoutPadding = new(8f, 4f, 8f, 4f);

    public const float ReadoutSize = 13f, ReadoutLine = 18f;

    /// <summary>The time labels (12/16, the board's): a slot wide enough for "-59:59" so 0:09 → 0:10 moves nothing (a floor while live), and
    /// the " · " between the two.</summary>
    public const float TimeSize = 12f, TimeLine = 16f, TimeSlotW = 40f;

    /// <summary>The times BESIDE the transport (the bar's full, wide and big levels, the T1 audit): each 16 from the button it flanks.</summary>
    public const float TimeSideGap = 16f;

    /// <summary>The margin that puts a time slot <see cref="TimeSideGap"/> from the transport button beside it, on top of the transport's
    /// own <paramref name="transportGap"/>: on the slot's trailing side for elapsed (left of previous), its leading side for remaining.</summary>
    public static Edges4 TimeSideMargin(float transportGap, bool remaining)
    {
        float extra = TimeSideGap - transportGap;
        return remaining ? new Edges4(extra, 0f, 0f, 0f) : new Edges4(0f, 0f, extra, 0f);
    }

    /// <summary>The times row UNDER the transport (narrow, medium) carries its gap as its own top padding, so when a level change takes it
    /// away its whole height (gap and line) eases out and the centred disc row glides, never jumping by the gap.</summary>
    public static Edges4 TimesRowPadding(float timesGap) => new(0f, timesGap, 0f, 0f);

    /// <summary>The live slot (sized once for "go live −99:59"), its words (11/14 semibold), the static red dot and the outlined mark.</summary>
    public const float LiveSlotW = 104f, LiveSize = 11f, LiveLine = 14f, LiveDot = 6f, LiveMarkH = 16f, LiveMarkPadX = 4f, LiveGap = 4f;

    public const ushort LiveWeight = 600;
}

/// <summary>The constants of the panel's bodies (Z-Part-Panel).</summary>
public static class ZunePanelBodies
{
    /// <summary>The bodies' inset: 24 left (under the pivot head's first word), 20 right.</summary>
    public static readonly Edges4 Inset = new(24f, 0f, 20f, 0f);

    // ── the lyrics header: the meta row under the pivots ───────────────────────────────────────────────────────────

    /// <summary>The meta row (28; 40 at Compact, the touch floor), its words 16 apart (12/16), a glyph in it (14).</summary>
    public const float MetaRowH = 28f, MetaRowCompactH = 40f, MetaGap = 16f, MetaSize = 12f, MetaLine = 16f, MetaGlyph = 14f;

    /// <summary>The sparkle's breath while the track is being timed, and its settle.</summary>
    public const float SparklePulseMs = 1800f;

    public static readonly Keyframe[] SparklePulse =
        [new Keyframe(0f, 1f), new Keyframe(0.5f, 0.55f, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)];

    public static readonly SpringParams SparkleSettle = SpringParams.FromResponse(0.35f, 1f);

    /// <summary>The meta row's height in a window tier: the Compact touch floor, else the board's 28.</summary>
    public static float MetaRow(WidthTier window) => window == WidthTier.Compact ? MetaRowCompactH : MetaRowH;

    // ── "next up" ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row: the 36 square art, the 52 floor (the board's grid row).</summary>
    public const float RowArt = 36f, RowH = 52f;

    // ── the lyrics peek ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two 28 rows (the sung line, the next at 0.4) on a 2-px square accent spine 12 from the text.</summary>
    public const float PeekRowH = 28f, PeekReelH = PeekRowH * 2f, PeekSpineW = 2f, PeekGap = 12f, PeekOpacity = 0.4f, PeekNoteSize = 12f;

    // ── friends ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row (Z-Part-Panel friends): the 40 round avatar in a 52 column, 64 tall at least; name 15/20, the track 13/18 (the accent
    /// while live), the context and the time 12/16; the live equalizer 10.</summary>
    public const float FriendRowH = 64f, Avatar = 40f, AvatarCol = 52f, FriendNameSize = 15f, FriendNameLine = 20f, FriendTrackSize = 13f,
        FriendTrackLine = 18f, FriendSmallSize = 12f, FriendSmallLine = 16f, FriendWhenPad = 8f, FriendEq = 10f, FriendLinesGap = 1f;

    /// <summary>The friend rows' entrance and exit travel.</summary>
    public const float FriendEnterDy = 6f, FriendExitDy = 4f;

    /// <summary>The skeleton's rows and bars.</summary>
    public const int SkeletonRows = 5;

    public const float SkeletonNameW = 96f, SkeletonDetailW = 160f, SkeletonBarH = 10f, SkeletonGap = 6f;

    // ── the now-playing hero ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hero's inset (24 left, 4 above, 20 right), the gap between the cover and the meta, the meta's lines (the artists 13/18, the
    /// album 12/16), the heart (40, 16) and the gap above the lyrics peek.</summary>
    public static readonly Edges4 HeroPadding = new(24f, 4f, 20f, 0f);

    public const float HeroGap = 16f, HeroMetaGap = 2f, HeroArtistSize = 13f, HeroArtistLine = 18f, HeroAlbumSize = 12f, HeroAlbumLine = 16f,
        HeroHeart = 40f, HeroHeartGlyph = 16f, HeroPeekTop = 12f;

    /// <summary>The hero cover's decode size.</summary>
    public const int HeroDecodePx = 640;

    // ── the device picker (Z-PlayBar-Huge: "play on") ─────────────────────────────────────────────────────────────

    /// <summary>The picker: 340 wide, 8 above and below; the "play on" head and an information row (32, 12/16); a device row (52, the 8 dot,
    /// 12 to the name 14/20 over its note 12/16, 16 in); a divider's inset; the resting dot's ink alpha.</summary>
    public const float PickerW = 340f, PickerPadY = 8f, PickerHeadH = 32f, PickerSmallSize = 12f, PickerSmallLine = 16f, PickerRowH = 52f,
        PickerDot = 8f, PickerGap = 12f, PickerNameSize = 14f, PickerNameLine = 20f, PickerPadX = 16f, PickerDotRestA = 0.24f;

    public static readonly Edges4 PickerDividerMargin = new(16f, 4f, 16f, 4f);

    // ── the deck style flyout (built from Zune's parts) ───────────────────────────────────────────────────────────

    /// <summary>The flyout: 300 wide, 16 in, 12 between blocks; the heading (20/28 light), a group's label (12/16), the preset and choice words
    /// (14/20, 12 apart, 4 between lines); the swatches (a 30 ring of 2 around a 22 dot).</summary>
    public const float DeckW = 300f, DeckPad = 16f, DeckGap = 12f, DeckTitleSize = 20f, DeckTitleLine = 28f, DeckLabelSize = 12f, DeckLabelLine = 16f,
        DeckWordSize = 14f, DeckWordLine = 20f, DeckWordGap = 12f, DeckWordRowGap = 4f, DeckSwatchRing = 30f, DeckSwatchDot = 22f, DeckSwatchStroke = 2f;

    public const ushort DeckTitleWeight = 300;
}

/// <summary>The queue body's constants and its lane's slot extents (Z-Part-Panel queue, Queue).</summary>
public static class ZuneQueueRules
{
    /// <summary>The mode words (shuffle · repeat · autoplay): a 28 row (40 at Compact), 16 apart, 13/18.</summary>
    public const float ModeRowH = 28f, ModeRowCompactH = 40f, ModeGap = 16f, WordSize = 13f, WordLine = 18f;

    /// <summary>The mode row's inset: the bodies' 24 / 20 less each word's own 8, so the first word's ink starts under the pivots and the words
    /// sit the board's 16 apart.</summary>
    public static readonly Edges4 ModeRowPadding = new(24f - ZunePlayerLayout.WordPadX, 0f, 20f - ZunePlayerLayout.WordPadX, 0f);

    /// <summary>"playing from …": 28 tall, its text at the foot.</summary>
    public const float PlayingFromH = 28f;

    /// <summary>The now-playing row: 72 tall, the 56 art in a 64 column, the title 17/22 and the artists 13/18 in the accent (the artists at 0.8).</summary>
    public const float NowH = 72f, NowArt = 56f, NowArtCol = 64f, NowTitleSize = 17f, NowTitleLine = 22f, NowSubA = 0.8f, NowLinesGap = 2f;

    /// <summary>A section's header: 32 (40 at Compact, so "clear" is a 40 target), its text at the foot 4 up, "clear" 12 after the name (12/16).</summary>
    public const float HeaderH = 32f, HeaderCompactH = 40f, HeaderPadBottom = 4f, HeaderGap = 12f, ClearSize = 12f, ClearLine = 16f;

    /// <summary>A row: the 36 square art and the board's 52 floor; the ✕ (40, glyph 12) as its trailing cell.</summary>
    public const float RowArt = 36f, RowFloor = 52f, CloseBox = 40f, CloseGlyph = 12f;

    /// <summary>"show next …": 40 tall, 4 above.</summary>
    public const float MoreH = 40f, MoreTop = 4f;

    /// <summary>The autoplay rows' dim (the board's 0.6), and the body's room under its last row.</summary>
    public const float AutoplayDim = 0.6f, BottomRoom = 16f;

    /// <summary>The row entrance and exit travel.</summary>
    public const float EnterDy = 6f, ExitDy = 4f;

    /// <summary>The shared surface's row text column (title, gap, caption).</summary>
    public const float TextColumn = SurfaceGeometry.CardTitleLineH + SurfaceGeometry.CardLabelGap + SurfaceGeometry.CardCaptionLineH;

    /// <summary>One row's resting extent, EXACT (the lane's drag maths reads it): the shared surface's 8 padding round the taller of the art and
    /// its text column, never under the board's 52.</summary>
    public static float RowExtent => MathF.Max(RowFloor, 2f * SurfaceGeometry.RowPad + MathF.Max(RowArt, TextColumn));

    /// <summary>The mode row's height in a window tier.</summary>
    public static float ModeRow(WidthTier window) => window == WidthTier.Compact ? ModeRowCompactH : ModeRowH;

    /// <summary>A header's extent in a window tier.</summary>
    public static float Header(WidthTier window) => window == WidthTier.Compact ? HeaderCompactH : HeaderH;

    /// <summary>The "show next" slot's extent: its row and the margin above it.</summary>
    public const float MoreExtent = MoreH + MoreTop;

    /// <summary>A section's rows dim when they are the radio's tail.</summary>
    public static float Dim(QueueSection section) => section == QueueSection.Autoplay ? AutoplayDim : 1f;

    /// <summary>Which adapter feeds a row: the seed face while its title is unknown, the episode adapter, the track adapter.</summary>
    public enum Face : byte { Seed, Track, Episode }

    public static Face FaceOf(EntityKind kind, bool thin) => thin ? Face.Seed : kind == EntityKind.Episode ? Face.Episode : Face.Track;
}

/// <summary>The constants of the strip over a video (built from Zune's parts).</summary>
public static class ZuneOnMediaLayout
{
    /// <summary>A glyph target (40, glyph 16), the accent disc (40, glyph box 20), the gap between them, the strip's inset and the seek row.</summary>
    public const float GlyphBox = 40f, Glyph = 16f, Disc = 40f, DiscGlyph = 20f, Gap = 8f, Pad = 12f, SeekRowH = 16f;
}
