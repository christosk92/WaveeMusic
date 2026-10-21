// ── Themes/Zune/Structure/Rules/ZuneDetailRules.cs — the numbers and records of Zune's detail frame and album (themes plan 6.3; P5 Z6a1) ──
//
// Boards Z-Album (Standard, 1440), Z-Album-Small (800), Z-Album-Compact (400), Z-Album-Huge (2560), Z-Album-Huge4K (3840), Album-Page and Album
// (the early panorama studies), Z-Light-Album (the light pass reads the same numbers).
//
// THE COMPOSITION. A Zune detail page is a PANORAMA: the page's title set huge and light across the top (`R.Type.Panorama`, fitted to one line, a long
// name on two smaller lines; see THE PANORAMA STEP), then up to three columns under it: the IDENTITY column (the cover, the artist, the meta
// line, the ring actions, the release's courtesy lines), the LIST (a "songs" heading over the track table), and the ASIDE ("more by", the
// artist, fans also like). The art wash behind the top is the material's (the frame places the core's tint and tone leaves; Zune's material
// draws them as the wash). The page's title and its views also sit in the band's row 2 (the page publishes them, `Album.Page.cs`).
//
// ONE RECORD PER CONTENT TIER (`ZuneDetailTiers.Of`): the columns follow the CONTENT width (the window less a docked panel), never the window's,
// so a Standard window with the panel docked lays the page out as Small instead of starving the list. The tier is the layout record in use,
// never a key (a crossing re-renders the frame; the slots ease their widths; the arms cross-fade):
//
//   Compact   (Z-Album-Compact)  the VERTICAL arm: the panorama in the compact voice (64/72), the 144 cover beside the artist and the meta,
//             the 40 ring row (play and saved with their words, share and "..." at the end), then the list. No rail, no aside.
//   Small     (Z-Album-Small)    the panorama 64/72 over a 160 identity column, 32 apart from the list; share and "..." only (from Small up
//             play, shuffle and saved live in the band's row 2: one home per verb, audit A4); the meta on two lines.
//
// THE PANORAMA BAND is the title's size, its descender (a quarter of it) and 8 of air (`PanoramaHFor`, audit A3: 88 / 158 / 196), so no descender
// is clipped and the body starts at least 8 under it. EVERY detail kind (album, playlist, Liked Songs, show, episode) sets its title at the SAME step
// per tier, the record's own; only a title too long for one line at it is set smaller (the owner's one exception to "Zune never shrinks display type", below).
//   Standard  (Z-Album)          the panorama 120/130 over the 240 identity column, the list, and the 260 aside, 48 apart.
//   Huge      (Z-Album-Huge)     the panorama 150/160; 320 identity, 400 aside (the artist band, "more by" as a grid, fans).
//   Huge 4K   (Z-Album-Huge4K)   the panorama 150/160; 400 identity, 440 aside, 64 apart, the larger text and the 40 rings, shuffle in the rings.
//
// A SHORT window (Z-Sizes-Breakpoints: below 700 tall) keeps the page's rows: the two-column arm's panorama folds away (its band eases to 0)
// and the identity column leads with the title in the heading voice instead. The vertical arm keeps its panorama (it scrolls away with the hero);
// a show's or an episode's vertical arm is a FIXED header, so it folds to the name over the ring row (`ShowPadTopShort`).
//
// THE INSIGHTS by tier (ZT5; the facts bento is the page's, Q18): a sheet over the list below Standard (320 to 400 wide, never wider than the page:
// a 300 window is covered edge to edge); from Standard the aside's section when an aside docks (320 wide alone, 420 at Huge and 440 at 4K; the
// aside docks by the window tier, the content tier folds it where a docked panel narrows the page); from Huge it rests OPEN (Z-Liked-Huge).
//
// THE PANORAMA STEP: one per tier for every kind, the record's own (64/72 at Compact and Small, 120/130 at Standard, 150/160 at Huge and 4K). The detail
// title FITS its band (owner exception 2026-10-21: two full-size lines squeezed the list to three rows): the step when it fits one line; else smaller,
// down to the floor (`ZuneDetailKit.PanoramaFloor`: 64 under the 120 and 150 steps, 40 under 64), until it fits one line; a name the floor cannot
// hold on one line wraps by whole words at the floor over up to `PanoramaLines` (2) lines, then ends in an ellipsis. The exact line scales with the
// size (the engine's one-line-first auto-fit), so two lines at the floor take about the band one full-size line takes. A fitted title stands on the
// band's foot (the step's baseline), and the band keeps its reserved height as a floor and HUGS anything taller (its height glides on the size
// channel, `PanoramaFold`, and everything below rides the presented extent, so nothing jumps). Every other Zune title keeps its size. Only a folded
// band (a short window) draws the title on one line, hidden.
//
// THE LADDERS the core resolves by id (`DetailLadders`, declared through `ZuneTiers.LaddersDetail`, Tiers.Detail.cs):
//   mode    the page column: the vertical arm below 640, the two-column arms from it (820 and 1008 split the core's three rail modes, which
//           Zune's records do not read), each held 24 on narrowing like every Zune edge (Z-Sizes-Breakpoints).
//   table   the list column: the core's seven table tiers at 300 / 340 / 440 / 560 / 720 / 860, a lane yielding at once and re-admitted 24
//           past its edge (the lanes the width admits; the window tier's record caps them, `ZuneListRules.DetailTiers`).
//   height  the window's height: short below 700 (the description's tight clamp), tall from 900 (the rail title cap), held 24 on shrinking.
//
// Public, so `Wavee.Tests` pins the rules (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The numbers and records of Zune's detail frame and album views.</summary>
public static class ZuneDetailRules
{
    // ── the node keys (the tests find them) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The frame's root, the panorama band, the identity column, the list column's heading, the aside slot and its column.</summary>
    public const string FrameKey = "zune.detail.frame", PanoramaKey = "zune.detail.panorama", RailKey = "zune.detail.rail",
        CompactRailKey = "zune.detail.compact-rail", SongsHeadingKey = "zune.detail.songs", AsideKey = "zune.detail.aside",
        AsideColumnKey = "zune.detail.aside-column", HeroKey = "zune.detail.hero", HeroBandKey = "zune.detail.hero-band",
        ShowHeaderKey = "zune.detail.show-header", NoticeKey = "zune.detail.notice", ActionsKey = "zune.detail.actions",
        HeadKey = "zune.detail.head";

