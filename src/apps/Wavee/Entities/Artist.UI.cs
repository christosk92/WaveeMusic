// ── Entities/Artist.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The artist page's LEAVES and its three CORE layout sections: ArtistHeroLayout and ArtistPopularLayout (ported VERBATIM
// from 0.2.9's Features/Detail/ArtistHeroLayout.cs / ArtistPopularLayout.cs, ch 08 §8) and ArtistSections (the page's
// new pure decisions: section order, pivot membership, the top band's wide latch, the chart's column clamp, the
// profile-facts list, the fans fallback, the page-failure verdict). Then the UI leaves: the full-bleed hero (HeroArt, the
// two presentation arms, identity, meta, actions), the artist pick and the gallery lightbox. The Top-tracks chart is the
// named partial Artist.UI.Chart.cs. The composition, the context band wiring, the shelves, the biography band and the
// three banner sections (upcoming, latest release, tour) are Artist.Page.cs — the banners moved there to keep this file
// and its chart partial inside +30 % of their shared 1,500 (1,797 together).
//
// Role: UI (+ CORE sections 1-3, engine-free except the Spacing constants)
// Owner: N (stream N-A)
// Wave: 5
// Budget: 1500 lines (together with Artist.UI.Chart.cs)
// Spec: ch 08 §8-§9
//
// ── WHY THE CORE LIVES IN A UI FILE ──────────────────────────────────────────────────────────────────────────────────
//
// Ch 08 §8 homes the two layout rules here, and plan §2 gives Artist.UI.cs "the two layout rules". They are top-level
// static classes with no component, no signal and no node — Wavee.Tests drives them directly (ArtistHeroLayoutTests,
// ArtistPopularLayoutTests, ArtistPageRulesTests).
//
// ── THE 0.2.9 INCONSISTENCY THIS FILE FIXES (ch 08 §9 "two real inconsistencies", #1) ─────────────────────────────────
//
// 0.2.9 sized the hero from the HYSTERETIC tier while the blend wash and the magazine gutter read the bare-width
// HeroHeightFor/PageGutterFor, so between 856 and 880 the hero was 440 tall with a 36 gutter while the wash was sized for
// 384 + 96. The page now resolves ONE ArtistHeroMetrics per render and hands it to the hero, the band, the wash
// (BlendBackdropHeightFor/BlendBoundaryFor(in metrics)) and the magazine gutter (metrics.Gutter). The width-only forms
// stay, verbatim, for their tests.
//
// NAME NOTE: inside `struct Artist` the simple names `Uri`, `Id`, `Name`, `Version`, `Pick`, `Latest` and `Links` are the
// handle's INSTANCE members — `System.Uri` is spelled in full and nothing here declares a nested type of those names.

using System.Globalization;
using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ══ 1. CORE — THE HERO LAYOUT (verbatim, ArtistHeroLayout.cs) ════════════════════════════════════════════════════════

public enum ArtistHeroTier : byte { Narrow, Compact, Medium, Wide }
public enum ArtistHeroVeilAxis : byte { Horizontal, Vertical }

public readonly record struct ArtistHeroMetrics(
    ArtistHeroTier Tier,
    float MinHeight,
    float Gutter,
    ArtistHeroVeilAxis VeilAxis,
    float CopyMaxWidth)
{
    public bool Stacked => VeilAxis == ArtistHeroVeilAxis.Vertical;
}

/// <summary>Responsive geometry for the full-bleed artist hero. The photo always fills the hero; pressure changes only
/// the copy placement and veil direction. Tier selection retains the detail layout's 24-DIP recovery band.</summary>
public static class ArtistHeroLayout
{
    // Lowered from 480/760/1040: the old thresholds dropped into the stacked presentation at ordinary desktop widths.
    public const float CompactWidth = 360f;
    public const float MediumWidth = 600f;
    public const float WideWidth = 880f;
    public const float TierHysteresis = 24f;

    public const float WideHeight = 440f;
    public const float MediumHeight = 384f;

    // Stacked (Compact/Narrow): the photograph is a FIELD at the top and the identity column sits BELOW it.
    public const float CompactPhotoHeight = 200f;
    public const float NarrowPhotoHeight = 176f;

    // The stacked identity band's WORST-CASE anatomy: verified caption + a 2-line title + a 2-line bio + meta (one row at
    // Compact, three stacked rows at Narrow) + gaps + 12/20 padding + actions (one 36 row at Compact, two at Narrow).
    public const float CompactExpandedIdentityHeight = 252f;
    public const float NarrowExpandedIdentityHeight = 300f;
    public const float CompactHeight = CompactPhotoHeight + CompactExpandedIdentityHeight;
    public const float NarrowHeight = NarrowPhotoHeight + NarrowExpandedIdentityHeight;

    /// <summary>The photograph's own extent: the full hero on horizontal tiers, the top slice on stacked tiers. Both the
    /// banner's media box and <c>HeroArt</c> derive from THIS, so they cannot disagree.</summary>
    public static float PhotoHeightFor(in ArtistHeroMetrics m) => !m.Stacked ? m.MinHeight
        : m.Tier == ArtistHeroTier.Compact ? CompactPhotoHeight
        : NarrowPhotoHeight;

    public const float WideCopyMaxWidth = 1120f;
    public const float MediumCopyMaxWidth = 760f;
    public const float CompactCopyMaxWidth = 640f;
    public const float NarrowCopyMaxWidth = 520f;
    public const float PhotoParallaxFraction = 0.15f;
    public const float ContentBlendTail = Spacing.XXXL * 3f;
    public const float CompactIdentityHeight = Detail.VerticalLayout.CompactIdentityHeight;

    public static ArtistHeroTier TierFor(float width, ArtistHeroTier previous)
    {
        if (previous == ArtistHeroTier.Wide && width >= WideWidth - TierHysteresis) return previous;
        if (previous == ArtistHeroTier.Medium && width >= MediumWidth - TierHysteresis && width < WideWidth + TierHysteresis) return previous;
        if (previous == ArtistHeroTier.Compact && width >= CompactWidth - TierHysteresis && width < MediumWidth + TierHysteresis) return previous;

        if (width >= WideWidth + ((byte)previous < (byte)ArtistHeroTier.Wide ? TierHysteresis : 0f)) return ArtistHeroTier.Wide;
        if (width >= MediumWidth + ((byte)previous < (byte)ArtistHeroTier.Medium ? TierHysteresis : 0f)) return ArtistHeroTier.Medium;
        if (width >= CompactWidth + ((byte)previous < (byte)ArtistHeroTier.Compact ? TierHysteresis : 0f)) return ArtistHeroTier.Compact;
        return ArtistHeroTier.Narrow;
    }

    public static ArtistHeroMetrics For(float width, ArtistHeroTier previous)
    {
        var tier = TierFor(width, previous);
        return tier switch
        {
            ArtistHeroTier.Wide => new(tier, WideHeight, Spacing.PageWide, ArtistHeroVeilAxis.Horizontal, WideCopyMaxWidth),
            ArtistHeroTier.Medium => new(tier, MediumHeight, Spacing.XXXL, ArtistHeroVeilAxis.Horizontal, MediumCopyMaxWidth),
            ArtistHeroTier.Compact => new(tier, CompactHeight, Spacing.L, ArtistHeroVeilAxis.Vertical, CompactCopyMaxWidth),
            _ => new(tier, NarrowHeight, Spacing.PageNarrow, ArtistHeroVeilAxis.Vertical, NarrowCopyMaxWidth),
        };
    }

