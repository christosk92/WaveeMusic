// ── Themes/WinUi/Structure/Rules/WinUiPanelLayout.cs — the look numbers of WinUI's side panes and their bodies (P6 W4a) ─────────────────
//
// Boards W-Queue (and -Small / -Compact / -Huge), W-PaneLyrics, W-PaneNowPlaying, W-Shell-Compact (the sheet), W-Shell-Small (the overlay) and
// W-Shell-Huge (the two panes). The views (`Views\Panel\*`, `Views\Queue\*`) read their numbers here (WVTH003 allows literals only in
// `Rules\**`):
//
//   WinUiPanelLayout   the pane's frame: the tab row (48; the tabs Body 14 18 apart, the selected one primary with the 3 x 16 accent pill under
//                      it), the close (32, 40 at Compact), the overlay's acrylic and the dock <-> overlay crossfade (250 ms), the sheet
//                      (85 % high, r8 top corners, the 32 x 4 grabber), the open / close slide
//   WinUiPanelBodies   the bodies: the 16 inset, the lyrics header, "next up", the lyrics card, friends, the now-playing hero and its style
//                      flyout
//   WinUiQueueRules    the queue body: "Playing from", the now-playing card, the section headers, the rows, "show more", Autoplay; the lane's
//                      slot extents are computed here, exact
//
// COMPACT IS THE TOUCH TIER (owner rule): every pane control a Compact window shows is 40 x 40 at least (the close, the header buttons, the
// tab hit boxes, "Clear", "show more").
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's pane frame.</summary>
public static class WinUiPanelLayout
{
    /// <summary>The node keys the mounted tests and `find` read: the tab row, a tab (its prefix and the body's name), the close, the grabber,
    /// the body.</summary>
    public const string TabsKey = "winui-pane-tabs", TabKeyPrefix = "winui-pane-tab:", CloseKey = "winui-pane-close", GrabberKey = "winui-pane-grabber",
        BodyKey = "winui-pane-body", SheetKey = "winui-pane-sheet";

    /// <summary>The tab row: 48 tall (the tab's hit box: it clears the touch floor at every tier), 16 in, the tabs 18 apart (Body 14/20).</summary>
    public const float TabRowH = 48f, TabGap = 18f, TabSize = 14f, TabLine = 20f;

    /// <summary>A tab's least hit width (the touch floor: "Lyrics" is narrower than 40).</summary>
    public const float TabMinW = 40f;

    public static readonly Edges4 TabRowPadding = new(16f, 0f, 8f, 0f);

    /// <summary>The selected tab's pill: 3 tall, 16 wide (the SelectorBar's), 6 under the text's line, r1.5.</summary>
    public const float PillH = 3f, PillW = 16f, PillGap = 4f;

    /// <summary>The selected tab's weight (the SelectorBar's semibold).</summary>
    public const ushort TabSelectedWeight = 600, TabWeight = 400;

    /// <summary>The close: a 32 subtle button with a 12 glyph; 40 at Compact (the touch floor).</summary>
    public const float CloseBox = 32f, CloseTouchBox = 40f, CloseGlyph = 10f;

    /// <summary>The head's "⋯" (W-Queue-Small: the tabs the pane's width leaves out): its key and glyph (its box is the close's).</summary>
    public const string MoreKey = "winui-pane-more";

    public const float MoreGlyph = 16f;

    /// <summary>The room the tabs have in a head <paramref name="paneWidth"/> wide: the row's inset either side and the close.</summary>
    public static float TabLane(float paneWidth, float close) => paneWidth - TabRowPadding.Left - TabRowPadding.Right - close;

