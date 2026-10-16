// ── Entities/Artist.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The artist page's CORE layout sections and facts: the hero's metrics and its tier-free collapse arithmetic (ArtistHeroLayout; WHERE the hero's
// sub-tiers break and what each lays out is the structure's since TC6, `Theming\Policies\ArtistTierPolicy.cs`), ArtistPopularLayout (ported VERBATIM
// from 0.2.9's Features/Detail/ArtistPopularLayout.cs, ch 08 §8) and ArtistSections (the page's pure decisions: section order, pivot
// membership, the chart's column clamp, the profile-facts list, the fans fallback, the page-failure verdict), then the
// hero's facts (HeroText) and the gallery lightbox's core (the overlay, the open index, the export). The hero, the artist pick, the shelves, the
// biography band and the banners are the structure's since T10b1 (`Themes\Classic\Structure\Views\Pages\ClassicArtist*.cs`), the lightbox's
// picture since T10b2 (`ClassicArtistLightbox.cs`, `ViewKeys.ArtistLightbox`); the Top-tracks chart is the named partial Artist.UI.Chart.cs.
//
// Role: CORE (sections 1-3 engine-free except the Spacing constants; section 4 the hero's facts and the lightbox's overlay, index and export)
// Owner: N (stream N-A)
// Wave: 5
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
// (BlendBackdropHeightFor/BlendBoundaryFor(in metrics)) and the magazine gutter (metrics.Gutter). The width-only height
// forms went with the core's tier ladder (TC6): the structure's records are the only place a hero height is named.
//
// NAME NOTE: inside `struct Artist` the simple names `Uri`, `Id`, `Name`, `Version`, `Pick`, `Latest` and `Links` are the
// handle's INSTANCE members — `System.Uri` is spelled in full and nothing here declares a nested type of those names.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

// ══ 1. CORE — THE HERO'S METRICS AND ITS COLLAPSE ARITHMETIC ═══════════════════════════════════════════════════════════

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

    /// <summary>The photograph's own extent when it is a slice of the hero (a stacked arm); 0 when the photograph fills the whole hero.</summary>
    public float PhotoHeight { get; init; }
}

/// <summary>The full-bleed artist hero's tier-free arithmetic: the photograph's extent, its fade and parallax, and the collapse into the 56-DIP band. WHERE
/// the hero's sub-tiers break (the hero ladder, <see cref="Wavee.Theming.ArtistLadders.Hero"/>) and what each one lays out (its height, gutter, veil and copy
/// measure: <see cref="Wavee.Theming.ArtistTierPolicy.Hero"/>) is the structure's (Classic: <c>Rules\Tiers.Artist.cs</c>).</summary>
public static class ArtistHeroLayout
{
    /// <summary>The photograph's own extent: the full hero, or the slice the metrics name. Both the banner's media box and <c>HeroArt</c> derive from THIS,
    /// so they cannot disagree.</summary>
    public static float PhotoHeightFor(in ArtistHeroMetrics m) => m.PhotoHeight > 0f ? MathF.Min(m.PhotoHeight, m.MinHeight) : m.MinHeight;

    public const float PhotoParallaxFraction = 0.15f;
    public static float ContentBlendTail => Spacing.XXXL * 3f;
    public const float CompactIdentityHeight = Detail.VerticalLayout.CompactIdentityHeight;

    public static float PhotoFadeBandFor(float height) => Math.Clamp(height * 0.28f, 120f, 180f);
    public static float CollapseDistance(float height) => MathF.Max(1f, height - CompactIdentityHeight);
    public static float ExpandedFadeStart(float collapseDistance) => Detail.VerticalLayout.ExpandedFadeStart(collapseDistance);
    public static float CompactRevealStart(float collapseDistance) => Detail.VerticalLayout.CompactRevealStart(collapseDistance);

    /// <summary>The wash's extent from the RESOLVED (hysteretic) metrics the hero itself is sized from (ch 08 §9 inconsistency #1).</summary>
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

    /// <summary>The chart: never taller than five rows, never more than 50 tracks; its widest column count and its rows per column are the structure's per
    /// tier (<see cref="Wavee.Theming.ArtistTierPolicy.ChartMaxColumns"/>, <see cref="Wavee.Theming.ArtistTierPolicy.ChartRows"/>), two and five where it
    /// declares none.</summary>
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

    /// <summary>Never offer more columns than the rows can fill: a chart no longer than one column's <paramref name="rows"/> is ONE column at any width
    /// (ArtistPopular.cs:138, ch 08 audit #4), and never more than <paramref name="maxColumns"/> (the tier's).</summary>
    public static int ChartColumns(int total, int maxColumns = ChartMaxColumns, int rows = ChartMaxRows)
    {
        int perColumn = Math.Max(1, rows);
        return Math.Clamp((total + perColumn - 1) / perColumn, 1, Math.Max(1, maxColumns));
    }