    /// <summary>The album's pieces: the artist line, the release facts, the countdown, the trailing band's sections, one list stack, the aside.</summary>
    public const string ArtistLineKey = "zune.album.artist", ReleaseKey = "zune.album.release", CountdownKey = "zune.album.countdown",
        SectionsKey = "zune.album.sections", StackKey = "zune.album.stack", AlbumAsideKey = "zune.album.aside", FansKey = "zune.album.fans",
        AboutKey = "zune.album.about", UnresolvedKey = "zune.album.unresolved";

    /// <summary>The ring actions' key prefix (the vertical hero's and the identity column's): "play", "save", "share", "shuffle", "more".</summary>
    public const string RingPrefix = "zune.detail.ring:";

    // ── the mode ladder (the page column) ───────────────────────────────────────────────────────────────────────────

    /// <summary>The two-column arm begins at the Small edge; below it the vertical arm (Z-Album-Compact).</summary>
    public const float TwoColumnFrom = 640f;

    /// <summary>The core's narrow and mid rail modes split here (Zune's records read the content tier, not the mode).</summary>
    public const float MidFrom = 820f;

    /// <summary>The wide arm begins at the Standard edge.</summary>
    public const float WideFrom = 1008f;

    /// <summary>Every detail edge's hold: 24 on narrowing, none on widening (Z-Sizes-Breakpoints).</summary>
    public const float Hold = 24f;