    /// <summary>Which tabs a head shows (<paramref name="shown"/>), from their words' measured <paramref name="widths"/> (a tab is at least
    /// <see cref="TabMinW"/>, 18 apart): every tab when they fit <paramref name="lane"/> (false: no "⋯"); else the current one and, in order,
    /// the others that fit beside the "⋯" (a <paramref name="more"/> box), the first that does not and every one after it moving into it.</summary>
    public static bool FitTabs(ReadOnlySpan<float> widths, int current, float lane, float more, Span<bool> shown)
    {
        float total = 0f;
        for (int i = 0; i < widths.Length; i++) total += (i > 0 ? TabGap : 0f) + MathF.Max(TabMinW, widths[i]);
        if (total <= lane + 0.5f) return false;
        float room = lane - more - TabGap, used = MathF.Max(TabMinW, widths[current]);
        bool open = true;
        for (int i = 0; i < widths.Length; i++)
        {
            if (i == current) { shown[i] = true; continue; }
            float w = TabGap + MathF.Max(TabMinW, widths[i]);
            shown[i] = open && used + w <= room + 0.5f;
            if (shown[i]) used += w; else open = false;
        }
        return true;
    }

    /// <summary>The sheet: 85 % of the body's height (the rest the scrim above it), the r8 top corners, the grabber (32 x 4, r2, 12 above and
    /// 4 under it: its 40-tall hit row closes the sheet).</summary>
    public const float SheetShare = 0.85f, GrabberW = 32f, GrabberH = 4f, GrabberRowH = 20f, GrabberTouchH = 40f;

    /// <summary>The open / close slide: 250 ms on the decelerate curve (X docked and as an overlay, Y as a sheet).</summary>
    public const float SlideMs = 250f;

    public static readonly Easing SlideEasing = Easing.FluentDecelerate;

    /// <summary>The dock ↔ overlay crossfade (W-Tiers: "switching between dock and overlay crossfades over 250 ms").</summary>
    public const float CrossfadeMs = 250f;
}

/// <summary>The constants of the pane's bodies.</summary>
public static class WinUiPanelBodies
{
    /// <summary>The lyrics header's row, the hero's slot and its meta (the mounted tests find them).</summary>
    public const string LyricsMetaKey = "winui-lyrics-header", HeroSlotKey = "winui-hero-slot", HeroMetaKey = "winui-hero-meta";

    /// <summary>The bodies' inset: 16 either side (the tabs' first letter, W-Queue).</summary>
    public static readonly Edges4 Inset = new(16f, 0f, 16f, 0f);

    /// <summary>A card's corner (r8) and a button's in a head (32, 40 at Compact).</summary>
    public const float CardRadius = 8f, HeadButton = 32f, TouchButton = 40f;

    // ── the lyrics header (W-PaneLyrics) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The header's buttons (32; 40 at Compact, the touch floor), 8 apart, the row 16 taller than them (8 above and below); the title
    /// (Body Strong 14/20); the button words (Caption Strong 12/16); a glyph (14); the Translate button's inset.</summary>
    public const float MetaRowH = 32f, MetaRowCompactH = 40f, HeaderRowExtra = 16f, MetaGap = 8f, MetaSize = 12f, MetaLine = 16f, MetaGlyph = 14f,
        HeaderTitleSize = 14f, HeaderTitleLine = 20f;

    public const ushort HeaderTitleWeight = 600;

    /// <summary>The header row's inset (W-PaneLyrics: 16 at the title, 8 after the last button) and its "Synced · word by word" line (Caption
    /// 12/16, secondary) under the song's name.</summary>
    public static readonly Edges4 HeaderPadding = new(16f, 0f, 8f, 0f);

    public const float HeaderSyncSize = 12f, HeaderSyncLine = 16f;

    public static readonly Edges4 TranslatePadding = new(12f, 0f, 12f, 0f);

    /// <summary>The sparkle's breath while the track is being timed, and its settle.</summary>
    public const float SparklePulseMs = 1800f;

    public static readonly Keyframe[] SparklePulse =
        [new Keyframe(0f, 1f), new Keyframe(0.5f, 0.55f, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)];

    public static readonly SpringParams SparkleSettle = SpringParams.FromResponse(0.35f, 1f);

    /// <summary>The header's button size in a window tier: the Compact touch floor, else 32.</summary>
    public static float MetaRow(WidthTier window) => window == WidthTier.Compact ? MetaRowCompactH : MetaRowH;

    // ── "next up" ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row: the 40 cover, the 56 floor (W-Queue's rows).</summary>
    public const float RowArt = 40f, RowH = 56f;

    // ── the lyrics card (W-PaneNowPlaying) ────────────────────────────────────────────────────────────────────────