    public static float PageGutterFor(float width) => width >= WideWidth ? Spacing.PageWide
        : width >= MediumWidth ? Spacing.XXXL
        : width >= CompactWidth ? Spacing.L
        : Spacing.PageNarrow;

    public static float PhotoFadeBandFor(float height) => Math.Clamp(height * 0.28f, 120f, 180f);
    public static float CollapseDistance(float height) => MathF.Max(1f, height - CompactIdentityHeight);
    public static float ExpandedFadeStart(float collapseDistance) => Detail.VerticalLayout.ExpandedFadeStart(collapseDistance);
    public static float CompactRevealStart(float collapseDistance) => Detail.VerticalLayout.CompactRevealStart(collapseDistance);

    public static float HeroHeightFor(float width) => width >= WideWidth ? WideHeight
        : width >= MediumWidth ? MediumHeight
        : width >= CompactWidth ? CompactHeight
        : NarrowHeight;
    public static float BlendBackdropHeightFor(float width) => HeroHeightFor(width) + ContentBlendTail;
    public static float BlendBoundaryFor(float width)
    {
        float height = HeroHeightFor(width);
        return height / (height + ContentBlendTail);
    }

    // ── 0.3: the resolved-metrics forms (ch 08 §9 inconsistency #1) ──

    /// <summary>The wash's extent from the RESOLVED (hysteretic) metrics the hero itself is sized from.</summary>
    public static float BlendBackdropHeightFor(in ArtistHeroMetrics m) => m.MinHeight + ContentBlendTail;

    /// <summary>The wash's boundary stop, <c>h/(h+96)</c>, from the resolved metrics.</summary>
    public static float BlendBoundaryFor(in ArtistHeroMetrics m) => m.MinHeight / (m.MinHeight + ContentBlendTail);
}

// ══ 2. CORE — THE CHART ROW TIER (verbatim, ArtistPopularLayout.cs) ═══════════════════════════════════════════════════

/// <summary>The chart row's geometry tier (artwork size / duration visibility / subtitle stacking), decided from the
/// shelf's fitted column width with one-directional 24-DIP hysteresis: narrow immediately, re-admit the wider reading only
/// once the width clears the breakpoint by <see cref="HysteresisDip"/>. The play-count FORMAT is deliberately not part of
/// the tier (the row always renders the compact form with the exact count in a tooltip), and the tier travels as a
/// re-pushed PROP, never a Key (S2 audit #8).</summary>
public static class ArtistPopularLayout
{
    public const float HysteresisDip = 24f;

    public const float ArtBreakW = 220f;       // < this: 40px art (Modern only — Classic art is fixed at 40px)
    public const float DurationBreakW = 200f;  // < this: the duration cell is dropped
    public const float StackBreakW = 340f;     // < this: the subtitle stacks onto its own 3rd line (Modern only)

    public readonly record struct Tier(float Art, bool ShowDuration, bool StackSub)
    {
        public static Tier Classic(bool showDuration) => new(40f, showDuration, false);
    }

    /// <summary>Bare thresholds, no memory of a previous decision.</summary>
    public static Tier NominalFor(float cellW, bool classic) => classic
        ? Tier.Classic(cellW >= DurationBreakW)
        : new Tier(cellW >= ArtBreakW ? 44f : 40f, cellW >= DurationBreakW, cellW < StackBreakW);

    /// <summary>The stateful decision. <paramref name="previous"/> null (mount, or a classic/modern toggle) takes
    /// <see cref="NominalFor"/> outright.</summary>
    public static Tier Decide(float cellW, bool classic, Tier? previous)
    {
        if (previous is not { } p) return NominalFor(cellW, classic);
        bool showDuration = Admit(cellW, DurationBreakW, p.ShowDuration);
        if (classic) return Tier.Classic(showDuration);
        float art = Admit(cellW, ArtBreakW, p.Art >= 44f) ? 44f : 40f;
        bool stackSub = !Admit(cellW, StackBreakW, !p.StackSub);
        return new Tier(art, showDuration, stackSub);
    }

    static bool Admit(float cellW, float breakpoint, bool currentlyWide)
        => cellW >= (currentlyWide ? breakpoint : breakpoint + HysteresisDip);
}

// ══ 3. CORE — THE PAGE'S OWN DECISIONS (new in 0.3, ArtistPageRulesTests) ════════════════════════════════════════════

/// <summary>Every section the artist page can render, in the ONE fixed order (ArtistPage.cs:234-269).</summary>
public enum ArtistSection : byte
{
    Popular, Upcoming, LatestRelease, Albums, Singles, Compilations, AppearsOn, Tour, MusicVideos, Playlists,
    Concerts, Merch, Biography, Gallery, Related, Fans,
}

/// <summary>What the page knows is present, read once per render off the handle and the edges.</summary>
public readonly record struct ArtistPageFacts(
    bool Popular, bool Pick, bool Upcoming, bool LatestRelease, bool Albums, bool Singles, bool Compilations,
    bool AppearsOn, bool Tour, bool MusicVideos, bool Playlists, bool Concerts, bool Merch, bool Gallery,
    bool Related, bool Fans);

/// <summary>One profile-facts tile (W18): which stat, and its value.</summary>
public enum ArtistFactKind : byte { Monthly, Followers, Albums, Singles, Concerts, Related }

/// <inheritdoc cref="ArtistFactKind"/>
public readonly record struct ArtistFact(ArtistFactKind Kind, long Value);

/// <summary>The artist page's pure decisions (ch 08 §1.1, §2, W18-W19). Each was a branch inside 0.2.9's
/// <c>ArtistPage.Body</c>; here each is one assertion.</summary>
public static class ArtistSections
{
    public const int Count = 16;

    /// <summary>Both <c>Responsive.Of</c> call sites decide their first composed frame at 900, not the real pane
    /// (ch 08 breakpoint table "Pre-measure width", parity 90).</summary>
    public const float PreMeasureWidth = 900f;

    /// <summary>The Top-tracks band goes side by side at 760 and holds down to 736 once wide (TopTracks.cs:17-27).</summary>
    public const float TopBandWideW = 760f, TopBandHysteresis = 24f;

    /// <summary>The chart: never taller than five rows, never more than two columns, never more than 50 tracks.</summary>
    public const int ChartMaxRows = 5, ChartMaxColumns = 2, ChartMaxTracks = 50;

    /// <summary>"Fans also like" falls back to this many followed artists, excluding the page artist.</summary>
    public const int FansCap = 12;

    /// <summary>The shelf caps (Shelves.cs): videos, playlists and gallery 16; concerts and merch 12.</summary>
    public const int AppearsOnCap = 16;
    public const int VideoCap = 16, PlaylistCap = 16, ConcertCap = 12, MerchCap = 12, GalleryCap = 16;

    /// <summary>The inline facet's reveal inset: the band (56) plus the pinned facet header (40).</summary>
    public const float FacetExpandedTopInset = 96f;

    static readonly string[] s_keys =
    [
        "popular", "upcoming", "latest-release", "albums", "singles", "compilations", "appears-on", "tour",
        "music-videos", "playlists", "concerts", "merch", "biography", "gallery", "related", "fans",
    ];

    /// <summary>The section's stable key. <c>related</c> and <c>fans</c> are deliberately DIFFERENT (they are
    /// alternatives holding different data; one key would reuse the shelf subtree across the swap).</summary>
    public static string Key(ArtistSection s) => s_keys[(int)s];