    /// <summary>The page-mode ladder (index = 3 - mode).</summary>
    public static TierLadder Mode { get; } = TierLadder.Of(
        new TierEdge(TwoColumnFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(MidFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(WideFrom, NarrowHold: Hold, WidenHold: 0f));

    // ── the table ladder (the list column) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The table tiers' edges, narrowest first: tier 5 from 300 … tier 0 from 860.</summary>
    public static ImmutableArray<float> TableEdges { get; } = ImmutableArray.Create(300f, 340f, 440f, 560f, 720f, 860f);

    /// <summary>The table-tier ladder (index = 6 - tier): a lane yields at once and is re-admitted 24 past its edge.</summary>
    public static TierLadder Table { get; } = BuildTable();

    static TierLadder BuildTable()
    {
        var edges = new TierEdge[TableEdges.Length];
        for (int i = 0; i < edges.Length; i++) edges[i] = new TierEdge(TableEdges[i], NarrowHold: 0f, WidenHold: Hold);
        return new TierLadder(edges);
    }

    // ── the height rungs ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Regular from Zune's short edge; tall (the rail title's tall cap) from 900.</summary>
    public const float RegularFrom = 700f, TallFrom = 900f;

    /// <summary>The height-rung ladder, held 24 on shrinking.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(
        new TierEdge(RegularFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(TallFrom, NarrowHold: Hold, WidenHold: 0f));

    /// <summary>The three ladders as Zune's tier policy names them.</summary>
    public static ImmutableArray<NamedLadder> Ladders { get; } = ImmutableArray.Create(
        new NamedLadder(DetailLadders.Mode, Mode),
        new NamedLadder(DetailLadders.Table, Table),
        new NamedLadder(DetailLadders.Height, Height));

    /// <summary>The page mode of a <paramref name="width"/>-wide page column, held against <paramref name="previous"/> once
    /// <paramref name="initialized"/>. An unmeasured width keeps the previous mode.</summary>
    public static int ModeFor(float width, int previous, bool initialized = true)
    {
        if (!(width > 0f)) return previous;
        return DetailLadders.ModeOf(Mode.Resolve(width, initialized ? DetailLadders.IndexOfMode(previous) : -1));
    }

    // ── the page ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The widest the page's columns grow: the system page column's cap (<see cref="ZuneFrameRules.PageCap"/>), the gutters outside it.</summary>
    public const float PageCap = ZuneFrameRules.PageCap;

    /// <summary>The panorama band's height for a <c>size</c>-DIP title (audit A3): the size, its descender (a quarter of it) and 8 of air, so the
    /// body starts at least 8 below the descender (64: 88, 120: 158, 150: 196, Liked's 40: 58).</summary>
    public const float PanoramaDescent = 0.25f, PanoramaAir = 8f;

    /// <summary>The panorama band's height for a <c>size</c>-DIP title: the boards' own (Z-Album-Small 80 under a 64/72 title, Z-Album 140 under 120/130
    /// nudged up 6, Z-Album-Huge 176 under 150/160 nudged up 8). The title sits on its EXACT line (block line stacking), so its descender stays inside
    /// the band's gap under the line (8 / 16 / 24: at least the descender's quarter of the size plus air); a wrapped title adds that gap under its last
    /// line (<see cref="ZuneDetailLayout.PanoramaGap"/>), so a descender is never clipped.</summary>
    public static float PanoramaHFor(float size) => size <= 64f ? 80f : size <= 120f ? 140f : 176f;

    /// <summary>The slots' size ease (the album's trailing sections when a tier swaps their arrangement): clip-window reveal.</summary>
    public static readonly LayoutTransition SlotReveal = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.ColumnCollapseMs, Easing.SmoothOut), Size: SizeMode.Reveal);

    /// <summary>The columns' WIDTH ease (the identity column, the aside and the docked insights when a crossing moves them): REAL layout on the width axis,
    /// so the list beside them yields smoothly instead of jumping to its final width while the column's clip window eases (ZT5: `SizeMode.Reveal` lays the
    /// neighbours out at the final size at once). The content stays anchored to the leading edge (a wipe); the width alone animates.</summary>
    public static readonly LayoutTransition ColumnReflow = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.ColumnCollapseMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>The panorama band's height ease (a crossing to a short window folds it; a wrapping title or the editor grows it): the layout lands once and
    /// everything below RIDES the presented extent (ZT5: the flow reveal, as the chapters of the discovery pages do), so the columns under the band never
    /// jump when it folds. The title scales with its size step on its own box (`ZuneMotion.TitleScale`).</summary>
    public static readonly LayoutTransition PanoramaFold = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.TitleScaleMs, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>A mode crossing's arm cross-fade (the vertical arm ↔ the two-column arm).</summary>
    public static readonly EnterExit ArmFade = new(Opacity: 0f, Active: true);

    /// <summary>The arm cross-fade's timing.</summary>
    public static MotionTokenDef ArmFadeMotion => MotionTok.ControlFast;

    /// <summary>A Small (or narrower) page keeps its panorama in a short window down to this height (Z-Album-Small, 800 x 600); lower it folds.</summary>
    public const float SmallPanoramaMinH = 480f;

    /// <summary>The content width from which an episode's ring row (the primary, save, played, share and "...") stands on one line beside the primary: 75 + 4 x 40 +
    /// 4 x 16 inside two 16 gutters, rounded up.</summary>
    public const float EpisodeRingsOneLineFrom = 364f;

    /// <summary>Does an episode's ring row wrap (a content narrower than <see cref="EpisodeRingsOneLineFrom"/>)?</summary>
    public static bool EpisodeRingsWrap(in TierFacts facts) => facts.ContentWidth < EpisodeRingsOneLineFrom;

    /// <summary>The panorama's opacity (the boards' 0.92: the title sits a breath back from the ink).</summary>
    public const float PanoramaOpacity = 0.92f;

    /// <summary>The "songs" heading row over the list (Z-Album: 40), and the list's inset under it (the rows' own 8 already stand in).</summary>
    public const float HeadingRowH = 40f;

    /// <summary>The "songs" heading's side inset (A5): none, so the heading, the bar's first glyph and the header's hairline share the list's x.</summary>
    public const float SongsHeadingInset = 0f;

    /// <summary>The identity column's cover decode bucket (the shelf's: a warm texture on arrival) and its saturation.</summary>
    public const int CoverDecodePx = 512;