    /// <summary>The card: 16 / 12 in, 4 between its lines; "Lyrics" and "Show lyrics" (Caption 12/16); two 24 rows (the sung line Body Strong
    /// 14, the next in the secondary ink); a video's one-row note (12) on the 2-px spine.</summary>
    public static readonly Edges4 PeekPadding = new(16f, 12f, 16f, 12f);

    public const float PeekGap = 4f, PeekCaptionSize = 12f, PeekCaptionLine = 16f, PeekLineSize = 14f, PeekRowH = 24f, PeekReelH = PeekRowH * 2f,
        PeekSpineW = 2f, PeekNoteSize = 12f;

    public const ushort PeekNextWeight = 400;

    // ── friends (undesigned: the queue rows' grammar) ─────────────────────────────────────────────────────────────

    /// <summary>A row: the 40 round avatar in a 52 column, 56 tall at least; the name (Body Strong 14/20), the track (Caption 12/16, the accent
    /// while live), the context and the time (Caption, tertiary); the live level bars 10.</summary>
    public const float FriendRowH = 56f, Avatar = 40f, AvatarCol = 52f, FriendNameSize = 14f, FriendNameLine = 20f, FriendTrackSize = 12f,
        FriendTrackLine = 16f, FriendSmallSize = 12f, FriendSmallLine = 16f, FriendWhenPad = 8f, FriendEq = 10f, FriendLinesGap = 0f;

    /// <summary>The friend rows' entrance and exit travel.</summary>
    public const float FriendEnterDy = 6f, FriendExitDy = 4f;

    /// <summary>The skeleton's rows and bars.</summary>
    public const int SkeletonRows = 5;

    public const float SkeletonNameW = 96f, SkeletonDetailW = 160f, SkeletonBarH = 10f, SkeletonGap = 6f;

    // ── the now-playing hero (W-PaneNowPlaying) ───────────────────────────────────────────────────────────────────

    /// <summary>The hero's inset (16, 4 above), 12 between its blocks; the title (Subtitle 20/28 semibold), the artists (Body 14/20, accent
    /// links), "from" and the album (Caption 12/16), 4 after "from"; the heart (40, 16).</summary>
    public static readonly Edges4 HeroPadding = new(16f, 4f, 16f, 16f);

    public const float HeroGap = 12f, HeroMetaGap = 2f, HeroTitleSize = 20f, HeroTitleLine = 28f, HeroArtistSize = 14f, HeroArtistLine = 20f,
        HeroAlbumSize = 12f, HeroAlbumLine = 16f, HeroFromGap = 4f, HeroHeart = 40f, HeroHeartGlyph = 16f;

    public const ushort HeroTitleWeight = 600;

    /// <summary>The hero cover's decode size.</summary>
    public const int HeroDecodePx = 640;

    // ── the deck style flyout (no WinUI board) ────────────────────────────────────────────────────────────────────

    /// <summary>The flyout: 300 wide, 16 in, 12 between blocks; the heading (Body Strong 14/20), a group's label (Caption 12/16), the preset
    /// and choice words (Body 14/20, 12 apart, 4 between lines); the swatches (a 30 ring of 2 around a 22 dot).</summary>
    public const float DeckW = 300f, DeckPad = 16f, DeckGap = 12f, DeckTitleSize = 14f, DeckTitleLine = 20f, DeckLabelSize = 12f, DeckLabelLine = 16f,
        DeckWordSize = 14f, DeckWordLine = 20f, DeckWordGap = 12f, DeckWordRowGap = 4f, DeckSwatchRing = 30f, DeckSwatchDot = 22f, DeckSwatchStroke = 2f;

    public const ushort DeckTitleWeight = 600;
}

/// <summary>The constants of the queue body (W-Queue).</summary>
public static class WinUiQueueRules
{
    /// <summary>The body's inset (16 either side, 16 under the last row).</summary>
    public static readonly Edges4 BodyPadding = new(16f, 0f, 16f, 16f);

    /// <summary>"Playing from …": 32 tall (Caption 12/16), 4 between the words.</summary>
    public const float PlayingFromH = 32f, FromGap = 4f, SmallSize = 12f, SmallLine = 16f;