    /// <summary>Only a DESTINATION joins the pivot; the upcoming band and the two banners are announcements. Popular
    /// is excluded too: it has no "Overview" tab of its own any more — the band title itself scrolls back to it
    /// (ArtistPage.cs's <c>ScrollToTop</c>).</summary>
    public static bool IsDestination(ArtistSection s)
        => s is not (ArtistSection.Upcoming or ArtistSection.LatestRelease or ArtistSection.Tour or ArtistSection.Popular);

    /// <summary>Everything past Compilations that collapses into the single "Details" tab (ArtistPage.cs's Compose):
    /// the caller marks only the first PRESENT member of this group a destination each render — Biography is
    /// unconditional (<see cref="Plan"/>), so Details always has somewhere to point.</summary>
    public static bool IsDetailsGroup(ArtistSection s)
        => s is ArtistSection.AppearsOn or ArtistSection.MusicVideos or ArtistSection.Playlists
            or ArtistSection.Concerts or ArtistSection.Merch or ArtistSection.Biography or ArtistSection.Gallery
            or ArtistSection.Related or ArtistSection.Fans;

    /// <summary>The pick owns the rail beside Top tracks; an upcoming release then takes its OWN band above Latest
    /// release. Without Top tracks there is no rail and neither renders (ArtistPage.cs:241).</summary>
    public static bool UpcomingBand(bool pick, bool popular, bool upcoming) => pick && popular && upcoming;

    /// <summary>With no pick, the upcoming card is the rail.</summary>
    public static bool UpcomingInRail(bool pick, bool upcoming) => !pick && upcoming;

    /// <summary>The page's sections, in order, into <paramref name="into"/> (≥ <see cref="Count"/>). Biography is
    /// unconditional (W24: the minimum page is hero + divider + Biography).</summary>
    public static int Plan(in ArtistPageFacts f, Span<ArtistSection> into)
    {
        int n = 0;
        if (f.Popular) into[n++] = ArtistSection.Popular;
        if (UpcomingBand(f.Pick, f.Popular, f.Upcoming)) into[n++] = ArtistSection.Upcoming;
        if (f.LatestRelease) into[n++] = ArtistSection.LatestRelease;
        if (f.Albums) into[n++] = ArtistSection.Albums;
        if (f.Singles) into[n++] = ArtistSection.Singles;
        if (f.Compilations) into[n++] = ArtistSection.Compilations;
        if (f.AppearsOn) into[n++] = ArtistSection.AppearsOn;
        if (f.Tour) into[n++] = ArtistSection.Tour;
        if (f.MusicVideos) into[n++] = ArtistSection.MusicVideos;
        if (f.Playlists) into[n++] = ArtistSection.Playlists;
        if (f.Concerts) into[n++] = ArtistSection.Concerts;
        if (f.Merch) into[n++] = ArtistSection.Merch;
        into[n++] = ArtistSection.Biography;
        if (f.Gallery) into[n++] = ArtistSection.Gallery;
        if (f.Related) into[n++] = ArtistSection.Related;
        else if (f.Fans) into[n++] = ArtistSection.Fans;
        return n;
    }

    /// <summary>The upcoming card's live countdown shows only inside two weeks; further out the big date alone speaks
    /// (TopTracks.cs:172-180). Unix seconds.</summary>
    public const long CountdownWindowSeconds = 14L * 86_400L;

    /// <inheritdoc cref="CountdownWindowSeconds"/>
    public static bool ShowsCountdown(long releaseAtUnix, long nowUnix)
        => releaseAtUnix > nowUnix && releaseAtUnix - nowUnix <= CountdownWindowSeconds;

    /// <summary>The top band's latched wide/stacked decision.</summary>
    public static bool TopBandWide(float width, bool wasWide)
        => wasWide ? width >= TopBandWideW - TopBandHysteresis : width >= TopBandWideW;

    /// <summary>Never offer more columns than the rows can fill: a ≤5-track chart is ONE column at any width
    /// (ArtistPopular.cs:138, ch 08 audit #4).</summary>
    public static int ChartColumns(int total)
        => Math.Clamp((total + ChartMaxRows - 1) / ChartMaxRows, 1, ChartMaxColumns);

    /// <summary>The profile-facts tiles in their fixed order, a zero stat dropping its WHOLE tile (never "0 Albums").
    /// Returns the count written into <paramref name="into"/> (≥ 6). Zero tiles ⇒ the biography takes the full width.</summary>
    public static int FactTiles(long monthly, long followers, long albums, long singles, long concerts, long related,
                                Span<ArtistFact> into)
    {
        int n = 0;
        if (monthly > 0) into[n++] = new ArtistFact(ArtistFactKind.Monthly, monthly);
        if (followers > 0) into[n++] = new ArtistFact(ArtistFactKind.Followers, followers);
        if (albums > 0) into[n++] = new ArtistFact(ArtistFactKind.Albums, albums);
        if (singles > 0) into[n++] = new ArtistFact(ArtistFactKind.Singles, singles);
        if (concerts > 0) into[n++] = new ArtistFact(ArtistFactKind.Concerts, concerts);
        if (related > 0) into[n++] = new ArtistFact(ArtistFactKind.Related, related);
        return n;
    }

    /// <summary>The "Fans also like" fallback pool: the followed artists in order, excluding the page artist, up to
    /// <see cref="FansCap"/> (ch 08 GAP 19). Returns the count written.</summary>
    public static int Fans(ReadOnlySpan<int> followed, int self, Span<int> into)
    {
        int n = 0;
        int cap = Math.Min(FansCap, into.Length);
        for (int i = 0; i < followed.Length && n < cap; i++)
        {
            int slot = followed[i];
            if (slot <= 0 || slot == self) continue;
            into[n++] = slot;
        }
        return n;
    }

    /// <summary>W23, with 0.3's Retry: the page is FAILED when the overview is not known, nobody is still asking for
    /// it, and the overview transport's own relation (the chart edge rides the same pathfinder answer) reports a
    /// failure. A pending ask, or a chart that has not failed, keeps the skeleton.</summary>
    public static bool PageFailed(bool overviewKnown, bool overviewPending, EdgeState chartReadiness)
        => !overviewKnown && !overviewPending && chartReadiness == EdgeState.Failed;
}

// ══ 4. THE LEAVES ═════════════════════════════════════════════════════════════════════════════════════════════════════