    /// <summary>The vertical hero's cover: 0.42 of the column inside its gutters, 96 to 144 (Z-Album-Compact 144 at 400).</summary>
    public const float HeroCoverMin = 96f, HeroCoverMax = 144f, HeroCoverFraction = 0.42f;

    /// <summary>The vertical hero's cover edge in a <paramref name="columnW"/>-wide column with <paramref name="gutter"/> each side.</summary>
    public static float HeroCoverFor(float columnW, float gutter)
        => MathF.Floor(Math.Clamp((columnW - 2f * gutter) * HeroCoverFraction, HeroCoverMin, HeroCoverMax));

    /// <summary>The vertical hero's rows: the gap beside the cover, the actions' air over them, the air under the actions and the description's.</summary>
    public const float HeroCoverGap = 16f, HeroActionsTop = 16f, HeroBottom = 12f, HeroIdentityGap = 6f, HeroDescriptionTop = 12f;

    /// <summary>The vertical hero's ring row writes its words (play, saved) from this inner measure; narrower, the rings stand alone so the row
    /// keeps its one line (Z-Album-Compact at 400 writes them; a 300 window does not have the room).</summary>
    public const float HeroWordsFrom = 340f;

    /// <summary>Does a <paramref name="columnW"/>-wide hero with <paramref name="gutter"/> each side write its ring words?</summary>
    public static bool HeroWords(float columnW, float gutter) => columnW - 2f * gutter >= HeroWordsFrom;

    /// <summary>The hero cover's resize: its bounds tween from the top-left (the page's anchor: no entrance, no morph).</summary>
    public static readonly LayoutTransition HeroGeometry = new(TransitionChannels.Bounds, TransitionDynamics.Tween(HeroGeometryMs, Easing.SmoothOut),
        SizeMode.ScaleCorrect);

    /// <summary>The hero's geometry tween.</summary>
    public const float HeroGeometryMs = 280f;

    /// <summary>The fallback column width before the table measured (the Compact board's).</summary>
    public const float HeroFallbackW = 400f;

    /// <summary>The description's clamp and line (13/20) in the vertical hero and the identity column.</summary>
    public const int DescriptionLines = 3;

    /// <summary>The description's line height.</summary>
    public const float DescriptionLine = 20f, DescriptionSize = 13f;

    /// <summary>The meta line's loading SHAPE (never painted as text: the deriver paints its bar).</summary>
    public const string MetaShimmerText = "00 songs · 0 hr 00 min";

    /// <summary>The meta line's run separator, and the no-break space that glues a run's own words.</summary>
    public const string MetaSeparator = " · ";

    /// <inheritdoc cref="MetaSeparator"/>
    public const char NoBreakSpace = '\u00A0';

    /// <summary>The runs of a meta line with each run's own spaces made no-break (the engine's line breaker breaks at a plain space only), so a meta
    /// that wraps breaks between its runs, never inside one: no orphan "min" (audit B, Playlist at 400). The words are unchanged (no casing, no data
    /// rewritten).</summary>
    public static string Glued(string meta)
    {
        var runs = meta.Split(MetaSeparator);
        for (int i = 0; i < runs.Length; i++) runs[i] = runs[i].Replace(' ', NoBreakSpace);
        return string.Join(MetaSeparator, runs);
    }

    /// <summary>The tags line under the meta (Z-Album-Compact: 12/16).</summary>
    public const float TagsLine = 16f;

    /// <summary>The Compact touch minimum: a link or a command in a Compact window is at least this tall (a smaller visual inside a box of it is fine).</summary>
    public const float CompactHit = 40f;

    /// <summary>The hit height of a link whose visual is <paramref name="line"/> tall in a <paramref name="window"/>-tier window: the line, or the Compact
    /// minimum.</summary>
    public static float HitHeight(WidthTier window, float line) => window == WidthTier.Compact ? MathF.Max(line, CompactHit) : line;

    /// <summary>The vertical hero's height: the panorama band, the cover row, the action row and its air, the description when present, the bottom
    /// air and the toolbar block (the core's list bar row and its pads). The skeleton reserves exactly this, and the loaded hero lays out these blocks
    /// at these heights, so the swap is a dissolve.</summary>
    public static float HeroHeight(ZuneDetailLayout lay, float cover, bool description, float toolbarBlock)
        => lay.PanoramaH + cover + HeroActionsTop + lay.RingSize
           + (description ? HeroDescriptionTop + DescriptionLines * DescriptionLine : 0f) + HeroBottom + toolbarBlock;

    // ── the rings (Z-Album: the 36 ring, 2 dip outline; the play ring a filled accent disc) ─────────────────────────

    /// <summary>The ring's outline.</summary>
    public const float RingStroke = 2f;

    // ── the identity column's rows ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The air between the actions and the release's courtesy lines (Z-Album: 12).</summary>
    public const float NotesTop = 12f;