    /// <summary>The now-playing card: 64 tall in an 8 inset, the 48 cover (r4), 12 apart; the title Body Strong 14/20 in the accent; the level
    /// glyph (16) at its end; 8 under the card.</summary>
    public const float NowH = 64f, NowArt = 48f, NowGap = 12f, ArtCorner = 4f, TitleSize = 14f, TitleLine = 20f, CardRadius = 8f, LevelGlyphSize = 16f;

    public const ushort TitleWeight = 600;

    public static readonly Edges4 NowPadding = new(8f, 0f, 12f, 0f), NowMargin = new(0f, 0f, 0f, 8f);

    /// <summary>The playing card's level glyph (Segoe Fluent Icons' "Audio" bars).</summary>
    public const string LevelGlyph = "";

    /// <summary>The skeleton line while the title is unknown.</summary>
    public const float SkeletonW = 160f, SkeletonH = 10f;

    /// <summary>A section's header: 40 (Body Strong 14/20; "Clear" and "<n> more" Caption at its end, "Clear" a 40 target).</summary>
    public const float HeaderH = 40f, HeaderSize = 14f, HeaderLine = 20f, ClearMinW = 40f;

    /// <summary>A row: the 40 cover and the 56 floor; the ✕ (32 box, 12 glyph; 40 at Compact) as its trailing cell.</summary>
    public const float RowArt = 40f, RowFloor = 56f, CloseBox = 32f, CloseGlyph = 12f;

    /// <summary>The ✕'s box in a window tier: 40 at Compact (the touch floor), else 32.</summary>
    public static float CloseOf(WidthTier window) => window == WidthTier.Compact ? MoreTouchH : CloseBox;

    /// <summary>"Show next …": 32 tall (40 at Compact), 4 above, 8 in.</summary>
    public const float MoreRowH = 32f, MoreTouchH = 40f, MoreTop = 4f;

    public static readonly Edges4 MorePadding = new(8f, 0f, 8f, 0f);

    /// <summary>The Autoplay row: 8 above the divider, 12 / 4 inside.</summary>
    public static readonly Edges4 AutoplayMargin = new(0f, 8f, 0f, 0f), AutoplayPadding = new(0f, 12f, 0f, 4f);

    /// <summary>The autoplay rows' dim, the row entrance and exit travel.</summary>
    public const float AutoplayDim = 0.6f, EnterDy = 6f, ExitDy = 4f;

    /// <summary>The shared surface's row text column (title, gap, caption).</summary>
    public const float TextColumn = SurfaceGeometry.CardTitleLineH + SurfaceGeometry.CardLabelGap + SurfaceGeometry.CardCaptionLineH;

    /// <summary>One row's resting extent, EXACT (the lane's drag maths reads it): the shared surface's padding round the taller of the cover and
    /// its text column, never under the 56 floor.</summary>
    public static float RowExtent => MathF.Max(RowFloor, 2f * SurfaceGeometry.RowPad + MathF.Max(RowArt, TextColumn));

    /// <summary>A header's extent in a window tier (40 everywhere: "Clear" is a 40 target).</summary>
    public static float Header(WidthTier window) => HeaderH;

    /// <summary>"Show next"'s row in a window tier: 40 at Compact (the touch floor), else 32.</summary>
    public static float MoreH(WidthTier window) => window == WidthTier.Compact ? MoreTouchH : MoreRowH;

    /// <summary>The "show next" slot's extent: its row and the margin above it.</summary>
    public static float MoreExtent(WidthTier window) => MoreH(window) + MoreTop;

    /// <summary>A section's rows dim when they are the radio's tail.</summary>
    public static float Dim(QueueSection section) => section == QueueSection.Autoplay ? AutoplayDim : 1f;

    /// <summary>Which adapter feeds a row: the seed face while its title is unknown, the episode adapter, the track adapter.</summary>
    public enum Face : byte { Seed, Track, Episode }

    public static Face FaceOf(EntityKind kind, bool thin) => thin ? Face.Seed : kind == EntityKind.Episode ? Face.Episode : Face.Track;
}