public readonly partial struct Artist
{
    // ── 4.1 the hero (ch 08 W1-W4b, §3 hero rows, §5 hero motion) ────────────────────────────────────────────────────

    /// <summary>What the hero STATES, resolved once per page render. <see cref="Placeholder"/> is the representative
    /// shape the page's derived skeleton is built from — its words are never shown (the deriver paints bars in their
    /// place), so the shimmer carries the loaded hero's exact geometry (ch 08 §0 #15, W5).</summary>
    internal readonly record struct HeroText(string Name, string Lead, bool Verified, ushort Rank, uint Monthly, uint Followers)
    {
        public static HeroText For(Artist a) => new(a.Name, Entities.Strings.Resolve(a.BioLeadId), a.IsVerified,
            a.WorldRank, a.MonthlyListeners, a.Followers);

        public static HeroText Placeholder => new("Artist name", "A first sentence of the biography stands in this line.",
            Verified: true, Rank: 100, Monthly: 10_000_000, Followers: 10_000_000);
    }

    /// <summary>The full-bleed hero and its collapse into the 56-DIP band (ArtistPage.Hero.cs:22-198). Horizontal tiers
    /// are a ZStack (photo · veil · copy centred); stacked tiers are a column (photo band · identity justified End) —
    /// two compositions, never one with a flag (ch 08 §9 #1). A null <paramref name="play"/> is the skeleton arm: no
    /// veil leaf, no band, the actions as their shapes.</summary>
    internal static Element HeroBanner(in HeroText text, string uri, string? photoUrl, string? paletteUrl, float width,
                                       in ArtistHeroMetrics m, ColorF accent, bool compactCanHit,
                                       Action? play, Action? shuffle, Action? radio, Element? band,
                                       uint headerAccent = 0)
    {
        float w = MathF.Max(1f, width);
        float height = m.MinHeight;
        float collapse = ArtistHeroLayout.CollapseDistance(height);
        float photoH = ArtistHeroLayout.PhotoHeightFor(m);

        // W4b: no url ⇒ HeroArt is NOT mounted; the flat theme neutral stands in (never a cover tint — ch 08 §4 row 6b).
        // KEYED ON THE ARTIST, NOT THE URL (ch 08 BUG E #2): the caller already latches which art identity is showing
        // (Detail.CoverLatch.PreferVisible), so the url itself still changes CDN size/rendition across a render — a
        // key on `src` would remount HeroArt (and its zoom/settle state) on every one of those, undoing the latch. The
        // url instead reaches the mounted instance live as a re-pushed prop (HeroArtProps + UseProps), exactly like the
        // wash and veil below.
        // ch 08 BUG F: WATCHED, not the raw `Design.ArtworkPlaceholder` ColorF — the same frozen-placeholder defect as
        // PersonPicture's fill (Artist.UI.cs's pick avatar, below). No url here means no tint EITHER way (the "never a
        // cover tint" rule above still holds — Design.WatchedPlaceholder(null) resolves to the same flat neutral), but
        // it is now a live-bound Prop like every other art slot's placeholder instead of a value baked in at this
        // render, so a future caller of this arm with a real (still-ungrading) url repaints instead of staying grey.
        Element art = photoUrl is { Length: > 0 } src
            ? Embed.Comp(new HeroArtProps(src, w, photoH), static () => new HeroArt()) with { Key = "heroart:" + uri }
            : new BoxEl { Width = w, Height = photoH, Fill = Design.WatchedPlaceholder(photoUrl) };
        Element media = new BoxEl
        {
            Width = w, Height = photoH, ZStack = true, ClipToBounds = true,
            TransformOriginX = 0.5f, TransformOriginY = 0f,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, ArtistHeroLayout.PhotoFadeBandFor(photoH)),
            Children = [art],
        }.StretchFromTop().ParallaxY(ArtistHeroLayout.PhotoParallaxFraction, photoH);

        // Shared by both arms: the expanded presentation slides up and fades as the band takes over.
        ScrollBindDsl[] collapseBinds =
        [
            new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                Range = ScrollRange.Px(0f, collapse), OutStart = 0f, OutEnd = -collapse, Ease = Easing.Linear },
            new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse),
                OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
        ];

        Element identity = HeroIdentity(in text, uri, w, in m, accent, play, shuffle, radio);
        Element expanded;
        if (m.Stacked)
        {
            // No veil: no type sits on the photograph on these tiers (ch 08 §0 #2).
            expanded = new BoxEl
            {
                Width = w, Height = height, Direction = 1,
                HitTestVisible = !compactCanHit, ScrollBinds = collapseBinds,
                Children =
                [
                    media,
                    new BoxEl
                    {
                        Grow = 1f, MinHeight = 0f, Direction = 1,
                        Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                        Padding = new Edges4(m.Gutter, Spacing.M, m.Gutter, Spacing.XL),
                        Children = [identity],
                    },
                ],
            };
        }
        else
        {
            Element copy = new BoxEl
            {
                Width = w, Height = height, Direction = 1,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Start,
                Padding = new Edges4(m.Gutter, Spacing.XXL, m.Gutter, Spacing.XXL),
                Children = [identity],
            };
            // A cover-keyed leaf: a late grading swaps the gradient without rebuilding the banner (ch 08 §4 row 3).
            // headerAccent (row.HeaderAccent) is the raw payload rung: the caller passes it as an optional param so
            // this stays source-compatible until Artist.Page.cs's two HeroBanner call sites are wired to pass it.
            Element veil = play is null
                ? new BoxEl()
                : Palette.ArtistHeroVeil(paletteUrl, vertical: false, w, height, key: "artist-veil:" + uri,
                                          payloadAccent: headerAccent);
            expanded = new BoxEl
            {
                Width = w, Height = height, ZStack = true,
                HitTestVisible = !compactCanHit, ScrollBinds = collapseBinds,
                Children = [media, veil, copy],
            };
        }

        return new BoxEl
        {
            Direction = 1, Height = height, ClipToBounds = true, ZStack = true,
            Children = band is null ? [expanded] : [expanded, band],
        }.Collapse(height, ArtistHeroLayout.CompactIdentityHeight, collapse);
    }

    /// <summary>Verified · name · first sentence · meta, then the action row (Hero.cs:34-95).</summary>
    static Element HeroIdentity(in HeroText text, string uri, float w, in ArtistHeroMetrics m, ColorF accent,
                                Action? play, Action? shuffle, Action? radio)
    {
        Element verified = text.Verified
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children =
                [
                    InfoBadge.Icon(Icons.Accept, color: accent),
                    Caption(Loc.Get(Strings.Artist.Verified)) with { Color = Tok.TextSecondary },
                ],
            }
            : new BoxEl();

        TextEl name = m.Tier switch
        {
            ArtistHeroTier.Wide => Design.Type.ArtistDisplay(text.Name),
            ArtistHeroTier.Medium => Design.Type.ArtistTitle(text.Name),
            _ => Design.Type.ArtistCompactTitle(text.Name),
        };
        name = name with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, MinWidth = 0f };

        // The lead is a COMMIT-computed column (ch 08 GAP 3): the hero never strips HTML on a render.
        Element bio = text.Lead.Length == 0
            ? new BoxEl()
            : Body(text.Lead) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            };

        return new BoxEl
        {
            Direction = 1,
            Width = MathF.Min(m.CopyMaxWidth, MathF.Max(1f, w - 2f * m.Gutter)),
            MaxWidth = m.CopyMaxWidth, MinWidth = 0f, Gap = Spacing.S,
            // The page-level SkelRegion owns the single reveal. A second hero-copy entrance here made the
            // already-revealed page flash and move for another frame when the overview landed.
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                    // Meta stays ONE horizontal row at Compact; only Narrow stacks it.
                    Children = [verified, name, bio, HeroMeta(in text, m.Tier == ArtistHeroTier.Narrow)],
                },
                HeroActions(text.Name, uri, accent, play, shuffle, radio, m.Tier),
            ],
        };
    }

    /// <summary>"#N in the world" (accent) · monthly listeners · followers — a zero stat drops its run (Hero.cs:236-259).</summary>
    static Element HeroMeta(in HeroText text, bool stacked)
    {
        int count = (text.Rank > 0 ? 1 : 0) + (text.Monthly > 0 ? 1 : 0) + (text.Followers > 0 ? 1 : 0);
        var kids = new Element[count];
        int k = 0;
        if (text.Rank > 0)
            kids[k++] = BodyStrong(Strings.Artist.WorldRank(text.Rank.ToString(CultureInfo.CurrentCulture)))
                with { Color = Tok.AccentTextPrimary };
        if (text.Monthly > 0)
            kids[k++] = Body(CountLabel(text.Monthly) + " " + Loc.Get(Strings.Artist.MetaMonthly)) with { Color = Tok.TextSecondary };
        if (text.Followers > 0)
            kids[k++] = Body(CountLabel(text.Followers) + " " + Loc.Get(Strings.Artist.MetaFollowers)) with { Color = Tok.TextSecondary };
        return new BoxEl
        {
            Direction = (byte)(stacked ? 1 : 0),
            AlignItems = stacked ? FlexAlign.Start : FlexAlign.Center,
            Gap = stacked ? Spacing.XS : Spacing.L,
            MinWidth = 0f,
            Children = kids,
        };
    }

    /// <summary>Play (the cover's accent) · Shuffle · Follow · Radio; at Narrow [Play, Follow] over [Shuffle, Radio]
    /// (Hero.cs:200-234). Never skeleton content (ch 08 §9 #8).</summary>
    static Element HeroActions(string name, string uri, ColorF accent, Action? play, Action? shuffle, Action? radio,
                               ArtistHeroTier tier)
    {
        Element playButton = Controls.Play(accent, play ?? s_noop, Loc.Get(Strings.Artist.Play));
        // Keyed on the uri: FollowButton carries its uri as a mount-frozen field.
        Element follow = uri.Length == 0
            ? Controls.FollowButton.SkeletonShape()
            : Embed.Comp(() => new Controls.FollowButton { Uri = uri, Name = name })
                with { Key = "artist-follow:" + uri, SkeletonProxy = s_followShape };
        Element shuffleButton = Controls.Named(
            Controls.IconPill(Icons.Shuffle, shuffle, ButtonAppearance.Subtle), Loc.Get(Strings.Detail.Shuffle));
        Element radioButton = Controls.Named(
            Controls.IconPill(Icons.RadioTower, radio, ButtonAppearance.Subtle), Loc.Get(Strings.Artist.ArtistRadio));

        if (tier == ArtistHeroTier.Narrow)
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [playButton, follow] },
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [shuffleButton, radioButton] },
                ],
            }.Skeletonized(false);

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = [playButton, shuffleButton, follow, radioButton],
        }.Skeletonized(false);
    }

    static readonly Func<Element> s_followShape = Controls.FollowButton.SkeletonShape;
    static readonly Action s_noop = static () => { };

    /// <summary>A count in the current culture's grouping ("20,577,457").</summary>
    internal static string CountLabel(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    sealed record HeroArtProps(string Url, float Width, float PhotoHeight);

    /// <summary>The photograph: one gentle image-only entrance settle. The page keeps its shimmer until this exact
    /// image is resident, so the photo never fades over a flat hero after the rest of the page has appeared. The scale
    /// is owned by this keyed component and runs once per artist; it never restarts when a CDN rendition or window
    /// measurement changes. The decode size is latched at the first measured width so a resize cannot create a visible
    /// resize flash.</summary>
    sealed class HeroArt : Component
    {
        const float StartScale = 1f, RestScale = 1.03f, FrameScale = 1.02f, FrameLiftFraction = 0.02f;
        static readonly Keyframe[] s_enterScale =
            [new(0f, StartScale, Easing.Linear), new(1f, RestScale, Easing.FluentDecelerate)];
        static readonly Keyframe[] s_enterOpacity = [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.FluentDecelerate)];
        static readonly Keyframe[] s_restOpacity = [new(0f, 1f), new(1f, 1f)];

        public override Element Render()
        {
            var p = UseProps<HeroArtProps>();
            float width = MathF.Max(1f, p.Width);
            float height = MathF.Max(1f, p.PhotoHeight);

            var decode = UseRef((0, 0));
            if (decode.Value.Item1 <= 0 && width > 1f)
            {
                int decodeW = Math.Clamp((int)MathF.Round(width), 320, 1920);
                int decodeH = Math.Max(1, (int)MathF.Round(decodeW * (height / width)));
                decode.Value = (decodeW, decodeH);
            }
            int baseW = decode.Value.Item1 > 0 ? decode.Value.Item1 : (int)width;
            int baseH = decode.Value.Item2 > 0 ? decode.Value.Item2 : Math.Max(1, (int)height);
            // The zoom scale still reaches the decode budget (recomputed per render, outside the resize latch).
            float scale = UseContext(Viewport.Scale);
            int dw = Design.ImageDecodeScale.For(baseW, scale);
            int dh = Math.Max(1, Design.ImageDecodeScale.For(baseH, scale));
            float aspect = (float)dw / dh;

            // THE HERO OWNS ITS OWN ENTRANCE, and the page no longer waits for it (`ArtistReadiness.BodyReady`): the
            // copy, the chart and the rails reveal as soon as THEY are ready, and the photograph scales up from its
            // 1.00 start to its 1.03 rest and fades 0 -> 1 when its bitmap actually lands, however long that takes.
            //
            // This is what the page-wide wait was standing in for, badly. Blocking the whole reveal on the decode
            // meant a slow photo held an otherwise fully-known page at a shimmer; and when it did land, the page
            // reveal and the image fade were two animations over the same pixels (stillwrong.mp4). One owner, one
            // entrance, keyed on the ready EDGE so it plays once per photo and never re-runs on a plain re-render.
            var image = UseImage(p.Url, dw, dh, ImagePriority.Visible, blurHash: null, transition: ImageTransition.None);
            bool ready = image.State is ImageState.Ready or ImageState.Failed;
            var zoom = UseRef(false);
            if (!zoom.Value && ready) zoom.Value = true;
            UseKeyframes(AnimChannel.ScaleX, zoom.Value ? s_enterScale : s_restScale,
                zoom.Value ? MotionTok.EmphasizedEnter.DurationMs : MotionTok.ControlFaster.DurationMs,
                loop: false, DepKey.From(zoom.Value));
            UseKeyframes(AnimChannel.ScaleY, zoom.Value ? s_enterScale : s_restScale,
                zoom.Value ? MotionTok.EmphasizedEnter.DurationMs : MotionTok.ControlFaster.DurationMs,
                loop: false, DepKey.From(zoom.Value));
            // Same ready edge as the scale settle. Reduced motion is a duration of 0, never a skipped call.
            float enterOpacityMs = Design.Reduced ? 0f : Design.Motion.Standard;
            UseKeyframes(AnimChannel.Opacity, zoom.Value ? s_enterOpacity : s_restOpacity,
                zoom.Value ? enterOpacityMs : MotionTok.ControlFaster.DurationMs,
                loop: false, DepKey.From(zoom.Value));

            return new BoxEl
            {
                Width = width, Height = height, ZStack = true, ScaleX = 1f, ScaleY = 1f,
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, ScaleX = FrameScale, ScaleY = FrameScale, OffsetY = -height * FrameLiftFraction,
                        Children =
                        [
                            // The FLAT theme neutral, never a cover tint: a 440-DIP tinted slab is a different design.
                            Ui.Image(p.Url, ImageFit.Cover, aspect: aspect, decodePx: dw, corners: 0f,
                                     placeholder: Design.ArtworkPlaceholder, blurHash: null, transition: ImageTransition.None)
                                with { FocusX = 0.62f, FocusY = 0.34f },
                        ],
                    },
                ],
            };
        }

        static readonly Keyframe[] s_restScale = [new(0f, RestScale), new(1f, RestScale)];
    }

    // ── 4.2 the artist pick (0.2.9 MediaCard.ArtistPick, MediaCard.cs:299-471) ──────────────────────────────────────

    const float PickPhotoHeight = 150f;    // the rail arm's photograph band; the column supplies the width
    const float PickPhotoColumnW = 300f;   // the band arm: photography as a right column, stretched to the panel

    /// <summary>The artist-authored pinned item as ONE tone panel: accent wash, the artist speaking, an optional wide
    /// photograph (the pick's own campaign art, else the artist's header — never a blurred cover stand-in), then the
    /// record as a footer row with Play, or Pre-save while it is not out. ONE record per panel (ch 08 §9 #9). The caller
    /// keys it on the rail/band arm (props freeze at mount).</summary>
    internal static Element PickCard(Artist a, Func<ColorF> accent, bool horizontal)
    {
        ColorF tint = accent();
        ref readonly ArtistPick pick = ref a.Pick;
        string title = Entities.Strings.Resolve(pick.Title);
        string kind = Entities.Strings.Resolve(pick.Subtitle);
        string comment = Entities.Strings.Resolve(pick.Comment.IsEmpty ? pick.Eyebrow : pick.Comment);
        string item = Entities.Strings.Resolve(pick.ItemUri);
        string target = item.Length > 0 ? item : Entities.Strings.Resolve(pick.Uri);
        string? cover = Controls.ArtUrl(pick.Cover);
        string? background = Controls.ArtUrl(pick.Background.IsEmpty ? a.HeaderId : pick.Background);
        int releaseAt = pick.ReleaseAt;
        string artistName = a.Name;
        string? avatar = Controls.ArtUrl(a.ImageId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var targetUri = EntityUri.Parse(target);
        Action open = () => Shell.GoTo(Shell.For(targetUri, title));
        Action play = () => Playback.PlayContext(target);

        Element wash = new BoxEl
        {
            Grow = 1f, HitTestVisible = false,
            Gradient = GradientDown(
                new GradientStop(0f, tint with { A = 0.16f }),
                new GradientStop(0.55f, tint with { A = 0.05f }),
                new GradientStop(0.85f, tint with { A = 0f })),
        };
        Element head = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, 0f),
            Children =
            [
                // ch 08 BUG F: PersonPicture.Create's `imageSourcePath` arm paints its photo over a FROZEN `fill`
                // ColorF (no watch subscription — the flat grey the pick avatar never repainted from once the
                // palette grading landed). Controls.Artwork stacks Shimmer's Design.WatchedPlaceholder tile under
                // the real photo instead — 32 DIP is below Design.ShimmerMinEdge (80f), so it takes the cheap
                // static-but-LIVE-bound arm, not the breathing pulse. The no-photo arm keeps PersonPicture.Create
                // (its initials/glyph fallback has no art to watch).
                avatar is { Length: > 0 }
                    ? new BoxEl
                      {
                          Width = 32f, Height = 32f, Shrink = 0f, ZStack = true, ClipToBounds = true,
                          Corners = Radii.Circle(32f), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                          Children = [Controls.Artwork(avatar, 32f, 32f, 16f, decodePx: 64)],
                      }
                    : PersonPicture.Create("", 32f, displayName: artistName) with { BorderColor = Tok.StrokeCardDefault, Shrink = 0f },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Grow = 1f, Basis = 0f,
                    Children =
                    [
                        BodyStrong(artistName) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Caption(Loc.Get(Strings.Artist.ArtistPick)) with { Color = Tok.TextSecondary, MaxLines = 1 },
                    ],
                },
            ],
        };
        Element quote = new BoxEl
        {
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L),
            Grow = horizontal ? 1f : 0f,
            Children =
            [
                Design.Type.PickQuote(comment) with
                {
                    Wrap = TextWrap.Wrap, MaxLines = 4, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
        Element? photo = background is { Length: > 0 } bgUrl
            ? new BoxEl
            {
                Height = horizontal ? float.NaN : PickPhotoHeight,
                Width = horizontal ? PickPhotoColumnW : float.NaN,
                Shrink = 0f, AlignSelf = horizontal ? FlexAlign.Stretch : FlexAlign.Auto,
                ZStack = true, ClipToBounds = true,
                Children =
                [
                    // ch 08 BUG F: WATCHED, not the raw `Design.ArtworkPlaceholder` ColorF (Controls.ArtworkFill's own
                    // pattern) — bgUrl is a real, tintable cover, so the frozen placeholder never re-tinted this tile
                    // once the grading landed after it had already started decoding.
                    Ui.Image(bgUrl, ImageFit.Cover, aspect: 1.6f, decodePx: 640, corners: 0f,
                             placeholder: (ColorF?)null) with
                    {
                        Placeholder = Design.WatchedPlaceholder(bgUrl),
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    },
                ],
            }
            : null;

        // Props freeze at mount: the pre-save embed is keyed on the uri it would pre-save.
        Element trailing = releaseAt > now
            ? Embed.Comp(() => new Controls.PreSaveButton { Uri = target, Name = title, Accent = accent })
                with { Key = "pick-presave:" + target }
            : Controls.Play(tint, play);
        Element metaKind = Design.Type.TrackMeta(kind) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        Element foot = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Children =
            [
                new BoxEl { Shrink = 0f, Children = [Controls.Artwork(cover, 44f, 44f, Radii.Control, decodePx: 96)] },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        Design.Type.TrackTitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children = releaseAt > 0
                                ? [metaKind, Caption(Strings.Detail.ReleasesOn(Track.Format.ShortDate(releaseAt, now)))
                                    with { Color = tint, Weight = 600, MaxLines = 1 }]
                                : [metaKind],
                        },
                    ],
                },
                new BoxEl { Shrink = 0f, Children = [trailing] }.Skeletonized(false),
            ],
        };

        Element copy = new BoxEl
        {
            Direction = 1,
            Grow = horizontal ? 1f : 0f, Basis = horizontal ? 0f : float.NaN, MinWidth = horizontal ? 0f : float.NaN,
            Children = !horizontal && photo is not null ? [head, quote, photo, foot] : [head, quote, foot],
        };
        Element content = horizontal && photo is not null ? new BoxEl { Direction = 0, Children = [copy, photo] } : copy;

        return Controls.CardPhysics(new BoxEl
        {
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = open,
            Draggable = Drag.Source(() => new DragPayload(Drag.KindOfUri(target), target, target, title, ArtUrl: cover)),
            Children = [wash, content],
        });
    }

    // ── 4.3 the gallery lightbox (ArtistGalleryLightbox.cs, W21) ────────────────────────────────────────────────────

    /// <summary>Open the full-window modal lightbox at <paramref name="index"/>: focus-trapped, no light dismiss; Escape
    /// closes — unless zoomed, where it unzooms and VETOES the close.</summary>
    internal static void GalleryOpen(IOverlayService? overlay, string[] photos, int index)
    {
        if (photos.Length == 0 || Controls.IsNullOverlay(overlay)) return;
        OverlayHandle? handle = null;
        GalleryLightbox? viewer = null;
        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => viewer = new GalleryLightbox(photos, index, () => handle)),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        handle.ClosingAction = _ => !(viewer?.TryConsumeEscape() ?? false);
        handle.ClosedAction = () => viewer?.Cleanup();
    }

    /// <summary>FlipView paging under auto-hiding floating chrome (2600 ms of pointer stillness), a filmstrip, and a real
    /// pan/zoom mode that REPLACES the pager and the filmstrip subtrees so drag/wheel ownership is never ambiguous. The
    /// photo list and the opening index are mount-stable by construction (the overlay mounts one instance per open).
    /// 0.3 changes: the unzoom settle is a frame-clock <c>UseTimeout</c> instead of <c>Task.Delay</c> (ch 08 §5), and
    /// every string goes through its key (ch 08 §6).</summary>
    sealed class GalleryLightbox : Component
    {
        const float IdleHideMs = 2600f, UnzoomSettleMs = 260f;
        const float ZoomMax = 4f, ZoomClick = 2.2f, ZoomWheelK = 0.0022f;
        const float Thumb = 56f, StripPad = 14f;

        static readonly GradientSpec TopScrim = GradientDown(
            new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 158)),
            new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 0)));

        /// <summary>One client for the process's exports, created on the first one.</summary>
        static HttpClient? s_http;

        readonly string[] _photos;
        readonly int _initialIndex;
        readonly Func<OverlayHandle?> _handle;
        readonly Signal<bool> _saving = new(false);
        readonly Signal<bool> _chrome = new(true);
        readonly Signal<bool> _zoomMode = new(false);

        // pan/zoom live state — screen px written straight onto the carrier's anim channels, never through a render
        NodeHandle _zoomNode;
        float _s = 1f, _tx, _ty;
        Point2 _pendingAnchor;
        bool _hasPendingAnchor;
        readonly Keyframe[] _seedKeys = new Keyframe[2];
        Point2 _dragLast, _dragDown;
        bool _dragMoved;
        TimerHandle _idleHide, _unzoom;
        int _renderCurrent;
        Action<Action> _post = static a => a();

        public GalleryLightbox(string[] photos, int initialIndex, Func<OverlayHandle?> handle)
        {
            _photos = photos;
            _initialIndex = Math.Clamp(initialIndex, 0, Math.Max(0, photos.Length - 1));
            _handle = handle;
        }

        /// <summary>Zoomed ⇒ unzoom and report the Escape consumed (the overlay vetoes its close).</summary>
        public bool TryConsumeEscape()
        {
            if (!_zoomMode.Peek()) return false;
            SpringZoomHome();
            return true;
        }

        public void Cleanup()
        {
            _idleHide.Cancel();
            _unzoom.Cancel();
        }

        public override Element Render()
        {
            var vp = UseContext(Viewport.Size);
            var (selected, setSelected) = UseState(_initialIndex);
            var post = UsePost();
            int current = Math.Clamp(selected, 0, Math.Max(0, _photos.Length - 1));
            _renderCurrent = current;
            _post = post;
            bool zoomed = _zoomMode.Value;
            _idleHide = UseTimeout(() => { if (_chrome.Peek()) _chrome.Value = false; }, IdleHideMs);
            // Swap back to the pager only once the spring has visibly settled, and only if nobody zoomed in again.
            _unzoom = UseTimeout(() => { if (_zoomMode.Peek() && _s <= 1.001f) _zoomMode.Value = false; }, UnzoomSettleMs);

            return new BoxEl
            {
                Grow = 1f, ZStack = true, Fill = ColorF.FromRgba(0, 0, 0, 224), Focusable = true,
                OnKeyDown = e => OnKeys(e, current, setSelected),
                OnHoverMove = _ => Poke(),
                Children =
                [
                    zoomed ? ZoomSurface(_photos[current], vp) : Pager(vp, current, i => { ResetZoomState(); setSelected(i); }),
                    TopChrome(current, post),
                    zoomed ? new BoxEl() : Filmstrip(current, i => { ResetZoomState(); setSelected(i); }),
                ],
            };
        }

        Element Pager(Size2 vp, int current, Action<int> onSelect)
        {
            var pages = new Element[_photos.Length];
            for (int i = 0; i < pages.Length; i++)
                pages[i] = new BoxEl
                {
                    Width = vp.Width, Height = vp.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    // Ctrl+wheel / a touchpad pinch enters zoom; everything else bubbles to the FlipView.
                    OnPointerWheel = e =>
                    {
                        if ((e.Mods & KeyModifiers.Ctrl) == 0) return;
                        e.Handled = true;
                        EnterZoom(e.Local);
                    },
                    Children = [Photo(_photos[i], vp)],
                };
            return FlipView.Create(pages, vp.Width, vp.Height, new Signal<int>(current), onChange: onSelect);
        }

        /// <summary>The photo's placeholder is fully TRANSPARENT, so a still-decoding frame shows the black ground.</summary>
        static Element Photo(string url, Size2 vp) => new ImageEl
        {
            Key = "gallery-image:" + url,
            Source = url, Width = vp.Width, Height = vp.Height, Fit = ImageFit.Contain,
            DecodePx = MathF.Min(MathF.Max(vp.Width, vp.Height), Design.ImageDecodeScale.Ceiling),
            Placeholder = ColorF.FromRgba(0, 0, 0, 0),
            RevealTransition = ImageTransition.Fade(140f),
        };

        Element ZoomSurface(string url, Size2 vp) => new BoxEl
        {
            Width = vp.Width, Height = vp.Height, ClipToBounds = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = CursorId.Hand,
            OnPointerDown = lp => { _dragLast = lp; _dragDown = lp; _dragMoved = false; },
            OnDrag = lp =>
            {
                float dx = lp.X - _dragLast.X, dy = lp.Y - _dragLast.Y;
                if (MathF.Abs(lp.X - _dragDown.X) + MathF.Abs(lp.Y - _dragDown.Y) > 4f) _dragMoved = true;   // 4-px slop
                _dragLast = lp;
                _tx += dx;
                _ty += dy;
                ClampPan(vp);
                WriteTransform();
            },
            OnClick = () => { if (!_dragMoved) SpringZoomHome(); },
            OnPointerWheel = e =>
            {
                e.Handled = true;
                if ((e.Mods & KeyModifiers.Ctrl) != 0)
                {
                    float s1 = _s * MathF.Exp(-e.Delta * ZoomWheelK);
                    if (s1 <= 1.02f) { SpringZoomHome(); return; }
                    ZoomAt(e.Local, s1, vp);
                    WriteTransform();
                }
                else
                {
                    _tx -= e.DeltaX;
                    _ty -= e.Delta;
                    ClampPan(vp);
                    WriteTransform();
                }
            },
            Children =
            [
                new BoxEl
                {
                    Width = vp.Width, Height = vp.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    OnRealized = nh => { _zoomNode = nh; SeedEnterZoom(vp); },
                    Children = [Photo(url, vp)],
                },
            ],
        };

        void EnterZoom(Point2 anchor)
        {
            _pendingAnchor = anchor;
            _hasPendingAnchor = true;
            _s = 1f; _tx = 0f; _ty = 0f;
            _zoomMode.Value = true;
        }

        void SeedEnterZoom(Size2 vp)
        {
            WriteTransform();
            Point2 a = _hasPendingAnchor ? _pendingAnchor : new Point2(vp.Width * 0.5f, vp.Height * 0.5f);
            _hasPendingAnchor = false;
            ZoomAt(a, ZoomClick, vp);
            SpringChannels();
        }

        void ZoomAt(Point2 local, float s1, Size2 vp)
        {
            s1 = Math.Clamp(s1, 1f, ZoomMax);
            float dx = local.X - vp.Width * 0.5f, dy = local.Y - vp.Height * 0.5f;
            float k = s1 / _s;
            _tx = dx - (dx - _tx) * k;
            _ty = dy - (dy - _ty) * k;
            _s = s1;
            ClampPan(vp);
        }

        /// <summary>The pan bound. The gallery edge carries no pixel dimensions, so the contain box is taken as the
        /// viewport's shorter side squared — a bound that never lets the photo leave the window.</summary>
        void ClampPan(Size2 vp)
        {
            float edge = MathF.Min(vp.Width, vp.Height);
            float maxX = MathF.Max(0f, (edge * _s - vp.Width) * 0.5f);
            float maxY = MathF.Max(0f, (edge * _s - vp.Height) * 0.5f);
            _tx = Math.Clamp(_tx, -maxX, maxX);
            _ty = Math.Clamp(_ty, -maxY, maxY);
        }

        void WriteTransform()
        {
            var anim = Context.Anim;
            if (anim is null || _zoomNode.IsNull) return;
            Seed(anim, AnimChannel.ScaleX, _s);
            Seed(anim, AnimChannel.ScaleY, _s);
            Seed(anim, AnimChannel.TranslateX, _tx);
            Seed(anim, AnimChannel.TranslateY, _ty);
        }

        void Seed(AnimEngine anim, AnimChannel ch, float v)
        {
            _seedKeys[0] = new Keyframe(0f, v, Easing.Linear);
            _seedKeys[1] = new Keyframe(1f, v, Easing.Linear);
            anim.Keyframes(_zoomNode, ch, _seedKeys, 1f);
        }

        void SpringChannels()
        {
            var anim = Context.Anim;
            if (anim is null || _zoomNode.IsNull) return;
            var sp = SpringParams.FromResponse(0.32f, 0.9f);
            anim.Spring(_zoomNode, AnimChannel.ScaleX, _s, sp);
            anim.Spring(_zoomNode, AnimChannel.ScaleY, _s, sp);
            anim.Spring(_zoomNode, AnimChannel.TranslateX, _tx, sp);
            anim.Spring(_zoomNode, AnimChannel.TranslateY, _ty, sp);
        }

        void SpringZoomHome()
        {
            _s = 1f; _tx = 0f; _ty = 0f;
            SpringChannels();
            _unzoom.Restart();
        }

        void ResetZoomState()
        {
            _s = 1f; _tx = 0f; _ty = 0f;
            if (_zoomMode.Peek()) _zoomMode.Value = false;
        }

        void OnKeys(KeyEventArgs e, int current, Action<int> setSelected)
        {
            if (e.Handled) return;
            if (e.KeyCode is Keys.Left or Keys.Right)
            {
                int next = Math.Clamp(current + (e.KeyCode == Keys.Right ? 1 : -1), 0, _photos.Length - 1);
                if (next != current) { ResetZoomState(); setSelected(next); }
                e.Handled = true;
            }
        }

        /// <summary>ONE 72-DIP row: [Gallery / "n / N"] · spacer · Export · Close (ch 08 audit #3).</summary>
        Element TopChrome(int current, Action<Action> post)
        {
            bool saving = _saving.Value;
            return new BoxEl
            {
                Height = 72f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                Gradient = TopScrim,
                Opacity = _chrome.Value ? 1f : 0f, Transition = MotionTok.ControlNormal,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Artist.Gallery)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                            new TextEl((current + 1).ToString(CultureInfo.CurrentCulture) + " / "
                                       + _photos.Length.ToString(CultureInfo.CurrentCulture)) { Size = 12f, Color = Tok.TextSecondary },
                        ],
                    },
                    new BoxEl { Grow = 1f },
                    Button.Accent(Loc.Get(saving ? Strings.Artist.GalleryExporting : Strings.Artist.GalleryExport),
                        () => ExportImage(_photos[current], current, post), isEnabled: !saving),
                    Button.Standard(Loc.Get(Strings.Common.Close), () => _handle()?.Close()),
                ],
            };
        }

        Element Filmstrip(int current, Action<int> setSelected)
        {
            var thumbs = new Element[_photos.Length];
            for (int i = 0; i < thumbs.Length; i++)
            {
                int idx = i;
                bool active = i == current;
                thumbs[i] = new BoxEl
                {
                    Width = Thumb, Height = Thumb, Shrink = 0f,
                    Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                    BorderWidth = 2f, BorderColor = active ? Tok.AccentTextPrimary : ColorF.FromRgba(0, 0, 0, 0),
                    Opacity = active ? 1f : 0.55f, Transition = MotionTok.ControlFast,
                    OnClick = () => setSelected(idx), Cursor = CursorId.Hand, Role = AutomationRole.Button,
                    HoverScale = Design.Motion.ScaleStandard.Hover,
                    Children = [Controls.Artwork(_photos[idx], Thumb, Thumb, Radii.Control, decodePx: 128)],
                };
            }
            return new BoxEl
            {
                Height = Thumb + 2f * StripPad, AlignSelf = FlexAlign.End,
                Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Padding = Edges4.All(StripPad),
                Opacity = _chrome.Value ? 1f : 0f, Transition = MotionTok.ControlNormal,
                Children = thumbs,
            };
        }

        void Poke()
        {
            if (!_chrome.Peek()) _chrome.Value = true;
            _idleHide.Restart();
        }

        /// <summary>The native Save As picker, then the original bytes: a CDN url downloads, a local cover (the offline
        /// seed's bundled art) copies.</summary>
        async void ExportImage(string url, int index, Action<Action> post)
        {
            if (_saving.Peek() || url.Length == 0) return;
            string? path;
            try
            {
                path = FilePicker.SaveFile(FluentApp.WindowHandle, Loc.Get(Strings.Artist.GalleryExportTitle),
                    "artist-gallery-" + (index + 1).ToString("D2", CultureInfo.InvariantCulture) + ExtensionOf(url),
                    (Loc.Get(Strings.Artist.GalleryImages), "*.jpg;*.jpeg;*.png;*.webp"),
                    (Loc.Get(Strings.Artist.GalleryAllFiles), "*.*"));
            }
            catch (InvalidOperationException ex) { Log.Warn("artist", "the gallery export dialog failed", ex); return; }
            if (path is null) return;

            _saving.Value = true;
            try
            {
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var http = s_http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    using var response = await http.GetAsync(url).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                }
                else
                {
                    string source = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new System.Uri(url).LocalPath : url;
                    await Task.Run(() => File.Copy(source, path, overwrite: true)).ConfigureAwait(false);
                }
                post(() =>
                {
                    _saving.Value = false;
                    Notify.Say(Loc.Get(Strings.Artist.GalleryExported), InfoBarSeverity.Success);
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                           or TaskCanceledException or UriFormatException or NotSupportedException)
            {
                Log.Warn("artist", "the gallery export failed", ex);
                string reason = ex.Message;
                post(() =>
                {
                    _saving.Value = false;
                    Notify.Say(Strings.Artist.GalleryExportFailed(reason), InfoBarSeverity.Error);
                });
            }
        }

        static string ExtensionOf(string url)
        {
            if (System.Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                string ext = Path.GetExtension(parsed.AbsolutePath).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".webp") return ext;
            }
            return ".jpg";
        }
    }
}