    /// <summary>The short window's title row in the identity column: the heading voice's line (26/32), two lines at most.</summary>
    public const int RailTitleLines = 2;

    /// <summary>The skeleton bars: the artist bar's and the meta bar's share of the column, their radius.</summary>
    public const float SkelArtistFraction = 0.55f, SkelMetaFraction = 0.85f, SkelRadius = 2f;

    // ── the collapsed strip (a collapse stored by another theme: Zune draws no grip) ───────────────────────────────────

    /// <summary>The strip's padding and the expand target (40, the Compact minimum).</summary>
    public const float StripPad = 8f, StripTarget = 40f, StripChevron = 12f;

    // ── the show header (the vertical arm of a show or an episode) ──────────────────────────────────────────────────

    /// <summary>The header's cover (Z-Show-Compact: 120, 16 from the words), its padding and its rows' gap. THE SHORT FOLD (a window under 700 tall,
    /// 330 at the least): the panorama band eases to 0, the cover row, the ledger and the facts leave, and the header is the show's name over the ring
    /// row with 8 of air above and 8 between (about 96 tall), so the fixed header leaves the episodes the room a 126-tall page has.</summary>
    public const float ShowCover = 120f, ShowGap = 16f, ShowPadTop = 16f, ShowPadBottom = 8f, ShowRowsGap = 12f,
        ShowPadTopShort = 8f, ShowRowsGapShort = 8f;

    // ── the notice ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The notice strip's air above and below.</summary>
    public const float NoticePadTop = 8f, NoticePadBottom = 4f;

    // ── the trailing band and the aside ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A trailing section's air over its heading (32), its heading's gap to its body (14), the band's side inset (the rows' 8).</summary>
    public const float SectionTop = 32f, SectionGap = 14f, SectionInset = 8f, SectionBottom = 16f;

    /// <summary>The aside's sections' gap (Z-Album-Huge: 22 over each heading) and its heading row (40).</summary>
    public const float AsideSectionGap = 22f, AsideHeadingH = 40f, AsideRowGap = 10f;

    /// <summary>The aside's "more by" heading carries a NAME, so it takes the 22/28 step and may wrap to two lines (audit B, Album at 1440: the 26 heading
    /// ellipsised "More by Mia S..." in the 260 aside).</summary>
    public const float AsideNamedSize = 22f, AsideNamedLine = 28f;

    /// <summary>See <see cref="AsideNamedSize"/>.</summary>
    public const int AsideNamedLines = 2;

    /// <summary>The aside's artist card (Zune's own, flat): the words' inset under the image band, their gap, the name (20/28, two lines), the follow
    /// pill STACKED under the name (audit B: beside the name it ellipsised "Mia S..." at 260), the listeners and the bio (14/20, four lines).</summary>
    public const float AboutPadX = 20f, AboutPadTop = 16f, AboutPadBottom = 20f, AboutWordsGap = 6f, AboutNameSize = 20f, AboutNameLine = 28f,
        AboutFollowTop = 4f, AboutBodySize = 14f, AboutBodyLine = 20f, AboutCheck = 12f, AboutCheckTop = 8f, AboutImageDecode = 640f;

    /// <summary>See <see cref="AboutPadX"/>.</summary>
    public const int AboutNameLines = 2, AboutBioLines = 4;

    /// <summary>"More by" as rows (Z-Album: a 56 cover, 12 to its words) and as a grid (Z-Album-Huge: two columns 16 apart).</summary>
    public const float MoreByArt = 56f, MoreByArtGap = 12f, MoreByGridGap = 16f;

    /// <summary>"More by"'s grid columns.</summary>
    public const int MoreByColumns = 2;

    /// <summary>Fans also like in the aside: four circles a row.</summary>
    public const int FanColumns = 4;

    /// <summary>The gap between two fans and two fan rows.</summary>
    public const float FanGap = 16f;

    /// <summary>The trailing band's video hero: a 200 x 116 still, its play badge.</summary>
    public const float VideoThumbW = 200f, VideoThumbH = 116f, VideoFab = 40f, VideoGap = 16f;

    /// <summary>The video shelf's cards (16:9) and its fade.</summary>
    public const float VideoCardMinW = 220f, VideoCardMaxW = 320f, VideoAspect = 16f / 9f, ShelfFade = 24f;

    /// <summary>A merch row: its art, its height.</summary>
    public const float MerchArt = 48f, MerchRowH = 64f;

    /// <summary>A missing row's reserve (a related row whose slot is gone).</summary>
    public const float PlaceholderRowH = 64f;

    /// <summary>The fans row's card width in the band (the artist page's shelf width).</summary>
    public const float FanCardW = 148f;

