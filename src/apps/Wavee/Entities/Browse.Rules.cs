// ── Entities/Browse.Rules.cs ───────────────────────────────────────────────────────────────────────────────────────
// ch 13 §8's Browse rule set, ported verbatim from 0.2.9 Features/Browse/*: BrowseTaxonomy, ChartPages / ChartSections,
// BrowseDirectorySeeds, BrowsePageLayout, BrowseMastheadMetrics, BrowseLayout, BrowseTiles.ToModel
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: 400 lines (a named partial of Browse.cs: Browse.cs's 500 could not hold the Wave 1 columns AND the rules —
//   split under the plan's >30 % rule by owner P at reconciliation; the section keeps its number)
// Spec: ch 13 §8; WP-5.P contract §4

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// ══ 5. THE PURE RULES (ch 13 §8 — Wave 5, owner P; ported verbatim from 0.2.9 Features/Browse/*) ═══════════════════

/// <summary>One browse category as the pure rules see it — 0.2.9's <c>BrowseCategory</c> record, kept by name so the
/// ported facts read verbatim. A page projects it from a <see cref="Browse"/> row (title, colour, artwork url, the
/// client-feature bit); the rules never touch a table.</summary>
public readonly record struct BrowseCategory(string Uri, string Title, uint? Color, string? Artwork = null,
                                             bool IsClientFeature = false);

/// <summary>The Charts directory PAGES (Browse category tiles) — stable Spotify page ids, same provenance as the
/// taxonomy map. Named constants so <see cref="ChartSections"/> can name their parent page without a second copy.</summary>
public static class ChartPages
{
    public const string Charts = "spotify:page:0JQ5DAudkNjCgYMM0TZXDw";
    public const string PodcastCharts = "spotify:page:0JQ5DAB3zgCauRwnvdEQjJ";
}

/// <summary>The Charts SECTIONS underneath those pages — <c>spotify:section:</c> uris, therefore only ever legal as a
/// <c>browseSection</c> read, never a <c>homeSection</c> one (the confusion this taxonomy exists to prevent).</summary>
public static class ChartSections
{
    /// <summary>Featured Charts — the first tile, and the one whose absence fails the Charts row loudly.</summary>
    public const string Featured = "spotify:section:0JQ5DAzQHECxDlYNI6xD1g";
    public const string Weekly = "spotify:section:0JQ5DAzQHECxDlYNI6xD1h";
    public const string Daily = "spotify:section:0JQ5DAzQHECxDlYNI6xD1i";
    public const string NowAvailable = "spotify:section:0JQ5DAzQHECxDlYNI6xD1x";
    public const string Podcast = "spotify:section:0JQ5DAob0LrW8pqFzVs4ut";

    /// <summary>Home's Charts row and Browse's Charts band read exactly these, in this order. Featured is <c>All[0]</c>
    /// so a missing Featured is the fail-loud signal; later shelves that come back empty are omitted.</summary>
    public static readonly IReadOnlyList<string> All = [Featured, Weekly, Daily, NowAvailable, Podcast];

    public static bool Contains(string? uri) => uri is { Length: > 0 } && Contains(uri.AsSpan());

    /// <summary>The span twin — what a caller asks of a uri it has not materialised.</summary>
    public static bool Contains(ReadOnlySpan<char> uri)
    {
        if (uri.IsEmpty) return false;
        for (int i = 0; i < All.Count; i++)
            if (uri.SequenceEqual(All[i])) return true;
        return false;
    }
}

/// <summary>The Browse directory's grouping. The wire has NO grouping — the bands are a product decision, carried by a
/// curated map keyed by page URI (NEVER by title: titles arrive localised) — and anything unmapped lands in
/// <see cref="BrowseGroup.More"/>, so a category Spotify adds tomorrow still appears.</summary>
public static class BrowseTaxonomy
{
    /// <summary>THE band order — Top, Charts, For you, Genres, Mood &amp; activity, More. The one spelling of the sequence:
    /// <see cref="Grouped"/> and the directory body both walk it.</summary>
    public static readonly IReadOnlyList<BrowseGroup> BandOrder =
        [BrowseGroup.Top, BrowseGroup.Charts, BrowseGroup.ForYou, BrowseGroup.Genres, BrowseGroup.MoodActivity, BrowseGroup.More];

    // Uri -> group, IN MAP ORDER (the seeds and the fake seed mirror it). Uris are stable Spotify page ids captured from
    // browseAll (browe.saz). An ordered pair list rather than a dictionary literal, so the declaration order is data.
    static readonly (string Uri, BrowseGroup Group)[] Entries =
    [
        ("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", BrowseGroup.Top),          // Music
        ("spotify:page:0JQ5DArNBzkmxXHCqFLx2J", BrowseGroup.Top),          // Podcasts
        ("spotify:page:0JQ5DAqbMKFETqK4t8f1n3", BrowseGroup.Top),          // Audiobooks
        ("spotify:concerts", BrowseGroup.Top),                              // Live Events (a client feature, not a page)

        ("spotify:page:0JQ5DAtOnAEpjOgUKwXyxj", BrowseGroup.ForYou),       // Discover
        ("spotify:page:0JQ5DAqbMKFPw634sFwguI", BrowseGroup.ForYou),       // EQUAL
        ("spotify:page:0JQ5DAqbMKFImHYGo3eTSg", BrowseGroup.ForYou),       // Fresh Finds
        ("spotify:page:0JQ5DAqbMKFGnsSfvg90Wo", BrowseGroup.ForYou),       // GLOW
        ("spotify:page:0JQ5DAt0tbjZptfcdMSKl3", BrowseGroup.ForYou),       // Made For You
        ("spotify:page:0JQ5DAqbMKFz6FAsUtgAab", BrowseGroup.ForYou),       // New Releases
        ("spotify:page:0JQ5DAqbMKFOOxftoKZxod", BrowseGroup.ForYou),       // RADAR
        ("spotify:page:0JQ5DAqbMKFDBgllo2cUIN", BrowseGroup.ForYou),       // Spotify Singles
        ("spotify:page:0JQ5DAqbMKFRKBHIxJ5hMm", BrowseGroup.ForYou),       // Tastemakers
        ("spotify:page:0JQ5DAqbMKFQIL0AXnG5AK", BrowseGroup.ForYou),       // Trending

        ("spotify:page:0JQ5DAqbMKFNQ0fGp4byGU", BrowseGroup.Genres),       // Afro
        ("spotify:page:0JQ5DAqbMKFFtlLYUHv8bT", BrowseGroup.Genres),       // Alternative
        ("spotify:page:0JQ5DAqbMKFLjmiZRss79w", BrowseGroup.Genres),       // Ambient
        ("spotify:page:0JQ5DAqbMKFQ1UFISXj59F", BrowseGroup.Genres),       // Arab
        ("spotify:page:0JQ5DAqbMKFQiK2EHwyjcU", BrowseGroup.Genres),       // Blues
        ("spotify:page:0JQ5DAqbMKFObNLOHydSW8", BrowseGroup.Genres),       // Caribbean
        ("spotify:page:0JQ5DAqbMKFPrEiAOxgac3", BrowseGroup.Genres),       // Classical
        ("spotify:page:0JQ5DAqbMKFKLfwjuJMoNC", BrowseGroup.Genres),       // Country
        ("spotify:page:0JQ5DAqbMKFHOzuVTgTizF", BrowseGroup.Genres),       // Dance/Electronic
        ("spotify:page:0JQ5DAqbMKFCLroFGPFVr5", BrowseGroup.Genres),       // Dutch music
        ("spotify:page:0JQ5DAqbMKFy78wprEpAjl", BrowseGroup.Genres),       // Folk & Acoustic
        ("spotify:page:0JQ5DAqbMKFFsW9N8maB6z", BrowseGroup.Genres),       // Funk & Disco
        ("spotify:page:0JQ5DAqbMKFQ00XGBls6ym", BrowseGroup.Genres),       // Hip-Hop
        ("spotify:page:0JQ5DAqbMKFCWjUTdzaG0e", BrowseGroup.Genres),       // Indie
        ("spotify:page:0JQ5DAqbMKFAJ5xb0fwo9m", BrowseGroup.Genres),       // Jazz
        ("spotify:page:0JQ5DAqbMKFGvOw3O4nLAf", BrowseGroup.Genres),       // K-pop
        ("spotify:page:0JQ5DAqbMKFxXaXKP7zcDp", BrowseGroup.Genres),       // Latin
        ("spotify:page:0JQ5DAqbMKFDkd668ypn6O", BrowseGroup.Genres),       // Metal
        ("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", BrowseGroup.Genres),       // Pop
        ("spotify:page:0JQ5DAqbMKFAjfauKLOZiv", BrowseGroup.Genres),       // Punk
        ("spotify:page:0JQ5DAqbMKFEZPnFQSFB1T", BrowseGroup.Genres),       // R&B
        ("spotify:page:0JQ5DAqbMKFJKoGyUMo2hE", BrowseGroup.Genres),       // Reggae
        ("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", BrowseGroup.Genres),       // Rock
        ("spotify:page:0JQ5DAqbMKFIpEuaCnimBj", BrowseGroup.Genres),       // Soul
        ("spotify:page:0JQ5DAqbMKFSCjnQr8QZ3O", BrowseGroup.Genres),       // Songwriters

        ("spotify:page:0JQ5DAqbMKFx0uLQR2okcc", BrowseGroup.MoodActivity), // At Home
        ("spotify:page:0JQ5DAqbMKFFzDl7qN9Apr", BrowseGroup.MoodActivity), // Chill
        ("spotify:page:0JQ5DAqbMKFRY5ok2pxXJ0", BrowseGroup.MoodActivity), // Cooking & Dining
        ("spotify:page:0JQ5DAqbMKFJ6dHNHTv6Mx", BrowseGroup.MoodActivity), // Fitness
        ("spotify:page:0JQ5DAqbMKFCbimwdOYlsl", BrowseGroup.MoodActivity), // Focus
        ("spotify:page:0JQ5DAqbMKFIRybaNTYXXy", BrowseGroup.MoodActivity), // In the car
        ("spotify:page:0JQ5DAqbMKFAUsdyVjCQuL", BrowseGroup.MoodActivity), // Love
        ("spotify:page:0JQ5DAqbMKFzHmL4tf05da", BrowseGroup.MoodActivity), // Mood
        ("spotify:page:0JQ5DAqbMKFI3pNLtYMD9S", BrowseGroup.MoodActivity), // Nature & Noise
        ("spotify:page:0JQ5DAqbMKFA6SOHvT3gck", BrowseGroup.MoodActivity), // Party
        ("spotify:page:0JQ5DAqbMKFCuoRTxhYWow", BrowseGroup.MoodActivity), // Sleep
        ("spotify:page:0JQ5DAqbMKFAQy4HL4XU2D", BrowseGroup.MoodActivity), // Travel
        ("spotify:page:0JQ5DAqbMKFLb2EqgLtpjC", BrowseGroup.MoodActivity), // Wellness
        ("spotify:page:0JQ5DAqbMKFAXlCG6QvYQ4", BrowseGroup.MoodActivity), // Workout Music

        (ChartPages.Charts, BrowseGroup.Charts),
        (ChartPages.PodcastCharts, BrowseGroup.Charts),
    ];

    static readonly Dictionary<string, BrowseGroup> Map = BuildMap();

    static Dictionary<string, BrowseGroup> BuildMap()
    {
        var map = new Dictionary<string, BrowseGroup>(Entries.Length, StringComparer.Ordinal);
        foreach (var (uri, group) in Entries) map[uri] = group;
        return map;
    }

    /// <summary>The band a category belongs to. Unmapped → <see cref="BrowseGroup.More"/>.</summary>
    public static BrowseGroup GroupOf(BrowseCategory c) => GroupOf(c.Uri);

    /// <inheritdoc cref="GroupOf(BrowseCategory)"/>
    public static BrowseGroup GroupOf(string uri) => Map.TryGetValue(uri, out var g) ? g : BrowseGroup.More;

    /// <summary>Every mapped uri of one band, in map order — what <see cref="BrowseDirectorySeeds"/> and the fake seed
    /// mirror entry for entry.</summary>
    public static IReadOnlyList<string> UrisOf(BrowseGroup group)
    {
        var list = new List<string>(25);
        foreach (var (uri, g) in Entries) if (g == group) list.Add(uri);
        return list;
    }

    /// <summary>Group the flat category list into bands in <see cref="BandOrder"/>, alphabetised WITHIN each band by the
    /// CURRENT culture (the titles are localised server-side) — except Top, which keeps the server's ranking. Empty
    /// bands are omitted.</summary>
    public static IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> Grouped(
        IReadOnlyList<BrowseCategory> categories)
    {
        if (categories.Count == 0) return Array.Empty<(BrowseGroup, IReadOnlyList<BrowseCategory>)>();

        var buckets = new Dictionary<BrowseGroup, List<BrowseCategory>>(6);
        foreach (var c in categories)
        {
            var g = GroupOf(c);
            if (!buckets.TryGetValue(g, out var list)) buckets[g] = list = new List<BrowseCategory>();
            list.Add(c);
        }

        var result = new List<(BrowseGroup, IReadOnlyList<BrowseCategory>)>(BandOrder.Count);
        foreach (var g in BandOrder)
        {
            if (!buckets.TryGetValue(g, out var list) || list.Count == 0) continue;
            if (g != BrowseGroup.Top)
                list.Sort(static (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            result.Add((g, list));
        }
        return result;
    }
}

/// <summary>The loading directory's SHAPE: the taxonomy map mirrored ENTRY FOR ENTRY (4 / 10 / 25 / 14) plus three
/// unmapped tail seeds for More — a three-per-band placeholder made the page height jump when data landed. Titles are a
/// single space (the rendered skeleton is derived, the text is never shown); every seed carries no artwork.</summary>
public static class BrowseDirectorySeeds
{
    public static readonly IReadOnlyList<BrowseCategory> Categories = Build();

    static BrowseCategory[] Build()
    {
        var list = new List<BrowseCategory>(56);
        foreach (var uri in BrowseTaxonomy.UrisOf(BrowseGroup.Top))
            list.Add(new BrowseCategory(uri, " ", null, null, IsClientFeature: uri == "spotify:concerts"));
        foreach (var uri in BrowseTaxonomy.UrisOf(BrowseGroup.ForYou)) list.Add(new BrowseCategory(uri, " ", null));
        foreach (var uri in BrowseTaxonomy.UrisOf(BrowseGroup.Genres)) list.Add(new BrowseCategory(uri, " ", null));
        foreach (var uri in BrowseTaxonomy.UrisOf(BrowseGroup.MoodActivity)) list.Add(new BrowseCategory(uri, " ", null));
        list.Add(new BrowseCategory("spotify:page:skeleton-more-1", " ", null));
        list.Add(new BrowseCategory("spotify:page:skeleton-more-2", " ", null));
        list.Add(new BrowseCategory("spotify:page:skeleton-more-3", " ", null));
        return list.ToArray();
    }
}

/// <summary>Which shelf treatment a browse section wants (0.2.9 <c>BrowseSectionKind</c>) — the server's
/// <c>data.__typename</c>, mapped from <see cref="SectionKind"/> by <see cref="BrowsePageLayout.KindOf"/>.</summary>
public enum BrowseSectionKind : byte { Shelf, CategoryGrid, Related }

/// <summary>One section of a browse page as the layout rule sees it (0.2.9 <c>BrowseSection</c>, minus the payloads the
/// rule never reads). <see cref="Slot"/> is the section row a page renders it from; the rule ignores it.</summary>
public readonly record struct BrowseSectionFacts(string Uri, string? Title, BrowseSectionKind Kind, int CardCount,
                                                 int CategoryCount, int Total, int Slot = 0);

/// <summary>A browse page as the layout rule sees it (0.2.9 <c>BrowsePageModel</c>).</summary>
public sealed record BrowsePageFacts(string Uri, string? Title, IReadOnlyList<BrowseSectionFacts> Sections)
{
    /// <summary>A page that resolved but carries nothing — Spotify answers 200 with only a <c>__typename</c>.</summary>
    public bool IsEmpty => Sections.Count == 0 && string.IsNullOrEmpty(Title);
}

/// <summary>Decides the SHAPE of a browse category page's body — named shelves, or one uniform grid ("flatten"). A lone
/// untitled shelf (or one whose title just repeats the page's) earns a grid, not a carousel.</summary>
public static class BrowsePageLayout
{
    public enum Mode { Shelves, FlattenOne, FlattenTwoConcat, FlattenTwoStacked }

    /// <summary>Sections with the empties dropped, original order kept — render THIS list, whatever the mode.</summary>
    public sealed record Result(Mode Mode, IReadOnlyList<BrowseSectionFacts> Sections);

    public static Result Of(BrowsePageFacts page)
    {
        var survivors = new List<BrowseSectionFacts>(page.Sections.Count);
        foreach (var s in page.Sections)
        {
            bool empty = s.Kind == BrowseSectionKind.Shelf ? s.CardCount == 0 : s.CategoryCount == 0;
            if (!empty) survivors.Add(s);
        }

        // A page with no uri has no endpoint to page a section against, so it never earns a grid (the skeleton's shape).
        if (string.IsNullOrWhiteSpace(page.Uri)) return new Result(Mode.Shelves, survivors);

        // CategoryGrid/Related never flatten — only Shelf sections count here.
        BrowseSectionFacts first = default, second = default;
        int shelfCount = 0;
        foreach (var s in survivors)
        {
            if (s.Kind != BrowseSectionKind.Shelf) continue;
            shelfCount++;
            if (shelfCount == 1) first = s;
            else if (shelfCount == 2) second = s;
        }

        if (shelfCount == 1 && TitleIsRedundant(first.Title, page.Title)) return new Result(Mode.FlattenOne, survivors);

        // Two-up flatten is STRICTER: both titles genuinely blank, not merely redundant with the page.
        if (shelfCount == 2 && IsBlank(first.Title) && IsBlank(second.Title))
            return new Result(HasMore(first) || HasMore(second) ? Mode.FlattenTwoStacked : Mode.FlattenTwoConcat, survivors);

        return new Result(Mode.Shelves, survivors);
    }

    public static bool HasMore(in BrowseSectionFacts s) => s.Total > s.CardCount;

    public static bool TitleIsRedundant(string? sectionTitle, string? pageTitle)
    {
        var s = sectionTitle?.Trim();
        if (string.IsNullOrEmpty(s)) return true;
        return string.Equals(s, pageTitle?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBlank(string? title) => string.IsNullOrWhiteSpace(title);

    /// <summary>A section row's form → the rule's kind. A home or unknown form is a shelf, as 0.2.9's mapper decided.</summary>
    public static BrowseSectionKind KindOf(SectionKind kind) => kind switch
    {
        SectionKind.BrowseCategoryGrid => BrowseSectionKind.CategoryGrid,
        SectionKind.BrowseRelated => BrowseSectionKind.Related,
        _ => BrowseSectionKind.Shelf,
    };
}

/// <summary>The overlay masthead's layout reserve — <c>FrameTop</c> + the SurfaceDisplay line. A CONSTANT, never a live
/// measure: parked family pages must not re-pad when the overlay fades out.</summary>
public static class BrowseMastheadMetrics
{
    public const float TitleLine = 52f;
    public const float Reserve = Spacing.XXXL + TitleLine;
    /// <summary>The body's top inset: the overlay reserve plus the gap that used to sit under the in-flow band.</summary>
    public const float BodyTop = Reserve + Spacing.L;
    /// <summary>The band paints NOTHING (a fill reads as a black slab on Mica), so a page scrolling under it cuts its
    /// content at the band's lower edge — exactly the reserve.</summary>
    public const float ClipInset = Reserve;
    /// <summary>The feather at that cut — the band every detail surface uses.</summary>
    public const float ClipFadeBand = Detail.VerticalLayout.StickyFadeBand;

    public static Edges4 FamilyBodyPad(float bottom) => new(Spacing.PageWide, BodyTop, Spacing.PageWide, bottom);

    /// <summary>Padding for a page that clips under the band: the reserve is a SPACER above the clipped node (so the cut
    /// engages when content reaches the band, not at rest), leaving the gutters and the bottom on the node.</summary>
    public static Edges4 FamilyUnderBandPad(float bottom) => new(Spacing.PageWide, 0f, Spacing.PageWide, bottom);
}

/// <summary>Layout constants and the column math for the Browse cells (0.2.9 <c>BrowseTiles.cs:286-358</c>).</summary>
public static class BrowseLayout
{
    public const float FrameTop = Spacing.XXXL;
    public const float MastheadReserve = BrowseMastheadMetrics.Reserve;
    public const float FrameX = Spacing.PageWide;
    public static Edges4 Frame(float bottom) => new(FrameX, FrameTop, FrameX, bottom);
    /// <summary>First-frame width guess the directory's Responsive grids share before a real measure lands.</summary>
    public const float DirectoryFallbackWidth = 900f;

    public const float WordChipH = 36f;
    public const float NameChipH = 32f;
    public const float ChipGap = Spacing.S;
    public const float TickW = 3f;
    public const float TickH = 14f;
    public const float BarHeight = 52f;
    public const float MoreHeight = 88f;
    public const float Peek = 80f;
    public const float PeekCopyFrac = 0.62f;
    public const float Pip = 8f;

    /// <summary>The Genres grid's column bands — fixed bands, never a floor-divide that flaps across one pixel.</summary>
    public static int LinkColumns(float width) => width > 720f ? 3 : width > 380f ? 2 : 1;

    public const float BarColMin = 132f;
    /// <summary>The Mood grid — plain floor-fit, NO cap.</summary>
    public static int BarColumns(float width) => Math.Max(1, (int)(width / BarColMin));

    public const float MoreColMin = 168f;
    const int MoreColMax = 4;
    /// <summary>The More grid — floor-fit, capped at four.</summary>
    public static int MoreColumns(float width) => Math.Clamp((int)(width / MoreColMin), 1, MoreColMax);

    /// <summary>A star grid, not a wrapped flex row (a row of Grow cells reports a one-line measure). AlignSelf Stretch
    /// backfills a 1-column band; zero cells is a bare box.</summary>
    public static Element StarGrid(int columns, float colGap, float rowGap, IReadOnlyList<Element> cells)
    {
        if (cells.Count == 0) return new BoxEl();
        var tracks = new TrackSize[Math.Max(1, columns)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        var children = new Element[cells.Count];
        for (int i = 0; i < children.Length; i++) children[i] = cells[i];
        return Ui.Grid(tracks, colGap, rowGap, float.NaN, children) with { AlignSelf = FlexAlign.Stretch };
    }
}

/// <summary>One destination a Browse cell renders, wherever it came from — a directory category, a page's related
/// block, a search genre (0.2.9 <c>BrowseTileModel</c>).</summary>
public readonly record struct BrowseTileModel(string Title, string Uri, uint? Color, string? Artwork, Action Open);

/// <summary>The cell factories' pure half (the densities themselves are <c>Browse.UI.cs</c>).</summary>
public static partial class BrowseTiles
{
    /// <summary>One category → one tile model. Both handlers null ⇒ the shared inert <see cref="ToModelNoop"/> (the
    /// loading directory: the cell still renders and hovers, and does nothing). A client feature routes to
    /// <paramref name="openFeature"/>, never to a browse page.</summary>
    public static BrowseTileModel ToModel(BrowseCategory c, Action<string, string>? openCategory, Action<string>? openFeature) => new(
        c.Title, c.Uri, c.Color, c.Artwork,
        openCategory is null && openFeature is null
            ? ToModelNoop
            : () =>
            {
                if (c.IsClientFeature) openFeature!(c.Uri);
                else openCategory!(c.Uri, c.Title);
            });

    public static readonly Action ToModelNoop = static () => { };

    /// <summary>A client feature's surface (0.2.9 <c>BrowseRoutes.FeatureRoute</c>): only <c>spotify:concerts</c> resolves
    /// (the Concerts hub); anything else is <see cref="Shell.Route.None"/>, so the tile declines rather than navigating to
    /// a key no page renders.</summary>
    public static Shell.Route FeatureRoute(string featureUri)
        => string.Equals(featureUri, "spotify:concerts", StringComparison.Ordinal)
            ? new Shell.Route(Shell.RouteKind.Concerts)
            : Shell.Route.None;

    /// <summary>A browse category page's route: the page uri as the subject, the title as the frame-one masthead arg.
    /// UI thread (the parse interns a text-form uri).</summary>
    public static Shell.Route PageRoute(string pageUri, string? title)
        => new(Shell.RouteKind.BrowseCategory, EntityUri.Parse(pageUri.AsSpan()),
               string.IsNullOrWhiteSpace(title) ? default : Entities.Strings.Intern(title.AsSpan().Trim()));
}