    /// <summary>The chart skeleton's counts: the rows shown (the known count, else the overview's seed cap: an empty edge assumes the overview's
    /// ten, W5), the columns the real layout would offer at the tier's <paramref name="maxColumns"/> and <paramref name="rows"/> (so the skeleton
    /// keeps the chart's own columns per tier) and the rows per column. The structure's skeleton draws them (`ArtistChartStyle.Skeleton`).</summary>
    public static (int Total, int Columns, int PerColumn) ChartSkeletonShape(int knownCount, int maxColumns = ChartMaxColumns, int rows = ChartMaxRows)
    {
        int total = knownCount > 0 ? Math.Min(knownCount, ArtistPopularTracks.OverviewSeedCap) : ArtistPopularTracks.OverviewSeedCap;
        int cols = ChartColumns(total, maxColumns, rows);
        int perCol = Math.Min(Math.Max(1, rows), Math.Max(1, (total + cols - 1) / cols));
        return (total, cols, perCol);
    }

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

// ══ 4. THE HERO'S FACTS AND THE LIGHTBOX'S CORE ═══════════════════════════════════════════════════════════════════════

public readonly partial struct Artist
{
    // ── 4.1 what the hero states (ch 08 W1-W4b, §3 hero rows) ─────────────────────────────────────────────────────────

    /// <summary>What the hero STATES, resolved once per page render. <see cref="Placeholder"/> is the representative
    /// shape the page's derived skeleton is built from — its words are never shown (the deriver paints bars in their
    /// place), so the shimmer carries the loaded hero's exact geometry (ch 08 §0 #15, W5). The hero itself is Classic's
    /// (`ClassicArtistHero`); these are the facts it draws.</summary>
    internal readonly record struct HeroText(string Name, string Lead, bool Verified, ushort Rank, uint Monthly, uint Followers)
    {
        public static HeroText For(Artist a) => new(a.Name, Entities.Strings.Resolve(a.BioLeadId), a.IsVerified,
            a.WorldRank, a.MonthlyListeners, a.Followers);

        public static HeroText Placeholder => new("Artist name", "A first sentence of the biography stands in this line.",
            Verified: true, Rank: 100, Monthly: 10_000_000, Followers: 10_000_000);
    }

    // ── 4.2 the gallery lightbox: the overlay, the open index and the export (ArtistGalleryLightbox.cs, W21) ──────────

    /// <summary>Open the full-window modal lightbox at <paramref name="index"/>: focus-trapped, no light dismiss; Escape
    /// closes — unless zoomed, where the view unzooms and VETOES the close. The picture (the pager, the pan/zoom mode, the
    /// chrome, the filmstrip) is the structure's <see cref="ViewKeys.ArtistLightbox"/>; this keeps the overlay, the selected
    /// index (one signal per open), the close, the Escape veto's wiring and the export.</summary>
    internal static void GalleryOpen(IOverlayService? overlay, string[] photos, int index)
    {
        if (photos.Length == 0 || Controls.IsNullOverlay(overlay)) return;
        OverlayHandle? handle = null;
        Func<bool>? consumeEscape = null;
        Action? closed = null;
        var selected = new Signal<int>(Math.Clamp(index, 0, Math.Max(0, photos.Length - 1)));
        var saving = new Signal<bool>(false);
        var props = new ArtistLightboxProps
        {
            Photos = photos,
            Selected = selected,
            Select = i => { if (selected.Peek() != i) selected.Value = i; },
            Saving = saving,
            Export = (at, post) => { if ((uint)at < (uint)photos.Length) GalleryExport.Start(photos[at], at, saving, post); },
            Close = () => handle?.Close(),
            Attach = (escape, onClosed) => { consumeEscape = escape; closed = onClosed; },
        };
        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Slots.View(ViewKeys.ArtistLightbox, props),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        handle.ClosingAction = _ => !(consumeEscape?.Invoke() ?? false);
        handle.ClosedAction = () => closed?.Invoke();
    }

    /// <summary>The lightbox's export: the native Save As picker, then the original bytes — a CDN url downloads, a local
    /// cover (the offline seed's bundled art) copies — and the notice. <c>saving</c> is the open's flag the view reads.</summary>
    static class GalleryExport
    {
        /// <summary>One client for the process's exports, created on the first one.</summary>
        static HttpClient? s_http;

        public static void Start(string url, int index, Signal<bool> saving, Action<Action> post)
        {
            if (saving.Peek() || url.Length == 0) return;
            Pickers.Pick(
                PickerRequest.Save(Loc.Get(Strings.Artist.GalleryExportTitle),
                    "artist-gallery-" + (index + 1).ToString("D2", CultureInfo.InvariantCulture) + ExtensionOf(url),
                    (Loc.Get(Strings.Artist.GalleryImages), "*.jpg;*.jpeg;*.png;*.webp"),
                    (Loc.Get(Strings.Artist.GalleryAllFiles), "*.*")),
                done: path => { if (path is not null) _ = SaveImage(url, path, saving, post); },
                failed: ex => Log.Warn("artist", "the gallery export dialog failed", ex));
        }

        /// <summary>The bytes half of <see cref="Start"/>, once the Save As dialog answered (UI thread in).</summary>
        static async Task SaveImage(string url, string path, Signal<bool> saving, Action<Action> post)
        {
            saving.Value = true;
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
                    saving.Value = false;
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
                    saving.Value = false;
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