    /// <summary>The trailing skeleton: a heading bar, a row bar, the gap between rows.</summary>
    public const float SkelHeadingW = 160f, SkelHeadingH = 20f, SkelRowH = 64f, SkelRowGap = 4f, SkelAboutH = 140f, SkelVideoH = 116f;

    /// <summary>The countdown's unit tiles: the numeral over its unit word, 12 apart.</summary>
    public const float CountdownGap = 12f, CountdownUnitGap = 2f, CountdownPad = 16f;

    /// <summary>The unresolvable route's line: its air.</summary>
    public const float UnresolvedPadX = 24f, UnresolvedPadY = 48f;

    /// <summary>The person flyout: its width, its row (44), the portrait (32), the list's height cap.</summary>
    public const float FlyoutW = 280f, FlyoutRowH = 44f, FlyoutPortrait = 32f, FlyoutMaxH = 360f, FlyoutPad = 8f, FlyoutRowGap = 12f;

    // ── the insights ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sheet's width (a third of the page, 320 to 400), its padding, the header row's gap and the close target (40).</summary>
    public const float SheetMinW = 320f, SheetMaxW = 400f, SheetFraction = 0.34f, SheetPad = 16f, SheetGap = 12f, SheetClose = 40f, SheetCloseGlyph = 12f;

    /// <summary>The sheet's width for a <paramref name="pageWidth"/>-wide page (NaN: the minimum). A page narrower than the minimum (a 300 window) is
    /// covered edge to edge: the sheet never runs past the page, and its × stays on screen.</summary>
    public static float SheetWidthFor(float pageWidth)
        => float.IsFinite(pageWidth) && pageWidth > 0f
            ? MathF.Min(pageWidth, Math.Clamp(MathF.Floor(pageWidth * SheetFraction), SheetMinW, SheetMaxW))
            : SheetMinW;

    /// <summary>The docked insights column (the head arm at Huge).</summary>
    public const float DockedInsightsW = 360f;

    /// <summary>The sheet's slide: 350 open, 120 close (the WinUI pane's).</summary>
    public const float SheetOpenMs = 350f, SheetCloseMs = 120f;

    /// <summary>The toggle's glyph edge (the bar's 32, the Compact 40 target from the record).</summary>
    public const float ToggleEdge = 32f, ToggleGlyph = 16f;

    // ── the records ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Compact (Z-Album-Compact): the vertical arm's numbers.</summary>
    public static ZuneDetailLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact, PanoramaH = 80f, PanoramaGap = 4f, PanoramaLarge = false, PanoramaNudgeX = -6f, PanoramaNudgeY = 4f,
        RailW = 0f, ColumnGap = 16f, AsideW = 0f, RailGap = 8f, RingSize = 40f, RingGlyph = 14f, RingLabelGap = 10f, ActionsGap = 16f,
        PlayWord = true, SaveWord = true, ShareRing = true, MoreRing = true, ShuffleRing = false, PrimaryInHero = true, MetaLines = 2,
        ArtistSize = 18f, ArtistLine = 24f, MetaSize = 13f, MetaLine = 18f, NotesSize = 12f, NotesLine = 18f, LabelSize = 14f,
        ArtistBand = 0f, MoreByGrid = false, Target = 40f,
    };

    /// <summary>Small (Z-Album-Small): the 160 identity column, 32 apart from the list. Play, shuffle and saved live in the band's row 2 from here up
    /// (audit A4: one home per verb), so the column keeps only share and "..." as unlabelled rings; the meta line may take two lines.</summary>
    public static ZuneDetailLayout Small { get; } = Compact with
    {
        Tier = WidthTier.Small, PanoramaNudgeX = -6f, RailW = 160f, ColumnGap = 32f, RingSize = 36f, RingGlyph = 13f, ActionsGap = 12f,
        PlayWord = false, SaveWord = false, ShareRing = true, PrimaryInHero = false, Target = 36f,
    };

    /// <summary>Standard (Z-Album): the panorama 120/130 (the role's own), the 240 identity column, the 260 aside, 48 apart.</summary>
    public static ZuneDetailLayout Standard { get; } = Small with
    {
        Tier = WidthTier.Standard, PanoramaH = 140f, PanoramaGap = 8f, PanoramaLarge = true, PanoramaNudgeX = -8f, PanoramaNudgeY = 2f,
        RailW = 240f, ColumnGap = 48f, AsideW = 260f, RailGap = 12f, ActionsGap = 18f, PlayWord = true, ShareRing = true, ArtistBand = 120f,
        MetaLines = 2, InsightsW = 320f,
    };

    /// <summary>Huge (Z-Album-Huge): the panorama 150/160, the 320 identity column, the 400 aside with its 140 artist band and "more by" as a grid.</summary>
    public static ZuneDetailLayout Huge { get; } = Standard with
    {
        Tier = WidthTier.Huge, PanoramaH = 176f, PanoramaGap = 12f, PanoramaSize = 150f, PanoramaLine = 160f, PanoramaNudgeX = -10f, PanoramaNudgeY = 4f,
        RailW = 320f, AsideW = 400f, SaveWord = true, ArtistBand = 140f, MoreByGrid = true, InsightsW = 420f,
    };

    /// <summary>Huge 4K (Z-Album-Huge4K): 400 identity, 440 aside, 64 apart, the 40 rings with shuffle, the 20/28 and 15/20 text.</summary>
    public static ZuneDetailLayout Huge4K { get; } = Huge with
    {
        Tier = WidthTier.Huge4K, PanoramaH = 176f, RailW = 400f, ColumnGap = 64f, AsideW = 440f, RailGap = 14f,
        RingSize = 40f, RingGlyph = 14f, RingLabelGap = 12f, ActionsGap = 20f, ShuffleRing = true,
        ArtistSize = 20f, ArtistLine = 28f, MetaSize = 15f, MetaLine = 20f, NotesSize = 13f, NotesLine = 20f, LabelSize = 15f,
        ArtistBand = 180f, Target = 40f, InsightsW = 440f,
    };
}

/// <summary>What Zune's detail views lay out in one content tier (see <see cref="ZuneDetailRules"/>' header).</summary>
public sealed record ZuneDetailLayout : ITierLayoutRecord
{
    /// <summary>The content tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The panorama band's reserved height: the title's line and its air over the columns (0: folded, a short window).</summary>
    public required float PanoramaH { get; init; }

    /// <summary>The air under the title's last line (8 at 64/72, 16 at 120/130, 24 at 150/160): the descender's room, and the board's gap to the columns.</summary>
    public required float PanoramaGap { get; init; }

    /// <summary>The panorama voice (120/130) rather than the compact one (64/72).</summary>
    public required bool PanoramaLarge { get; init; }

    /// <summary>The panorama's size and line where the board draws a step beyond the role's (150/160 at Huge); 0: the role's.</summary>
    public float PanoramaSize { get; init; }

    /// <summary>See <see cref="PanoramaSize"/>.</summary>
    public float PanoramaLine { get; init; }

    /// <summary>The title's optical offset from the gutter (the light face's side bearing) and from the band's top.</summary>
    public required float PanoramaNudgeX { get; init; }

    /// <summary>See <see cref="PanoramaNudgeX"/>.</summary>
    public required float PanoramaNudgeY { get; init; }

    /// <summary>The identity column's width and cover edge (0: the vertical arm).</summary>
    public required float RailW { get; init; }

    /// <summary>The gap between two columns.</summary>
    public required float ColumnGap { get; init; }

    /// <summary>The aside's width (0: no aside docks at this tier).</summary>
    public required float AsideW { get; init; }

    /// <summary>The identity column's row gap.</summary>
    public required float RailGap { get; init; }

    /// <summary>The ring's edge (40 at Compact: the touch minimum), its glyph, the gap to its word and between two rings.</summary>
    public required float RingSize { get; init; }

    /// <summary>See <see cref="RingSize"/>.</summary>
    public required float RingGlyph { get; init; }

    /// <summary>See <see cref="RingSize"/>.</summary>
    public required float RingLabelGap { get; init; }

    /// <summary>See <see cref="RingSize"/>.</summary>
    public required float ActionsGap { get; init; }

    /// <summary>Play and saved carry their words beside the ring.</summary>
    public required bool PlayWord { get; init; }

    /// <summary>See <see cref="PlayWord"/>.</summary>
    public required bool SaveWord { get; init; }

    /// <summary>The share ring, the "..." ring and the shuffle ring are drawn.</summary>
    public required bool ShareRing { get; init; }

    /// <summary>See <see cref="ShareRing"/>.</summary>
    public required bool MoreRing { get; init; }

    /// <summary>See <see cref="ShareRing"/>.</summary>
    public required bool ShuffleRing { get; init; }

    /// <summary>Play, saved and shuffle stand among the rings (audit A4). From Small up the band's row 2 is their one home, so the column and the hero
    /// keep only the verbs the band does not carry (share, "..."); Compact has no band actions and keeps them all.</summary>
    public required bool PrimaryInHero { get; init; }

    /// <summary>The meta line's lines in the identity column (2 at Compact and Small, where a 160 column would ellipsise it).</summary>
    public required int MetaLines { get; init; }

    /// <summary>The artist link (18/24), the meta line (13/18), the courtesy lines (12/18) and a ring's word (14).</summary>
    public required float ArtistSize { get; init; }

    /// <summary>See <see cref="ArtistSize"/>.</summary>
    public required float ArtistLine { get; init; }

    /// <summary>See <see cref="ArtistSize"/>.</summary>
    public required float MetaSize { get; init; }

    /// <summary>See <see cref="ArtistSize"/>.</summary>
    public required float MetaLine { get; init; }

    /// <summary>See <see cref="ArtistSize"/>.</summary>
    public required float NotesSize { get; init; }

    /// <summary>See <see cref="ArtistSize"/>.</summary>
    public required float NotesLine { get; init; }

    /// <summary>See <see cref="ArtistSize"/>.</summary>
    public required float LabelSize { get; init; }

    /// <summary>The aside's artist band (the about card's image) height.</summary>
    public required float ArtistBand { get; init; }

    /// <summary>"More by" in the aside is a two-column grid of covers (else rows with a 56 cover).</summary>
    public required bool MoreByGrid { get; init; }

    /// <summary>A command's hit edge (40 at Compact and 4K).</summary>
    public required float Target { get; init; }

    /// <summary>The identity column leads with the title (a short window folded the panorama).</summary>
    public bool RailTitle { get; init; }

    /// <summary>The lines the panorama's title may take (2): a name too long for one line even at the fit's floor breaks by whole words at the floor,
    /// the band hugging it (its reserved height a floor); past the last line, an ellipsis.</summary>
    public int PanoramaLines { get; init; } = 2;

    /// <summary>Does the panorama's title wrap by whole words (<see cref="PanoramaLines"/> over one)? A folded band never does.</summary>
    public bool PanoramaWraps => PanoramaLines > 1 && PanoramaH > 0f;

    /// <summary>The width of the aside column when it holds the insights alone (Liked Songs, a playlist without recommendations): 320 at Standard (the
    /// sheet's minimum, so the bento keeps the width it was drawn for), 420 at Huge (Z-Liked-Huge), 440 at Huge 4K; 0 where no aside docks. With a
    /// body in the aside the column is <see cref="AsideW"/>.</summary>
    public float InsightsW { get; init; }

    /// <summary>None: every region of the page fits at every size its tier lays out (the list yields, the slots ease).</summary>
    public ImmutableArray<TierRegion> Regions => ImmutableArray<TierRegion>.Empty;
}

/// <summary>The detail records per content tier, and the short window's fold.</summary>
public static class ZuneDetailTiers
{
    /// <summary>The record of content tier <paramref name="tier"/>.</summary>
    public static ZuneDetailLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => ZuneDetailRules.Compact,
        WidthTier.Small => ZuneDetailRules.Small,
        WidthTier.Standard => ZuneDetailRules.Standard,
        WidthTier.Huge => ZuneDetailRules.Huge,
        _ => ZuneDetailRules.Huge4K,
    };

    static readonly ZuneDetailLayout[] s_short =
    [
        Fold(ZuneDetailRules.Small),
        Fold(ZuneDetailRules.Small), Fold(ZuneDetailRules.Standard), Fold(ZuneDetailRules.Huge), Fold(ZuneDetailRules.Huge4K),
    ];

    static ZuneDetailLayout Fold(ZuneDetailLayout r) => r with { PanoramaH = 0f, RailTitle = true };

    /// <summary>The record the two-column arm and its pieces read for <paramref name="facts"/>: the content tier's, folded on a short window (the
    /// panorama gives its band to the rows; the identity column leads with the title). A Compact content tier reads Small's: the two-column arm
    /// exists there only inside the mode ladder's hold (the vertical arm, whose record is <see cref="Hero"/>, takes the rest). Standard's where no
    /// frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneDetailLayout Of(in TierFacts facts)
    {
        if (facts.Policy is null) return ZuneDetailRules.Standard;
        if (facts.Height == HeightTier.Short)
        {
            // Z-Album-Small is 800 x 600 (short) and keeps its 64/72 panorama: a Small page folds it only where the window is too low to carry it.
            if (facts.Content <= WidthTier.Small && facts.ViewportHeight >= ZuneDetailRules.SmallPanoramaMinH) return ZuneDetailRules.Small;
            return s_short[Math.Clamp((int)facts.Content, 0, s_short.Length - 1)];
        }
        return For(facts.Content == WidthTier.Compact ? WidthTier.Small : facts.Content);
    }

    /// <summary>The vertical hero's record: the Compact composition at any width it occurs (the vertical arm is the page column below 640).</summary>
    public static ZuneDetailLayout Hero => ZuneDetailRules.Compact;

    /// <summary>Do play, saved and shuffle stand in a hero or column drawn in <paramref name="facts"/>? The band's row 2 carries them by the WINDOW tier,
    /// so a vertical arm inside a Small window (a docked panel narrowed the page) leaves them to the band too. True where no frame resolved the tiers.</summary>
    public static bool PrimaryInHero(in TierFacts facts) => facts.Policy is null || For(facts.Window).PrimaryInHero;
}
