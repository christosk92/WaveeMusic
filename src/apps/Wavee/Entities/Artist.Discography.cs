// ── Entities/Artist.Discography.cs ─────────────────────────────────────────────────────────────────────────────────
// the disco: facet page, the virtual grid, the album drawer, the drawer adapter, the era bands — a deep-linkable route
// in the route table (§4.11), and the three inline facet sections of the artist page
//
// Role: CORE (§1-§5: DiscoFacet, DiscographyEraBands, DiscoRoute, DiscoCardText, the demand + drawer adapter) + UI (§6-§9)
// Owner: N (stream N-B)
// Wave: 5
// Budget: 1300 lines
// Spec: ch 08 §1.2, W14-W17, W22, §7 (discography rows), §8 (era bands, route, card text), §9 traps; WP-5.N contract §4, §9
//
// ── HOW DATA REACHES THE GRID (props freeze at mount) ────────────────────────────────────────────────────────────────
//
// There is no `VirtualCollection`, no snapshot key and no `ReplaceSnapshot` (ch 08 §1.2): the three facet EDGES version
// the list. A facet section and the disco page hand the grid an (artist, facet) value through re-pushed props. The GRID
// subscribes to ONE value — the facet's server `Total`, a `UseComputed` memo over the scope and the edge that notifies
// only when the total moves (LazyGrid's `IReadSignal<int>` count form, W2-A5) — so a page landing, an album row
// draining or any other table's publication never re-renders the grid or rebuilds its realized cells (the old
// `Func<int>` count read `Albums.Changed` and re-rendered the whole grid on every publication: `LazyGrid×1 a=452K` per
// frame in real mode). Each realized cell is its own component (`DiscoCell`): it resolves the album at its position off
// `Edges.Artist{Albums,Singles,Compilations}.Targets(artist)` LIVE, gates on that one row's `DiscoCellStamp` (slot,
// version, DiscoCard readiness) and flips its own placeholder to a card when the row lands — without a grid render.
// `Total` drives the shimmer count while the facet is still Partial — the "shimmer-up-to-N" 0.2.9 needed a separate
// total probe for (the overview's first page states the total, so nothing extra is asked).
//
// THE DRAWER'S ONE VERDICT (ch 08 §9 must-not-simplify 3): the reserved slot height (`DrawerHeight`), the panel and the
// bring-into-view all read `Album.DrawerVerdict.For` (owner M, Album.cs) through `Artist.DrawerVerdictFor` — the ARTIST
// half of the adapter, which turns (the expanded album's `AlbumTracks` readiness and count, the card's `TrackCount`, the
// grid columns) into that call. `DrawerHeight` reads the CACHED verdict and `DrawerFor` recomputes it (§9 trap).
//
// THE EXPANDED IDENTITY IS THE ALBUM SLOT, never an ordinal: the LazyGrid index is re-derived by a scan every render, so
// a late page landing re-points (or closes) the drawer instead of leaving a stale index on whatever now sits there.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using DrawerVerdict = Wavee.Album.DrawerVerdict;

namespace Wavee;

// ══ 1. THE FACET ═════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The three discography facets. The value IS the <c>disco:&lt;kind&gt;:</c> digit (0.2.9 <c>DiscographyKind</c>'s
/// ordinals), so a persisted route key survives the rewrite.</summary>
public enum DiscoFacet : byte { Albums = 0, Singles = 1, Compilations = 2 }

// ══ 2. THE ERA BANDS (ch 08 §8, DiscographyEraBands.cs verbatim; input is a span of years) ═══════════════════════════

public readonly record struct DiscographyYearRun(int Year, int Start, int Count);
public readonly record struct DiscographyEraBand(string Label, int Start, int Count, bool Provisional = false);

/// <summary>Resize-stable discography grouping. Sparse calendar buckets coalesce until every header earns roughly one
/// nominal five-card row; tiny or low-variety catalogues stay flat. FOUR bail-outs answer null (the header keeps the plain
/// "N releases"): <c>itemCount ≤ 5</c>, <c>distinct years &lt; 3</c>, <c>covered ≤ 5</c>, <c>planned &lt; 2</c>. FIVE
/// label arms: "1994", "1990s", "1990s–1970s", "1994–1991", and "1990s and earlier" (reachable only with
/// <c>provisional</c>, which no caller passes — dead on the artist page, as in 0.2.9).</summary>
public static class DiscographyEraBands
{
    public const int NominalColumns = 5;
    public const int MinBandItems = NominalColumns;
    public const int MaxBands = 8;

    static readonly int[] Widths = [1, 2, 5, 10, 20, 50, 100];

    /// <summary>0.2.9 <c>PlanAlbums</c> with the per-album year read moved to the caller: one year per flat-grid item, in
    /// grid order, 0 = undated (it joins the run it sits in). Display-only: the result never owns grid geometry.</summary>
    public static DiscographyEraBand[]? PlanYears(ReadOnlySpan<ushort> years)
    {
        if (years.IsEmpty) return null;
        var runs = new List<DiscographyYearRun>();
        int openYear = 0, openStart = 0, openCount = 0;
        for (int i = 0; i < years.Length; i++)
        {
            int year = years[i];
            if (openCount == 0)
            {
                openYear = year;
                openStart = i;
                openCount = 1;
            }
            else if (year <= 0 || openYear <= 0 || year == openYear)
            {
                if (openYear <= 0 && year > 0) openYear = year;
                openCount++;
            }
            else
            {
                runs.Add(new DiscographyYearRun(openYear, openStart, openCount));
                openYear = year;
                openStart = i;
                openCount = 1;
            }
        }
        if (openCount > 0) runs.Add(new DiscographyYearRun(openYear, openStart, openCount));
        return Plan(CollectionsMarshal.AsSpan(runs), years.Length);
    }

    /// <summary>The display era containing a flat-grid item index, or null when the catalogue stays ungrouped.</summary>
    public static DiscographyEraBand? AtIndex(IReadOnlyList<DiscographyEraBand>? eras, int index)
    {
        if (eras is null || index < 0) return null;
        for (int i = 0; i < eras.Count; i++)
        {
            var era = eras[i];
            if (index >= era.Start && index < era.Start + era.Count) return era;
        }
        return null;
    }

    readonly record struct DatedRun(int Year, int Start, int Count);
    readonly record struct WorkBand(int Newest, int Oldest, int Start, int Count)
    {
        public WorkBand Merge(in WorkBand older) =>
            new(Math.Max(Newest, older.Newest), Math.Min(Oldest, older.Oldest),
                Math.Min(Start, older.Start), Count + older.Count);
    }

    public static DiscographyEraBand[]? Plan(ReadOnlySpan<DiscographyYearRun> runs, int itemCount, bool provisional = false)
    {
        if (runs.IsEmpty || itemCount <= MinBandItems) return null;
        var dated = Normalize(runs);
        if (dated.Count == 0) return null;

        int distinct = 0, previous = int.MinValue;
        int newest = int.MinValue, oldest = int.MaxValue;
        int covered = 0;
        for (int i = 0; i < dated.Count; i++)
        {
            var run = dated[i];
            if (run.Year != previous) { distinct++; previous = run.Year; }
            newest = Math.Max(newest, run.Year);
            oldest = Math.Min(oldest, run.Year);
            covered += run.Count;
        }
        if (distinct < 3 || covered <= MinBandItems) return null;

        int span = newest - oldest + 1;
        int target = Math.Clamp((int)MathF.Round(MathF.Sqrt(Math.Max(1, itemCount))), 3, MaxBands);
        int widthIndex = WidthIndexFor((float)span / target);
        List<WorkBand> planned;
        while (true)
        {
            planned = Bucket(dated, Widths[widthIndex]);
            CoalesceSparse(planned);
            if (planned.Count <= MaxBands || widthIndex == Widths.Length - 1) break;
            widthIndex++;
        }
        if (planned.Count < 2) return null;

        var result = new DiscographyEraBand[planned.Count];
        for (int i = 0; i < planned.Count; i++)
        {
            var band = planned[i];
            bool open = provisional && i == planned.Count - 1;
            result[i] = new DiscographyEraBand(Label(band.Newest, band.Oldest, open), band.Start, band.Count, open);
        }
        return result;
    }

    static List<DatedRun> Normalize(ReadOnlySpan<DiscographyYearRun> runs)
    {
        var result = new List<DatedRun>(runs.Length);
        int leadingStart = 0, leadingCount = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            var run = runs[i];
            if (run.Count <= 0) continue;
            if (run.Year <= 0)
            {
                if (result.Count == 0)
                {
                    if (leadingCount == 0) leadingStart = run.Start;
                    leadingCount += run.Count;
                }
                else
                {
                    var current = result[^1];
                    result[^1] = current with { Count = current.Count + run.Count };
                }
                continue;
            }

            int start = leadingCount > 0 ? leadingStart : run.Start;
            int count = run.Count + leadingCount;
            leadingCount = 0;
            result.Add(new DatedRun(run.Year, start, count));
        }
        if (leadingCount > 0 && result.Count > 0)
        {
            var current = result[^1];
            result[^1] = current with { Count = current.Count + leadingCount };
        }
        return result;
    }

    static int WidthIndexFor(float desired)
    {
        for (int i = 0; i < Widths.Length; i++)
            if (Widths[i] >= desired) return i;
        return Widths.Length - 1;
    }

    static List<WorkBand> Bucket(List<DatedRun> runs, int width)
    {
        var result = new List<WorkBand>();
        int key = int.MinValue;
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            int bucket = (run.Year / width) * width;
            if (bucket == key)
            {
                var current = result[^1];
                result[^1] = current with
                {
                    Newest = Math.Max(current.Newest, run.Year),
                    Oldest = Math.Min(current.Oldest, run.Year),
                    Count = current.Count + run.Count,
                };
                continue;
            }
            key = bucket;
            result.Add(new WorkBand(run.Year, run.Year, run.Start, run.Count));
        }
        return result;
    }

    static void CoalesceSparse(List<WorkBand> bands)
    {
        int i = 0;
        while (i < bands.Count - 1)
        {
            if (bands[i].Count >= MinBandItems) { i++; continue; }
            bands[i] = bands[i].Merge(bands[i + 1]);
            bands.RemoveAt(i + 1);
        }
        if (bands.Count > 1 && bands[^1].Count < MinBandItems)
        {
            bands[^2] = bands[^2].Merge(bands[^1]);
            bands.RemoveAt(bands.Count - 1);
        }
    }

    /// <summary>Invariant English matching <c>artist.disco.decade</c> / <c>decadeAndEarlier</c> /
    /// <c>decadeRange</c> / <c>yearRange</c>. The planner stays engine-free; the loc keys are the
    /// translator source of truth for those templates.</summary>
    static string Label(int newest, int oldest, bool openEnded)
    {
        if (openEnded)
            return ((newest / 10) * 10).ToString(CultureInfo.InvariantCulture) + "s and earlier";
        if (newest == oldest) return newest.ToString(CultureInfo.InvariantCulture);
        if (newest % 10 == 9 && oldest % 10 == 0 && newest - oldest == 9)
            return oldest.ToString(CultureInfo.InvariantCulture) + "s";
        if (newest % 10 == 9 && oldest % 10 == 0 && newest - oldest > 9)
            return ((newest / 10) * 10).ToString(CultureInfo.InvariantCulture) + "s–"
                   + ((oldest / 10) * 10).ToString(CultureInfo.InvariantCulture) + "s";
        return newest.ToString(CultureInfo.InvariantCulture) + "–" + oldest.ToString(CultureInfo.InvariantCulture);
    }
}

// ══ 3. THE ROUTE (ch 08 §8 DiscographyRoute; the codec itself is Shell.Parse's `disco:` arm) ═════════════════════════

/// <summary><c>disco:&lt;kind&gt;:&lt;artist uri&gt;</c>. The uri may itself contain <c>':'</c>, but the kind is ONE
/// leading digit, so the parse is unambiguous (kind at [6], uri from [8], as 0.2.9). In a <see cref="Shell.Route"/> the
/// artist is <see cref="Shell.Route.Subject"/> and the suffix <c>&lt;kind&gt;:&lt;uri&gt;</c> is <see cref="Shell.Route.Arg"/>
/// (Shell.cs's codec); the breadcrumb reads the artist's NAME off its row, so no display name rides the route.</summary>
public static class DiscoRoute
{
    public const string Prefix = "disco:";

    /// <summary>The route key 0.2.9 persisted: <c>disco:1:spotify:artist:…</c>.</summary>
    public static string Key(DiscoFacet facet, ReadOnlySpan<char> artistUri)
        => string.Concat(Prefix, ((int)facet).ToString(CultureInfo.InvariantCulture), ":", artistUri);

    /// <summary>Parse a whole key. False for anything that is not <c>disco:&lt;0-2&gt;:&lt;non-empty uri&gt;</c>.</summary>
    public static bool TryParseKey(ReadOnlySpan<char> key, out DiscoFacet facet, out ReadOnlySpan<char> artistUri)
    {
        facet = DiscoFacet.Albums;
        artistUri = default;
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        return TryParseArg(key[Prefix.Length..], out facet, out artistUri);
    }

    /// <summary>Parse the suffix a route's <c>Arg</c> carries: <c>&lt;digit&gt;:&lt;uri&gt;</c>.</summary>
    public static bool TryParseArg(ReadOnlySpan<char> suffix, out DiscoFacet facet, out ReadOnlySpan<char> artistUri)
    {
        facet = DiscoFacet.Albums;
        artistUri = default;
        if (suffix.Length < 3 || suffix[1] != ':' || suffix[0] is < '0' or > '2') return false;
        facet = (DiscoFacet)(suffix[0] - '0');
        artistUri = suffix[2..];
        return true;
    }

    /// <summary>The route to one facet of an artist's discography. Built through <see cref="Shell.Parse"/> so a key minted
    /// here and one minted by a deep link are the same route.</summary>
    public static Shell.Route For(Artist a, DiscoFacet facet) => Shell.Parse(Key(facet, a.Uri.Text));

    /// <summary>The facet and the artist a <see cref="Shell.RouteKind.Discography"/> route addresses.</summary>
    public static bool TryParse(in Shell.Route route, out DiscoFacet facet, out EntityUri artist)
    {
        facet = DiscoFacet.Albums;
        artist = default;
        if (route.Kind != Shell.RouteKind.Discography || route.Arg.IsEmpty) return false;
        if (!TryParseArg(Entities.Strings.Resolve(route.Arg), out facet, out _)) return false;
        artist = route.Subject;
        return artist.Id.Form != EntityForm.None;
    }
}

// ══ 4. THE CARD TEXT (ch 08 §8: DiscoGrid.AlbumMeta / ReleaseDateLabel / ReleaseYearLabel, ArtistPage.KindLabel) ═════

/// <summary>A discography card's subtitle ("Jun 12, 2026 · 17 tracks") — the ONE rule shared by the grid card and the
/// drawer head, so the two can never read different metadata for one album. Ch 08 §8 names <c>Album.cs</c> as its
/// eventual home (shared with ch 05); it lands here until owner M takes it.</summary>
public static class DiscoCardText
{
    /// <summary>The card subtitle off an album handle, in the app culture.</summary>
    public static string AlbumMeta(Album a)
        => a.IsValid
            ? AlbumMeta(Entities.Strings.Resolve(a.ReleaseDateIsoId), a.DatePrecision, a.Year, a.TrackCount, Culture())
            : "";

    /// <summary>"date · N tracks", "N tracks", "date" or "" — AlbumMeta verbatim over values.</summary>
    public static string AlbumMeta(string? iso, byte precision, ushort year, int trackCount, CultureInfo culture)
    {
        string date = ReleaseDateLabel(iso, precision, year, culture);
        return trackCount > 0
            ? date.Length > 0
                ? Strings.Artist.ReleaseMeta(date, Strings.Artist.TrackCount(trackCount))
                : Strings.Artist.TrackCount(trackCount)
            : date;
    }

    /// <summary>The release date off an album handle, in the app culture (the latest-release banner's date).</summary>
    public static string ReleaseDateLabel(Album album)
        => album.IsValid
            ? ReleaseDateLabel(Entities.Strings.Resolve(album.ReleaseDateIsoId), album.DatePrecision, album.Year, Culture())
            : "";

    /// <summary>The release date at the PROVIDER'S precision (Album.cs: 0 year · 1 month · 2 day): "2019", "Jun 2019",
    /// "Jun 12, 2019". An unparseable or absent ISO falls back to the year, then to "".</summary>
    public static string ReleaseDateLabel(string? iso, byte precision, ushort year, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(iso) ||
            !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "";
        return precision switch
        {
            0 => date.ToString("yyyy", culture),
            1 => date.ToString("MMM yyyy", culture),
            _ => date.ToString("MMM d, yyyy", culture),
        };
    }

    /// <summary>The year alone — the appears-on card's subtitle (the kind label stands in when it is 0).</summary>
    public static string ReleaseYearLabel(ushort year, string? iso)
    {
        if (year > 0) return year.ToString(CultureInfo.InvariantCulture);
        return iso is { Length: >= 4 } ? iso[..4] : "";
    }

    /// <summary>ArtistPage.KindLabel: the localized badge word. ONE table — Detail.Text owns it for the album eyebrow, and
    /// a second copy here is exactly how two surfaces would name one kind differently.</summary>
    public static string KindLabel(AlbumKind kind) => Detail.Text.KindLabel(kind);

    /// <summary>The app culture, falling back to invariant for a culture this runtime does not know.</summary>
    public static CultureInfo Culture()
    {
        try { return CultureInfo.GetCultureInfo(Loc.CurrentCulture); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}

// ══ 4b. THE CELL RULES (CORE, W2-A5): what one grid cell paints, as a value ══════════════════════════════════════════

/// <summary>What one discography cell paints, as ONE value a <c>Memo</c> gates on: the album slot the cell's position
/// resolves to (<see cref="Table.None"/> for a shimmer), that row's own version WHILE IT PAINTS A CARD (0 while it
/// shimmers, so an unrelated group landing on a not-yet-ready row leaves the value equal), and whether the row carries
/// <see cref="AlbumFields.DiscoCard"/>. A cell's render subscribes to this value alone — the facet edge and the album
/// table wake the memo on every publication, but the cell re-renders only when ITS row moved.</summary>
public readonly record struct DiscoCellStamp(int Slot, uint Version, bool Ready)
{
    /// <summary>A shimmer cell with no row behind it: no slot, no version, not ready.</summary>
    public static DiscoCellStamp Placeholder => new(Table.None, 0, false);
}

/// <summary>The placeholder-or-card decision for one cell of the discography grid, pure (ch 08 §7: the EDGE controls
/// the grid's geometry — <c>Total</c> — while each ROW decides whether a real, interactive card can paint, so one missing
/// release never blanks an otherwise usable facet or disables hover on the cards that already landed).</summary>
public static class DiscoCellRules
{
    /// <summary>The album at grid position <paramref name="index"/>, or <see cref="Table.None"/> while the facet has not
    /// answered at all (whatever the edge happens to list), the index lies past what has landed (a page still to come —
    /// shimmer-up-to-N), or the edge holds no target there.</summary>
    public static int TargetAt(EdgeState facetState, ReadOnlySpan<int> targets, int index)
        => facetState != EdgeState.Unknown && (uint)index < (uint)targets.Length && targets[index] > Table.None
            ? targets[index] : Table.None;

    /// <summary>The stamp for a resolved slot off the live album table: <see cref="DiscoCellStamp.Placeholder"/> for
    /// <see cref="Table.None"/> or a slot the table has not allocated; a versionless not-ready stamp naming the slot
    /// while the row lacks <see cref="AlbumFields.DiscoCard"/>; else the row's version and ready. Every write to the row
    /// bumps its version, so a card whose title, cover or meta changed re-renders, and a write to ANY OTHER row leaves
    /// this stamp equal.</summary>
    public static DiscoCellStamp StampOf(Table albums, int slot)
    {
        if (slot <= Table.None || slot >= albums.Count) return DiscoCellStamp.Placeholder;
        return albums.Knows(slot, (uint)AlbumFields.DiscoCard)
            ? new DiscoCellStamp(slot, albums.Version[slot], true)
            : new DiscoCellStamp(slot, 0, false);
    }
}

// ══ 5. THE DEMAND AND THE DRAWER ADAPTER (CORE) ══════════════════════════════════════════════════════════════════════

public readonly partial struct Artist
{
    /// <summary>The facet's section title: "Albums" / "Singles &amp; EPs" / "Compilations" (loc-resolved — 0.2.9's
    /// <c>DiscographyRoute.FacetTitle</c> returned raw English, ch 08 §6).</summary>
    public static string FacetTitle(DiscoFacet facet) => facet switch
    {
        DiscoFacet.Singles => Loc.Get(Strings.Artist.SinglesEps),
        DiscoFacet.Compilations => Loc.Get(Strings.Artist.Compilations),
        _ => Loc.Get(Strings.Artist.Albums),
    };

    /// <summary>The facet's relation in the current scope (ch 08 GAP 8: three edges, three states, three totals).</summary>
    public static EdgeTable<NoEdge> FacetEdge(DiscoFacet facet) => facet switch
    {
        DiscoFacet.Singles => Entities.Current.Edges.ArtistSingles,
        DiscoFacet.Compilations => Entities.Current.Edges.ArtistCompilations,
        _ => Entities.Current.Edges.ArtistAlbums,
    };

    /// <summary>The edge door's name for the facet (routed to <c>queryArtistDiscography{Albums,Singles,Compilations}</c>).</summary>
    public static FetchEdge FacetFetchEdge(DiscoFacet facet) => facet switch
    {
        DiscoFacet.Singles => FetchEdge.ArtistSingles,
        DiscoFacet.Compilations => FetchEdge.ArtistCompilations,
        _ => FetchEdge.ArtistAlbums,
    };

    /// <summary>Is the facet's section present? Somebody answered, and the server says it has releases.</summary>
    public static bool HasFacet(Artist a, DiscoFacet facet)
    {
        if (!a.IsValid) return false;
        var edge = FacetEdge(facet);
        return edge.State(a.Slot) != EdgeState.Unknown && edge.Total(a.Slot) > 0;
    }

    /// <summary>Whether the facet keeps a stable section shell while its edge is being answered. Unknown, Partial and
    /// Failed remain present so a later edge answer cannot insert a whole section into the middle of the page; a
    /// complete empty answer removes it permanently.</summary>
    public static bool FacetPresent(Artist a, DiscoFacet facet)
    {
        if (!a.IsValid) return false;
        var edge = FacetEdge(facet);
        return ArtistReadiness.ShelfPresent(edge.State(a.Slot), edge.Targets(a.Slot).Length);
    }

    /// <summary>The server's total for the facet (the profile-facts tile, the header meta, the shimmer count).</summary>
    public static int FacetTotal(Artist a, DiscoFacet facet) => a.IsValid ? FacetEdge(facet).Total(a.Slot) : 0;

    /// <summary>Demand every facet's page ONE (ch 08 §7: pages demand their whole model; the grid virtualizes RENDER,
    /// not FETCH — the whole model still arrives, just paced past page one by scroll, see
    /// <see cref="DemandNextPage"/>). Idempotent — call it from the page's demand effect, which re-runs as pages land.
    /// ALL THREE facets ask at <see cref="FetchPriority.Visible"/> (bug D): the artist page's tabs are a SCROLL-SPY
    /// over three sections stacked inline, never a view switch (ch 08 §9's <c>GoToSection</c>) — Albums, Singles and
    /// Compilations are simultaneously on screen the moment the page mounts, so all three are, in fact, Visible. A
    /// prior revision demoted Singles/Compilations to <see cref="FetchPriority.Prefetch"/> to cut a render-cost
    /// regression, but because re-asking an already-asked entry at a HIGHER priority is a no-op at four levels
    /// (<c>Fetch.Edges.cs:52</c>'s <c>WasAsked</c> dedup, <c>Fetch.cs:262</c>'s <c>~Asked</c> mask, the bucket key
    /// packing priority into its shape word, and <c>s_active</c> carrying no priority the runner re-reads — see the
    /// 2026-09-15 bug handoff §5), the facet the user is actually looking at stayed Prefetch FOREVER, and its two-hop
    /// chain (the edge page, then <see cref="AlbumFields.DiscoCard"/> for every release) took ~5 s behind whatever else
    /// was asked first. Page-2+ paging stays scroll-paced (<see cref="DemandNextPage"/>, driven by the grid's own realized
    /// window) — that part was always correct and is unrelated to this demotion.</summary>
    public static void DemandDiscography(Artist a)
    {
        DemandFacet(a, DiscoFacet.Albums, DiscographyRules.FirstPagePriority(DiscoFacet.Albums));
        DemandFacet(a, DiscoFacet.Singles, DiscographyRules.FirstPagePriority(DiscoFacet.Singles));
        DemandFacet(a, DiscoFacet.Compilations, DiscographyRules.FirstPagePriority(DiscoFacet.Compilations));
    }

    /// <summary>One facet's page ONE: while nobody has answered (and the last ask did not fail — a failed facet waits
    /// for its Retry, never re-arms itself from an effect), plus the card group of every release already listed. A
    /// page BEYOND the first is no longer asked from here (render-cost fix) — see <see cref="DemandNextPage"/>, which
    /// the grid's own realized window drives, so paging keeps pace with scrolling instead of racing the network.</summary>
    public static void DemandFacet(Artist a, DiscoFacet facet, FetchPriority priority = FetchPriority.Visible)
    {
        if (!a.IsValid) return;
        var edge = FacetEdge(facet);
        int slot = a.Slot;
        if (edge.State(slot) == EdgeState.Unknown)
        {
            if (!edge.IsFailed(slot)) Entities.EnsureEdge(FacetFetchEdge(facet), slot, 0, priority);
            return;                                        // nothing is listed yet, so there are no cards to ask for
        }
        var targets = edge.Targets(slot);
        if (targets.Length > 0)
            // `DiscoCard`, not `Card`: this grid never paints the billed-artist line (every card is already on its
            // own artist's page) and `ArtistStageRelease` never parses one off the discography payload — asking the
            // full `Card` group would add a needless third `AlbumV4` hop behind bug D's two, for the common case,
            // not an edge case (`AlbumFields.DiscoCard`'s doc, Album.cs).
            Entities.Ensure(Entities.Current.Albums, targets, (uint)AlbumFields.DiscoCard, priority);
    }

    /// <summary>Page N+1 of a facet: asked once the grid's realized window (<see cref="LazyGrid"/>'s own
    /// <c>ensureRange</c> contract, already leading the viewport by its overscan rows) reaches the tail of what has
    /// landed — not the instant the previous page lands. The whole-model rule survives (every page still arrives,
    /// eventually, complete); only the PACE changes, from "as fast as the network answers" to "as fast as the user
    /// scrolls". Idempotent: <c>WasAsked</c> keeps a repeat call a no-op.</summary>
    public static void DemandNextPage(Artist a, DiscoFacet facet, FetchPriority priority = FetchPriority.Visible)
    {
        if (!a.IsValid) return;
        var edge = FacetEdge(facet);
        int slot = a.Slot;
        if (edge.State(slot) != EdgeState.Partial) return;
        int next = edge.Count(slot);
        if (!edge.WasAsked(slot, next)) Entities.EnsureEdge(FacetFetchEdge(facet), slot, next, priority);
    }

    /// <summary>THE ARTIST HALF OF THE DRAWER VERDICT (WP-5.N contract §9): the 0.2.9 inputs, re-derived from the edge
    /// model. <c>AlbumTracks</c> Complete (or Partial with rows) IS "the loaded album is the selected one"; Unknown is
    /// "pending for THIS uri" (the placeholder, sized from the card's advertised count); Failed is loaded-and-empty with
    /// nothing pending — the 2-row Retry note. The card carries no thin tracks in 0.3, so that input is 0.</summary>
    public static DrawerVerdict DrawerVerdictFor(string selectedUri, EdgeState tracksReadiness, int tracksCount,
                                                 int cardTrackCount, int gridColumns)
    {
        bool failed = tracksReadiness == EdgeState.Failed;
        bool loaded = failed || tracksReadiness == EdgeState.Complete
                   || (tracksReadiness == EdgeState.Partial && tracksCount > 0);
        return DrawerVerdict.For(selectedUri, loaded ? selectedUri : null, failed ? 0 : Math.Max(0, tracksCount),
                                      thinTracks: 0, thinTrackCount: Math.Max(0, cardTrackCount), pending: !loaded,
                                      gridCols: gridColumns);
    }

    /// <summary>The verdict for an album handle, off the live tables.</summary>
    public static DrawerVerdict DrawerVerdictFor(Album album, string selectedUri, int gridColumns)
    {
        if (!album.IsValid || selectedUri.Length == 0) return default;
        var tracks = Entities.Current.Edges.AlbumTracks;
        return DrawerVerdictFor(selectedUri, tracks.Readiness(album.Slot), tracks.Count(album.Slot), album.TrackCount,
                                gridColumns);
    }

    // ══ 6. THE FACET SECTION (UI, ch 08 W14/W17) ═════════════════════════════════════════════════════════════════════

    static readonly string[] s_facetKeys = ["disco-facet:0", "disco-facet:1", "disco-facet:2"];
    static readonly string[] s_gridKeys = ["disco-grid:0", "disco-grid:1", "disco-grid:2"];
    static readonly Func<ColorF> s_themeAccent = static () => Tok.AccentDefault;

    /// <summary>The facet header row's own height; with the band above it, the grid's sticky clip inset.</summary>
    public const float FacetHeaderRowH = 40f;

    // MOUNT POINT (WP-5.N contract §4)
    /// <summary>The whole inline facet section of the artist page: the accent-spined header (an <c>Expander</c> whose
    /// header pins at <paramref name="pinTop"/>), the grid clipped under it with a 24-DIP feather, and the drawer. Reads
    /// the page scroll from <c>LazyScroll.Slot</c>. The PAGE wraps it in its own "sec:…" anchor box. An empty box when
    /// the facet has nothing.</summary>
    public static Element FacetSection(Artist a, DiscoFacet facet, Func<ColorF> accent, float pinTop, float expandedTopInset)
        => Embed.Comp(new FacetProps(a, facet, pinTop, expandedTopInset, accent), static () => new FacetHost())
           with { Key = s_facetKeys[(int)facet] };

    /// <summary>Equality is the re-render gate over DATA (the accent thunk is behaviour, read live).</summary>
    sealed record FacetProps(Artist A, DiscoFacet Facet, float PinTop, float ExpandedTopInset, Func<ColorF> Accent)
    {
        public bool Equals(FacetProps? o) => o is not null && o.A == A && o.Facet == Facet
                                          && o.PinTop.Equals(PinTop) && o.ExpandedTopInset.Equals(ExpandedTopInset);
        public override int GetHashCode() => HashCode.Combine(A.Slot, (int)Facet, PinTop, ExpandedTopInset);
    }

    sealed class FacetHost : Component
    {
        readonly Signal<LazyGridVisibleRange> _visible = new(default);

        readonly Action<LazyGridVisibleRange> _onVisible;
        readonly Func<ColorF> _accent;
        readonly Action _demand;
        readonly Func<ulong> _yearFold;                          // the era memo's value gate (UseComputed in Render)
        FacetProps? _p;
        TemplateParts? _parts;
        float _partsPin = float.NaN;

        // The era memo: years re-read into a pooled buffer, re-planned only when the year FOLD moved (zero allocation
        // otherwise). The fold is the render's gate too — see YearFold.
        ushort[] _years = new ushort[64];
        ulong _erasFold;
        DiscographyEraBand[]? _eras;

        public FacetHost()
        {
            _onVisible = r => { if (_visible.Peek() != r) _visible.Value = r; };
            _accent = () => _p is { } p ? p.Accent() : Tok.AccentDefault;
            _demand = Demand;
            _yearFold = YearFold;
        }

        /// <summary>The era memo's VALUE gate (W3-A4). A fold (<see cref="RowFold"/>) over the facet's album YEARS in facet
        /// order: the memo re-evaluates on every album publication it read, but notifies the render only when a year
        /// actually landed or moved — where the raw <c>Albums.Changed</c> read this replaces re-rendered all three facet
        /// hosts on every publication the page's tables drained. 0 while the facet is not Complete (no eras then; the
        /// seed itself is what a complete facet with no albums folds to).</summary>
        ulong YearFold()
        {
            _ = Entities.ScopeEpoch.Value;
            var p = UsePropsOrDefault<FacetProps>();
            if (p is null) return 0UL;
            var edge = FacetEdge(p.Facet);
            _ = edge.Changed.Value;
            var albums = Entities.Current.Albums;
            _ = albums.Changed.Value;
            if (edge.State(p.A.Slot) != EdgeState.Complete) return 0UL;
            var targets = edge.Targets(p.A.Slot);
            ulong fold = RowFold.Seed;
            for (int i = 0; i < targets.Length; i++)
                fold = RowFold.Add(fold, targets[i] > Table.None ? (uint)albums.Year[targets[i]] : 0u);
            return fold;
        }

        /// <summary>The facet's own demand (idempotent beside the page's): subscribes to the scope and the edge, so it
        /// re-runs as each page lands and asks for the next one.</summary>
        void Demand()
        {
            _ = Entities.ScopeEpoch.Value;
            if (_p is not { } p) return;
            _ = FacetEdge(p.Facet).Changed.Value;
            // Bug D: every inline facet is Visible (see DemandDiscography's doc) — all three sections are on screen
            // at once, so the Card backfill for whichever facet's page already landed asks at the same priority.
            DemandFacet(p.A, p.Facet);
        }

        public override Element Render()
        {
            var p = UsePropsOrDefault<FacetProps>();
            if (p is null) return new BoxEl();
            _p = p;
            _ = Entities.ScopeEpoch.Value;                       // FIRST: a scope switch re-points every table below
            var edge = FacetEdge(p.Facet);
            _ = edge.Changed.Value;
            // Value-gated, never the raw Albums.Changed counter: only a year landing re-renders this host (W3-A4).
            ulong yearFold = UseComputed(_yearFold).Value;

            var a = p.A;
            UseEffect(_demand);                                  // re-runs as pages land (it reads the edge's Changed)
            if (!FacetPresent(a, p.Facet)) return new BoxEl();

            // A failed facet must leave the shimmer.  The edge can retain a positive server total from the failed
            // page, so HasFacet alone is insufficient here: without this branch the grid has an extent but no targets
            // and renders placeholder cards indefinitely, which looks like a navigation/rendering failure.
            if (edge.IsFailed(a.Slot))
                return Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact,
                    onAction: () => Entities.RefreshEdge(FacetFetchEdge(p.Facet), a.Slot));

            int total = edge.Total(a.Slot);
            var eras = Eras(edge, a.Slot, yearFold);
            float inset = p.PinTop + FacetHeaderRowH;

            Element header = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Width = 3f, MinHeight = 22f, AlignSelf = FlexAlign.Stretch,
                        Corners = CornerRadius4.All(Radii.Pill), Fill = _accent(), HitTestVisible = false,
                    },
                    Embed.Comp(new FacetLabelProps(FacetTitle(p.Facet), total, eras), () => new FacetHeaderLabel(_visible))
                        with { Key = "facet-label" },
                    new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
                ],
            };
            Element grid = new BoxEl
            {
                Direction = 1,
                // Feathered exactly while the grid's own clip is engaged, on the render turn that poses it (WhileStuck —
                // RCA 2026-09-25 F(ii): a re-render off an engaged signal lagged the cut by two presents).
                EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.BandLayout.ClipFadeBand) { WhileStuck = true },
                // Bug D: no facet-identity demotion — the grid's own next-page paging stays scroll-paced (DemandNextPage
                // via _ensureRange/OnVisible below); only page ONE's priority changed, and it defaults to Visible.
                Children = [Grid(a, p.Facet, _accent, _onVisible, p.ExpandedTopInset)],
            }.StickyClip(inset);

            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    // AnimateContentResize off: a drawer opening inside the grid must not replay the disclosure tween —
                    // one click, one motion (ch 08 W17, parity 55).
                    Embed.Comp(new Expander.ExpanderSlots(header, grid, Parts(p.PinTop)),
                        static () => new Expander { InitiallyExpanded = true, Options = new ExpanderOptions { AnimateContentResize = false } }),
                    new BoxEl { Height = Spacing.XXL, HitTestVisible = false },
                ],
            };
        }

        DiscographyEraBand[]? Eras(EdgeTable<NoEdge> edge, int parent, ulong yearFold)
        {
            // A whole-catalogue grouping (ch 08 §7): only a COMPLETE facet has eras; a partial one keeps "N releases".
            if (edge.State(parent) != EdgeState.Complete) { _erasFold = 0UL; _eras = null; return null; }
            // The same years in the same order fold the same (YearFold is 0 only while not Complete): keep the plan.
            if (yearFold != 0UL && yearFold == _erasFold) return _eras;
            var targets = edge.Targets(parent);
            var albums = Entities.Current.Albums;
            if (_years.Length < targets.Length) _years = new ushort[Math.Max(targets.Length, _years.Length * 2)];
            for (int i = 0; i < targets.Length; i++)
                _years[i] = targets[i] > Table.None ? albums.Year[targets[i]] : (ushort)0;
            _erasFold = yearFold;
            _eras = DiscographyEraBands.PlanYears(_years.AsSpan(0, targets.Length));
            return _eras;
        }

        TemplateParts Parts(float pinTop)
        {
            if (_parts is not null && _partsPin.Equals(pinTop)) return _parts;
            _partsPin = pinTop;
            return _parts = new TemplateParts
            {
                [Expander.PartHeader] = element => element with
                {
                    MinHeight = FacetHeaderRowH,
                    Padding = new Edges4(0f, Spacing.XS, Spacing.S, Spacing.XS),
                    Fill = ColorF.Transparent,
                    HoverFill = ColorF.Transparent,
                    PressedFill = ColorF.Transparent,
                    BorderWidth = 0f,
                    Corners = CornerRadius4.All(0f),
                    BrushTransitionMs = 0f,
                    ScrollEffects = [new ScrollEffectSpec(ScrollEffect.Sticky(pinTop))],
                },
                [Expander.PartChevron] = element => element with
                {
                    Width = 28f, Height = 28f,
                    Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
                },
                [Expander.PartContent] = element => element with
                {
                    Padding = Edges4.All(0f), MinHeight = 0f, Margin = Edges4.All(0f),
                    Fill = ColorF.Transparent, BorderWidth = 0f,
                    Corners = CornerRadius4.All(0f),
                },
            };
        }
    }

    sealed record FacetLabelProps(string Title, int Total, DiscographyEraBand[]? Eras);

    /// <summary>The only reactive leaf in the pinned facet heading: a visible-window change replaces one text run; it
    /// never re-renders the Expander, reflows the grid or writes the page scroll (0.2.9 DiscographyFacetHeaderLabel).</summary>
    sealed class FacetHeaderLabel(IReadSignal<LazyGridVisibleRange> visible) : Component
    {
        public override Element Render()
        {
            var props = UsePropsOrDefault<FacetLabelProps>() ?? new FacetLabelProps("", 0, null);
            var range = visible.Value;
            string meta = props.Total > 0 ? Strings.Artist.ReleaseCount(props.Total) : "";
            if (props.Eras is { Length: > 0 } eras)
            {
                int index = range.LastIndexExclusive > range.FirstIndex ? range.FirstIndex : 0;
                if (DiscographyEraBands.AtIndex(eras, index) is { } era && era.Label.Length > 0)
                    meta = era.Label + " · " + Strings.Artist.ReleaseCount(era.Count);
            }
            return meta.Length > 0
                ? Design.Type.RailHeader(props.Title, meta)
                : (Element)(Design.Type.RailHeader(props.Title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }
    }

    // ══ 7. THE GRID (UI, ch 08 W14/W15; 0.2.9 DiscoGrid) ═════════════════════════════════════════════════════════════

    const float DiscoMinCol = 180f;
    const float DiscoGap = Spacing.L;
    /// <summary>The grid surface's height is the cell width + 50 (the square cover is the cell less its 2 × 8 padding, over the
    /// 8 gap, the title + caption lines and the 12 bottom pad) — one uniform card height, so the drawer's hug spacing is
    /// exact; the seed face pinned to it is exactly as tall. RowGap is the vertical gutter, folded into the LazyGrid row
    /// extra.</summary>
    const float DiscoCardChrome = 50f, DiscoRowGap = 20f;
    /// <summary><c>HeaderH + 2 × RowPitch</c> = 104: enough of the drawer to prove it opened (ch 08 §9 trap).</summary>
    const float DiscoRevealPeek = DrawerVerdict.HeaderH + 2f * DrawerVerdict.RowPitch;
    const float CaretW = 16f, CaretH = 8f, CaretOverlap = 1f;
    static readonly PathData s_caret = BuildCaret();

    static PathData BuildCaret()
    {
        var b = new PathBuilder();
        b.MoveTo(0f, CaretH);
        b.LineTo(CaretW * 0.5f, 0f);
        b.LineTo(CaretW, CaretH);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    /// <summary>Outer drawer slot: SIZE only, Reflow, 200 in / 150 out, descendants suppressed (one wave per click).</summary>
    static readonly LayoutTransition s_drawerResize = new(
        TransitionChannels.Size, TransitionDynamics.Tween(200f, Easing.SmoothOut),
        Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

    /// <summary>Inner panel: OPACITY only, 150 in / 100 out — an album switch cross-fades under the same slot.</summary>
    static readonly LayoutTransition s_drawerPresence = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(150f, Easing.EaseInOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(100f, Easing.EaseInOut));

    /// <summary>The facet's virtualized grid + inline drawer, shared by the facet section and the disco page.
    /// <paramref name="priority"/> is the paging priority for pages beyond the first (see
    /// <see cref="DemandNextPage"/>), defaulting to <see cref="FetchPriority.Visible"/> — the artist page's three
    /// inline facets (bug D) and the standalone <c>DiscographyPage</c> (which shows exactly one facet, always on
    /// screen) both pass it unchanged; nothing in this file demotes it any more.</summary>
    static Element Grid(Artist a, DiscoFacet facet, Func<ColorF> accent, Action<LazyGridVisibleRange>? onVisible,
                        float expandedTopInset, FetchPriority priority = FetchPriority.Visible)
        => Embed.Comp(new GridProps(a, facet, expandedTopInset, accent, onVisible, priority), static () => new DiscoGridHost())
           with { Key = s_gridKeys[(int)facet] };

    sealed record GridProps(Artist A, DiscoFacet Facet, float ExpandedTopInset, Func<ColorF> Accent,
                            Action<LazyGridVisibleRange>? OnVisible, FetchPriority Priority)
    {
        public bool Equals(GridProps? o) => o is not null && o.A == A && o.Facet == Facet
                                         && o.ExpandedTopInset.Equals(ExpandedTopInset) && o.Priority == Priority;
        public override int GetHashCode() => HashCode.Combine(A.Slot, (int)Facet, ExpandedTopInset, (int)Priority);
    }

    sealed class DiscoGridHost : Component
    {
        GridProps _p = null!;
        readonly Signal<int> _expandedSlot = new(Table.None);    // the IDENTITY (an album slot), never an ordinal
        readonly Signal<int> _expandedIndex = new(-1);           // LazyGrid's contract, re-derived every render
        readonly Func<int> _count;                               // the count memo's compute (UseComputed in Render)
        readonly Func<int, float, Element> _cell;
        readonly Action<int, int> _ensureRange;
        readonly Func<int, GridDrawerInfo, Element> _drawer;
        readonly Func<int, float> _drawerHeight;
        readonly Action<LazyGridVisibleRange> _visible;
        readonly Action<int> _toggle;                            // handed to every cell once; the cell invokes the newest
        readonly Action _demandTracks;
        readonly Action _retry;
        DrawerVerdict _verdict;                                  // the CACHED verdict DrawerHeight reads (ch 08 §9 trap)
        string _expandedUri = "";
        int _expandedUriSlot;

        public DiscoGridHost()
        {
            _count = Count;
            _cell = Cell;
            _ensureRange = EnsureRange;
            _drawer = DrawerFor;
            _drawerHeight = _ => _verdict.SlotHeight;
            _visible = r => _p.OnVisible?.Invoke(r);
            _toggle = Toggle;
            _demandTracks = DemandTracks;
            _retry = Retry;
        }

        public override Element Render()
        {
            var p = UseProps<GridProps>();
            _p = p;
            _ = Entities.ScopeEpoch.Value;
            var edge = FacetEdge(p.Facet);
            _ = edge.Changed.Value;
            int expanded = _expandedSlot.Value;
            int index = expanded <= Table.None || !p.A.IsValid ? -1 : edge.IndexOf(p.A.Slot, expanded);
            if (_expandedIndex.Peek() != index) _expandedIndex.Value = index;
            UseEffect(_demandTracks);
            // The churn-free count (W2-A5): a Memo re-evaluates Count() on every publication it read but notifies the
            // grid only when the TOTAL moved, so an album row landing behind a placeholder — or any other table
            // draining after a frame — never re-renders the grid or rebuilds its realized cells. The cell that paints
            // that row follows it on its own (DiscoCell). The grid pulls the memo first in its pass (LazyGrid.ReadCount).
            var count = UseComputed(_count);

            return Embed.Comp(() => new LazyGrid(
                count: count, cell: _cell, ensureRange: _ensureRange,
                // Two rows keep a fast wheel/touchpad move covered without realizing four extra rows of album cards
                // during navigation. The grid's range callback still asks the next visible rows before they enter.
                minColWidth: DiscoMinCol, gap: DiscoGap, rowExtra: DiscoCardChrome + DiscoRowGap, overscanRows: 2,
                expanded: _expandedIndex, drawer: _drawer, drawerHeight: _drawerHeight,
                onVisibleRangeChanged: _visible,
                expandedTopInset: _p.ExpandedTopInset, expandedRevealPeek: DiscoRevealPeek,
                // AlignTop, not Minimal: the clicked row always parks under the sticky band, so the drawer opens in the
                // SAME place on every click (ch 08 §9 must-not-simplify 4).
                reveal: ExpandedReveal.AlignTop));
        }

        /// <summary>The expanded album's tracklist: asked while nobody answered (and not failed — Retry re-asks), and
        /// its rows' <c>TrackFields.Row</c> once they are listed.</summary>
        void DemandTracks()
        {
            _ = Entities.ScopeEpoch.Value;
            int album = _expandedSlot.Value;
            var tracks = Entities.Current.Edges.AlbumTracks;
            _ = tracks.Changed.Value;
            if (album <= Table.None || album >= Entities.Current.Albums.Count) return;
            if (tracks.State(album) == EdgeState.Unknown)
            {
                if (!tracks.IsFailed(album)) Entities.EnsureEdge(FetchEdge.AlbumTracks, album);
                return;
            }
            var rows = tracks.Targets(album);
            if (rows.Length > 0) Entities.Ensure(Entities.Current.Tracks, rows, (uint)TrackFields.Row);
        }

        void Retry()
        {
            int album = _expandedSlot.Peek();
            if (album > Table.None) Entities.RefreshEdge(FetchEdge.AlbumTracks, album);
        }

        /// <summary>LazyGrid's own contract: the realized window (visible rows + its overscan), reported every time it
        /// moves. Render-cost fix seam (ch 08 §7 notwithstanding — this does NOT switch the facet to range-fetch; it only
        /// PACES page N+1 behind the user's scroll instead of the network's answer): once the realized window's tail
        /// reaches what has landed, ask for the next page at this grid's priority (<see cref="GridProps.Priority"/> —
        /// Visible for both the artist page's three inline facets and the standalone <c>DiscographyPage</c>'s one
        /// facet; bug D removed the below-the-fold Prefetch demotion this comment used to describe).</summary>
        void EnsureRange(int firstIndex, int lastIndexExclusive)
        {
            var p = _p;
            if (!p.A.IsValid) return;
            if (lastIndexExclusive >= FacetEdge(p.Facet).Count(p.A.Slot)) DemandNextPage(p.A, p.Facet, p.Priority);
        }

        void Toggle(int album) => _expandedSlot.Value = _expandedSlot.Peek() == album ? Table.None : album;

        /// <summary>The whole facet's extent (the server total — shimmer-up-to-N while Partial): the ONE value the grid
        /// subscribes to. Runs as the count memo's compute, so it reads the props through <see cref="UseProps{T}"/>
        /// (a re-pushed artist or facet re-evaluates it) rather than a field frozen at its first evaluation, and it
        /// reads the scope and the facet edge ONLY — never the album table: whether a cell can paint a real card is
        /// that cell's own verdict (<see cref="DiscoCellRules"/>), not the grid's, so a late album answer neither
        /// re-renders the grid nor blanks an otherwise usable facet.</summary>
        int Count()
        {
            _ = Entities.ScopeEpoch.Value;
            var p = UseProps<GridProps>();
            var edge = FacetEdge(p.Facet);
            _ = edge.Changed.Value;
            return p.A.IsValid ? edge.Total(p.A.Slot) : 0;
        }

        /// <summary>One realized cell: a <see cref="DiscoCell"/> under LazyGrid's own per-position wrapper (the grid keys
        /// each realized slot <c>lazy-cell:&lt;index&gt;</c>, so the cell needs no key of its own and no key string is
        /// built here), re-pushed its position and the two things only the grid knows — the cell width and whether the
        /// album at that position is the expanded one. The cell resolves its album itself: a page that lands after this
        /// render must reach a placeholder without a grid render, and only a moved total re-renders the grid.</summary>
        Element Cell(int index, float cardW)
        {
            var p = _p;
            var targets = p.A.IsValid ? FacetEdge(p.Facet).Targets(p.A.Slot) : default;
            // Untracked on purpose: the grid already re-renders through _expandedIndex when the expanded album moves,
            // and this runs inside LazyGrid's render, which must not subscribe to the slot signal as well.
            int expanded = _expandedSlot.Peek();
            bool isExpanded = expanded > Table.None && (uint)index < (uint)targets.Length && targets[index] == expanded;
            return Embed.Comp(new DiscoCellProps(p.A, p.Facet, index, cardW, isExpanded, p.Accent, _toggle), s_cellFactory);
        }

        /// <summary>The drawer: the verdict RECOMPUTED here (DrawerHeight read the cached one earlier in the same LazyGrid
        /// pass), the per-album panel keyed on the album, the caret apexed on the clicked card's column.</summary>
        Element DrawerFor(int index, GridDrawerInfo info)
        {
            var p = _p;
            var targets = FacetEdge(p.Facet).Targets(p.A.Slot);
            int album = (uint)index < (uint)targets.Length ? targets[index] : Table.None;
            if (album <= Table.None) return new BoxEl();
            var tracks = Entities.Current.Edges.AlbumTracks;
            _ = tracks.Changed.Value;                             // LazyGrid re-renders as the tracklist lands
            if (_expandedUriSlot != album) { _expandedUriSlot = album; _expandedUri = new Album(album).Uri.Text; }
            var al = new Album(album);
            _verdict = DrawerVerdictFor(al, _expandedUri, info.Columns);

            var panel = Embed.Comp(new DrawerProps(al, _verdict, p.Accent, _retry), static () => new DrawerPanel())
                with { Key = "drawer:" + _expandedUri };

            float panelWidth = info.Columns * info.CellWidth + MathF.Max(0f, info.Columns - 1) * info.Gap;
            float apexX = Math.Clamp(info.Left + info.CellWidth / 2f, Radii.Card, MathF.Max(Radii.Card, panelWidth - Radii.Card));
            float caretX = apexX - CaretW / 2f;
            float caretY = DrawerVerdict.TopGap - CaretH + CaretOverlap;   // sinks 1 DIP to hide the panel's top stroke
            return new BoxEl
            {
                Key = "disco-drawer", Direction = 1, Height = _verdict.SlotHeight, ClipToBounds = true, Animate = s_drawerResize,
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, Animate = s_drawerPresence,
                        Children =
                        [
                            new BoxEl { Direction = 1, Children = [new BoxEl { Height = DrawerVerdict.TopGap, HitTestVisible = false }, panel] },
                            new PathEl
                            {
                                OffsetX = caretX, OffsetY = caretY, Width = CaretW, Height = CaretH,
                                Geometry = s_caret, Fill = Tok.FillCardSecondary, Rule = FillRule.NonZero,
                            },
                            new PolylineStrokeEl
                            {
                                OffsetX = caretX, OffsetY = caretY, Width = CaretW, Height = CaretH,
                                P0 = new Point2(0f, CaretH), P1 = new Point2(CaretW * 0.5f, 0f), P2 = new Point2(CaretW, CaretH),
                                PointCount = 3, Color = Tok.StrokeCardDefault, Thickness = 1f, RoundCaps = false,
                            },
                        ],
                    },
                ],
            };
        }
    }

    // ══ 7b. THE CELL (UI, W2-A5): one realized grid cell, following its own row ══════════════════════════════════════

    /// <summary>What the grid re-pushes to one cell. Equality is DATA — the artist, the facet, the position, the width
    /// and the expanded flag; the two delegates are behaviour, always present, and never compared, so a fresh accent
    /// thunk or toggle handler schedules nothing (component-props contract: what a card renders is a function of its
    /// item, and the cell invokes the NEWEST delegates it was handed). Public only so the equality rule can be pinned
    /// by a fact (this assembly has no <c>InternalsVisibleTo</c>).</summary>
    public sealed record DiscoCellProps(Artist A, DiscoFacet Facet, int Index, float CardW, bool Expanded,
                                        Func<ColorF> Accent, Action<int> OnToggle)
    {
        public bool Equals(DiscoCellProps? o) => o is not null && o.A == A && o.Facet == Facet && o.Index == Index
                                              && o.CardW.Equals(CardW) && o.Expanded == Expanded;
        public override int GetHashCode() => HashCode.Combine(A.Slot, (int)Facet, Index, CardW, Expanded);
    }

    static readonly Func<DiscoCell> s_cellFactory = static () => new DiscoCell();

    /// <summary>One realized grid cell — a component, so the placeholder→card switch, a re-titled row and the expanded
    /// accent each repaint THIS cell and nothing else. Its render subscribes to exactly three things: its props (the
    /// grid's re-push, gated by <see cref="DiscoCellProps"/>), its own <see cref="DiscoCellStamp"/> (a memo over the
    /// scope, the facet edge and the album table that stays EQUAL until the row at this position moves — so a
    /// publication for any other album re-evaluates the memo and re-renders nothing) and, while expanded, the accent it
    /// paints (a palette landing repaints the one opened card). The handlers, the drag source and the menu factory are
    /// built ONCE per mount as fields and read the cell's current row; the <see cref="Controls.CardData"/> and the meta
    /// are rebuilt only when the stamp names a different row or version (or the card width moves), never per render.
    /// The card's shell controls — its pinned height, the opened skin, the menu — travel IN the data: the shared surface
    /// (<c>Controls.Surface</c>, <c>Shape.Grid</c>) is a component whose chrome mounts lazily, so nothing here post-processes
    /// a BoxEl any more. A cell with no row yet is that same surface's SEED face at the card's pinned height.</summary>
    sealed class DiscoCell : Component
    {
        DiscoCellProps _p = null!;
        readonly Func<DiscoCellStamp> _stamp;
        readonly Action _click, _play;
        readonly Func<object?> _payload;
        readonly Func<ContextMenuModel?> _menu;
        readonly Func<ColorF> _accent;
        readonly DragSource _drag;
        // The row this cell paints and the data built from it — rebuilt when the stamp's slot or version moves.
        int _slot = Table.None;
        uint _version;
        string _uri = "", _title = "", _meta = "";
        string? _cover;
        // The two data variants — plain and OPENED — cached so an expanded cell re-pushes one reference per render
        // (CardData equality short-circuits on ReferenceEquals) instead of cloning a record; both die with Rebuild.
        Controls.CardData? _data, _dataSelected;
        // The seed (no row yet), pinned to the card's height and re-cut only when the width moves it.
        Controls.CardData? _seed;

        public DiscoCell()
        {
            _stamp = Stamp;
            _click = () => { if (_slot > Table.None) _p.OnToggle(_slot); };
            _play = () => { if (_slot > Table.None) Playback.PlayOrToggleContext(new Album(_slot).Id); };
            _payload = () => _slot > Table.None
                ? new DragPayload(Drag.KindOf(EntityKind.Album), _uri, _uri, _title, new EntityRef(EntityKind.Album, _slot), ArtUrl: _cover)
                : null;
            _drag = Drag.Source(_payload);
            _menu = () => AlbumMenu();
            // A trampoline into the NEWEST pushed accent thunk (DiscoCellProps never compares it), so the cached opened
            // variant can hold one delegate for its lifetime.
            _accent = () => _p.Accent();
        }

        /// <summary>The memo's compute: the row at this cell's position, as a value (<see cref="DiscoCellRules"/>). Reads
        /// the props through <see cref="UseProps{T}"/> so a re-pushed position, artist or facet re-evaluates it too.</summary>
        DiscoCellStamp Stamp()
        {
            _ = Entities.ScopeEpoch.Value;                       // FIRST: a scope switch re-points every table below
            var p = UseProps<DiscoCellProps>();
            var edge = FacetEdge(p.Facet);
            _ = edge.Changed.Value;
            var albums = Entities.Current.Albums;
            _ = albums.Changed.Value;
            if (!p.A.IsValid) return DiscoCellStamp.Placeholder;
            int slot = DiscoCellRules.TargetAt(edge.State(p.A.Slot), edge.Targets(p.A.Slot), p.Index);
            return DiscoCellRules.StampOf(albums, slot);
        }

        public override Element Render()
        {
            var p = UseProps<DiscoCellProps>();
            _p = p;
            var stamp = UseComputed(_stamp).Value;
            if (!stamp.Ready)
            {
                _slot = Table.None;
                _data = null;
                _dataSelected = null;
                // The pinned height keeps the seed the SAME height as a real card (the grid's folded row gap must not
                // stretch it). Keyed like the card below, so the row landing is a props re-push into the same host.
                float seedHeight = p.CardW + DiscoCardChrome;
                if (_seed is null || !_seed.Height.Equals(seedHeight)) _seed = Controls.CardData.Seed with { Height = seedHeight };
                return Controls.Surface(_seed, Shape.Grid) with { Key = "album" };
            }
            // The PINNED height is part of the data (it is the shell's), so a responsive width change rebuilds it too.
            float height = p.CardW + DiscoCardChrome;
            if (_data is null || _slot != stamp.Slot || _version != stamp.Version || !_data.Height.Equals(height))
                Rebuild(in stamp, height);

            // The opened card wears the accent border + the brighter fill; with the caret it answers "where did it
            // open" (ch 08 §9 must-not-simplify 4). The grid resolves the flag off the album SLOT (identity, never an
            // index); the host reads the accent inside ITS render, so a palette landing repaints the one expanded card
            // and no other.
            Controls.CardData data = _data!;
            if (p.Expanded) data = _dataSelected ??= data with { Selected = true, SelectedAccent = _accent, Subtitle = SubtitleOf(_meta, Icons.ChevronUp) };
            // A constant key, shared with the seed: the grid's own per-position wrapper already carries the identity, so
            // no per-render key string is built, and a seed hydrating is a props re-push, never a remount.
            return Controls.Surface(data, Shape.Grid) with { Key = "album" };
        }

        /// <summary>The card's data off its row — once per (slot, version, height), never per render. The pinned
        /// height is what keeps the card OUT of the grid's folded row gap (its shell would otherwise grow into it), and
        /// the menu rides along so the host attaches it to the shell.</summary>
        void Rebuild(in DiscoCellStamp stamp, float height)
        {
            var al = new Album(stamp.Slot);
            _slot = stamp.Slot;
            _version = stamp.Version;
            _uri = al.Uri.Text;
            _title = al.Title;
            _meta = DiscoCardText.AlbumMeta(al);
            _cover = Controls.ArtUrl(al.ImageId);
            _data = new Controls.CardData(_uri, _title, SubtitleOf(_meta, Icons.ChevronDown),
                _cover, _click, OnPlay: _play, Drag: _drag) { Height = height, Menu = _menu };
            _dataSelected = null;
        }

        /// <summary>The meta line with a small tertiary disclosure chevron after it: pointing down while the card can open
        /// its drawer, up while it is the open one (<see cref="Controls.CardData.Selected"/> carries the accent skin; the
        /// glyph is the quiet "this expands" cue the plain card never had).</summary>
        static Element SubtitleOf(string meta, string glyph) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, MinWidth = 0f,
            Children =
            [
                Design.Type.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                Icon(glyph, 9f, Tok.TextTertiary),
            ],
        };

        /// <summary>The album card menu: the shared container menu (strip + rows decided by <c>ContainerMenuRules</c>),
        /// built at OPEN from the cell's current row.</summary>
        ContextMenuModel? AlbumMenu()
        {
            var al = new Album(_slot);
            return al.IsValid ? Menus.Container(ActionTarget.ForAlbum(al.Uri, _title), _cover, _meta) : null;
        }
    }

    // ══ 8. THE DRAWER PANEL (UI, ch 08 W15/W16; 0.2.9 AlbumDrawerPanel) ══════════════════════════════════════════════

    /// <summary>The per-album drawer, re-pushed from the grid: the album, THE verdict, the accent and the retry.
    /// Equality is DATA (album + verdict); the two delegates are behaviour.</summary>
    sealed record DrawerProps(Album Album, DrawerVerdict Verdict, Func<ColorF> Accent, Action Retry)
    {
        public bool Equals(DrawerProps? o) => o is not null && o.Album == Album && o.Verdict.Equals(Verdict);
        public override int GetHashCode() => HashCode.Combine(Album.Slot, Verdict);
    }

    /// <summary># 26 · ♥ 28 · title ★ · time 44 · … 32 at the 32-DIP <c>DrawerVerdict.RowPitch</c> (content 28).</summary>
    static readonly Track.ColumnSet s_drawerCols = new(Album: false, By: false, Date: false, Video: false, Plays: false,
                                                        Heart: true, Thumb: false);
    static readonly TrackSize[] s_drawerColumns =
        [TrackSize.Px(26f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(), TrackSize.Px(44f), TrackSize.Px(32f)];
    const float DrawerRowContentH = 28f;

    /// <summary>The drawer rows' menu: the album IS the context, so "Go to album" would open the page the drawer is a
    /// preview of — omitted. Built with named arguments (the option structs skip their defaults under <c>default</c>).</summary>
    static readonly Track.MenuOptions s_drawerMenu = new(ShowGoToAlbum: false);

    sealed class DrawerPanel : Component
    {
        // Per-ALBUM state: the panel is keyed "drawer:" + uri, so a different album is a fresh selection. The model is
        // handed to the rows' ItemsView (it sizes ItemCount to the CELLS, the Show-all row included — every count below
        // therefore clamps to Verdict.Shown, and IsItemSelectable keeps Show-all out of interaction and Ctrl+A).
        internal readonly SelectionModel Sel = new() { Mode = ItemsSelectionMode.Extended };
        readonly Action _playAlbum, _exit, _selectAll;
        readonly Func<RowScope, Element> _slotTemplate;
        readonly ListOptions _listOptions;
        readonly Func<int, Track> _trackAt;
        readonly Func<IReadOnlyList<Track>> _selectedTracks;
        readonly Func<int, Element> _lane;
        readonly List<int> _dragIndices = new(8);
        internal readonly Action GoAlbum;
        internal readonly Func<bool> ChecksVisible;
        internal readonly Func<ColorF> AccentNow;
        DrawerProps _p = null!;
        IOverlayService? _overlay;
        // The rows' ItemsView is mounted once per GEOMETRY (columns x rows-per-column x cells): its layout is frozen at
        // mount, so a changed geometry is a keyed remount (the selection model is ours and survives it).
        Element? _list;
        int _geomCols = -1, _geomRows = -1, _geomCells = -1;

        public DrawerPanel()
        {
            _playAlbum = () => { if (_p.Album.IsValid) Playback.PlayOrToggleContext(_p.Album.Id); };
            GoAlbum = () => { if (_p.Album.IsValid) Shell.GoTo(Shell.For(_p.Album.Uri, _p.Album.Title)); };
            AccentNow = () => _p.Accent();
            _trackAt = TrackAt;
            _selectedTracks = SelectedTracks;
            _exit = Sel.DeselectAll;
            _selectAll = SelectAllShown;
            ChecksVisible = () => { _ = Sel.Version.Value; return SelectionBarRules.ChecksVisible(SelectedShownCount(), false); };
            _lane = fit => Track.SelectionLane(fit, SelectionLaneArgs.ForTracks(SelectedShownCount(), _selectedTracks, _exit, _selectAll));
            _slotTemplate = scope => Embed.Comp(() => new DrawerSlot(this, scope));
            _listOptions = new ListOptions
            {
                SelectionMode = ItemsSelectionMode.Extended,
                Selection = Sel,
                Selector = SelectorVisual.None,
                IsItemInvokedEnabled = true,
                OnInvoked = InvokeAt,
                IsItemSelectable = IsSelectable,
                Grow = 1f,
                Scroll = new ScrollOptions { SuppressScrollBar = true },
            };
        }

        internal DrawerProps Current => _p;
        internal IOverlayService? OverlayHost => _overlay;
        bool IsSelectable(int index) => index < _p.Verdict.Shown;

        public override Element Render()
        {
            var p = UsePropsOrDefault<DrawerProps>();
            if (p is null) return new BoxEl();
            _p = p;
            _overlay = UseContext(Overlay.Service);
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Edges.AlbumTracks.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            var v = p.Verdict;

            Element body = v.ReadyEmpty ? EmptyNote(p.Retry)
                         : v.Loading ? BuildColumns(v.Shown, v.Columns, s_shimmer)
                         : RowsList(v);
            return new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Padding = new Edges4(12f, 6f, 12f, 6f),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    Head(p),
                    // The standard selection bar floats over the drawer's own rows, 8 up from the bottom, once the
                    // selection earns it (SelectionBarRules.Visible: two or more rows). It is a component of its own, so a
                    // select/clear re-renders only the bar and the bound row chrome - never this panel or the list.
                    ZStack(body, Embed.Comp(() => new DrawerBar(this)) with { Key = "drawer-bar" }),
                ],
            };
        }

        Element Head(DrawerProps p)
        {
            var al = p.Album;
            ColorF accent = p.Accent();
            var titleType = Design.Type.DenseTitle("");
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = 36f,
                Children =
                [
                    Controls.Named(Controls.PlayFab(_playAlbum, Icons.Play, 32f), Loc.Get(Strings.Detail.Play)),
                    Controls.Artwork(Controls.ArtUrl(al.ImageId), 28f, 28f, Radii.Control, decodePx: 56),
                    new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f, OnClick = GoAlbum, Cursor = CursorId.Hand,
                        Children =
                        [
                            new SpanTextEl(
                            [
                                new TextSpan(al.Title),
                                new TextSpan(" · " + DiscoCardText.AlbumMeta(al), Weight: 400, Color: Tok.TextSecondary, Size: Ui.Caption("").Size),
                            ])
                            {
                                Size = titleType.Size, LineHeight = titleType.LineHeight, Weight = titleType.ResolvedWeight,
                                Color = Tok.TextPrimary,
                                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
                            },
                        ],
                    },
                    Controls.Named(new BoxEl
                    {
                        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(14f), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                        OnClick = GoAlbum, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                        HoverFill = Tok.FillSubtleSecondary,
                        Children = [Icon(Icons.OpenInNewWindow, 11.76f, Tok.TextSecondary)],
                    }, Loc.Get(Strings.Detail.GoToAlbum)),
                ],
            };
        }

        /// <summary>The track rows (and the "Show all" cell) as ONE bound list: a vertical stack for one column, or a
        /// column-major fill-row grid (the FIRST ceil(cells / 2) cells down the left, as the verdict's row count assumes)
        /// for two. The list is exactly <c>rows x RowPitch</c> tall, so nothing scrolls and the reserved slot height holds.</summary>
        Element RowsList(DrawerVerdict v)
        {
            int cells = v.Shown + (v.ShowAllRow ? 1 : 0);
            if (cells <= 0) return new BoxEl();
            int cols = v.Columns <= 1 ? 1 : v.Columns;
            int rows = Math.Max(1, v.Rows);
            if (_list is null || _geomCols != cols || _geomRows != rows || _geomCells != cells)
            {
                _geomCols = cols; _geomRows = rows; _geomCells = cells;
                // gap 0: the columns butt together and the slot carries the gutter, so row stride == RowPitch exactly.
                RepeatLayout layout = cols == 1
                    ? RepeatLayout.Stack(DrawerVerdict.RowPitch)
                    : RepeatLayout.Custom(new FillRowVirtualLayout(1f, 99999f, 0f, rows, perPageOverride: cols), horizontal: true);
                _list = ItemsView.CreateBound(cells, _slotTemplate, layout, _listOptions)
                    with { Key = "rows:" + cols + "x" + rows + ":" + cells };
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Height = rows * DrawerVerdict.RowPitch, Children = [_list] };
        }

        internal Track TrackAt(int index)
        {
            var tracks = Entities.Current.Edges.AlbumTracks.Targets(_p.Album.Slot);
            return (uint)index < (uint)Math.Min(tracks.Length, _p.Verdict.Shown) ? new Track(tracks[index]) : default;
        }

        /// <summary>The list's invoke (Enter, double-click): a track row plays the album FROM that track, the Show-all
        /// cell opens the album.</summary>
        void InvokeAt(int index)
        {
            if (index >= _p.Verdict.Shown) { GoAlbum(); return; }
            PlayFrom(index);
        }

        void PlayFrom(int index)
        {
            var t = TrackAt(index);
            if (t.Slot <= Table.None || !_p.Album.IsValid) return;
            var album = _p.Album;
            Track.Invoke(t, () => Playback.PlayContext(album.Id, t.Id));
        }

        internal ContextMenuModel? MenuFor(int index) => Track.RowMenu(Sel, index, _trackAt, static _ => -1, s_drawerMenu);

        /// <summary>The whole SELECTION when the gesture starts on a selected row, else that one track.</summary>
        internal DragPayload? DragPayloadFor(int index)
        {
            var t = TrackAt(index);
            if (t.Slot <= Table.None) return null;
            SelectionDrag.Indices(Sel, index, _dragIndices);
            var picked = new List<Track>(_dragIndices.Count);
            for (int k = 0; k < _dragIndices.Count; k++)
                if (TrackAt(_dragIndices[k]) is { Slot: > Table.None } s) picked.Add(s);
            if (picked.Count == 0) picked.Add(t);
            return new DragPayload(DragKind.Track, t.Uri.Text, t.Uri.Text, t.Title, new EntityRef(EntityKind.Track, t.Slot),
                                   Tracks: picked.ToArray());
        }

        /// <summary>Selected rows that are TRACKS (the model also spans the Show-all cell).</summary>
        internal int SelectedShownCount()
        {
            int shown = _p?.Verdict.Shown ?? 0;
            int n = 0;
            for (int r = 0; r < Sel.RangeCount; r++)
            {
                var (s, e) = Sel.GetRange(r);
                if (s >= shown) break;
                n += Math.Min(e, shown - 1) - s + 1;
            }
            return n;
        }

        IReadOnlyList<Track> SelectedTracks()
        {
            var picked = new List<Track>(SelectedShownCount());
            for (int r = 0; r < Sel.RangeCount; r++)
            {
                var (s, e) = Sel.GetRange(r);
                for (int i = s; i <= e; i++)
                    if (TrackAt(i) is { Slot: > Table.None } t) picked.Add(t);
            }
            return picked;
        }

        void SelectAllShown()
        {
            Sel.DeselectAll();
            int shown = _p.Verdict.Shown;
            if (shown > 0) Sel.SelectRange(0, shown - 1);
        }

        internal Func<int, Element> Lane => _lane;

        /// <summary>READY BUT EMPTY — offline, a failed read, a trackless album: a fixed 2-row note with a Retry that
        /// re-asks the tracklist while the drawer stays open (W16).</summary>
        static Element EmptyNote(Action retry) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M),
            Children =
            [
                Design.Type.DenseMeta(Loc.Get(Strings.Detail.Empty.NoTracks))
                    with { Grow = 1f, Basis = 0f, MinWidth = 0f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Button.Standard(Loc.Get(Strings.Common.Retry), retry),
            ],
        };

        static readonly Func<int, Element> s_shimmer = ShimmerRow;

        /// <summary>The placeholder row at the SAME pitch and column split as the real rows — nothing reflows on land.</summary>
        static Element ShimmerRow(int i) => new BoxEl
        {
            Key = "shimmer:" + i.ToString(CultureInfo.InvariantCulture),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = DrawerVerdict.RowPitch,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children =
            [
                new BoxEl { Width = 16f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                new BoxEl { Grow = 1f, Basis = 0f, Height = 11f, MaxWidth = 240f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                new BoxEl { Width = 30f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                new BoxEl { Width = 32f },
            ],
        };

        static Element BuildColumns(int cellCount, int columns, Func<int, Element> cell)
        {
            if (cellCount <= 0) return new BoxEl();
            var kids = new Element[cellCount];
            for (int i = 0; i < cellCount; i++) kids[i] = cell(i);
            return Split(kids, columns);
        }

        /// <summary>One stack, or two parallel column-major stacks (the FIRST ⌈n/2⌉ cells go left) — the ONE splitter the
        /// rows and the shimmer share, so neither can disagree with the verdict's ⌈cells / columns⌉ rows.</summary>
        static Element Split(Element[] cells, int columns)
        {
            if (cells.Length == 0) return new BoxEl();
            if (columns <= 1) return new BoxEl { Direction = 1, Children = cells };
            int perColumn = (cells.Length + columns - 1) / columns;
            var cols = new Element[columns];
            for (int c = 0; c < columns; c++)
            {
                int start = c * perColumn, end = Math.Min(cells.Length, start + perColumn);
                cols[c] = new BoxEl
                {
                    Key = "col:" + c.ToString(CultureInfo.InvariantCulture), Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = end > start ? cells[start..end] : [],
                };
            }
            return new BoxEl { Direction = 0, Gap = Spacing.XL, Children = cols };
        }
    }

    /// <summary>The drawer's floating selection bar - its own component, so selecting re-renders only it.</summary>
    sealed class DrawerBar : Component
    {
        readonly DrawerPanel _panel;
        public DrawerBar(DrawerPanel panel) => _panel = panel;

        public override Element Render()
        {
            _ = _panel.Sel.Version.Value;
            int n = _panel.SelectedShownCount();
            return Controls.SelectionBar(SelectionBarRules.Visible(n, false) ? n : 0, _panel.Lane, standalone: true, bottomPadding: Spacing.S);
        }
    }

    /// <summary>One bound slot of the drawer's list: a track row (check lane, the album's track cells, accent pill) or
    /// the "Show all" cell. The slot ROOT owns the gesture (press / Enter / Space through the row scope's interaction
    /// funnel) and focus; the cells inside are click-less content.</summary>
    sealed class DrawerSlot : Component
    {
        readonly DrawerPanel _o;
        readonly RowScope _scope;
        readonly Action<PointerEventArgs> _release;
        readonly Action<KeyEventArgs> _key;
        readonly Func<ContextMenuModel?> _menu;
        readonly DragSource _drag;
        readonly Prop<float> _pillOpacity;
        readonly Prop<ColorF> _pillFill;
        Action<PointerEventArgs>? _showAllRelease;
        Action<KeyEventArgs>? _showAllKey;
        static readonly Func<DrawerRowHost> s_rowFactory = static () => new DrawerRowHost();
        static readonly LayoutTransition s_checkShift = new(
            TransitionChannels.Position, TransitionDynamics.Tween(333f, Easing.FluentDecelerate));

        public DrawerSlot(DrawerPanel owner, RowScope scope)
        {
            _o = owner;
            _scope = scope;
            var interact = scope.OnInteraction;
            var checks = owner.ChecksVisible;
            var isSel = scope.IsSelected;
            _release = args =>
            {
                if (RowClickPolicy.TriggerOf(args.ClickCount, args.Mods) == ItemContainerTrigger.DoubleTap)
                    interact(ItemContainerTrigger.DoubleTap, args.Mods);
                else
                    interact(ItemContainerTrigger.Tap, SelectorVisualsBound.MultiSelectMods(checks(), args.Mods));
            };
            _key = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat)
                {
                    interact(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(checks(), args.Mods));
                    args.Handled = true;
                }
            };
            _menu = () => owner.MenuFor(scope.Index.Peek());
            _drag = Drag.Source(() => owner.DragPayloadFor(scope.Index.Peek()));
            _pillOpacity = Prop.Of(() => isSel() && !checks() ? 1f : 0f);
            _pillFill = Prop.Of(owner.AccentNow);
        }

        public override Element Render()
        {
            int index = _scope.Index.Value;                       // a recycle writes this: exactly this slot re-renders
            var overlay = UseContext(Overlay.Service);
            var p = _o.Current;
            var v = p.Verdict;
            if (index >= v.Shown)
                return v.ShowAllRow && index == v.Shown ? ShowAll(v.Total) : new BoxEl();

            // 2-column gutter: the layout butts the columns together, the slot insets its inner edge.
            Edges4 margin = v.Columns <= 1 ? default
                : index / Math.Max(1, v.Rows) == 0 ? new Edges4(0f, 0f, Spacing.M, 0f) : new Edges4(Spacing.M, 0f, 0f, 0f);
            var skin = new BoxEl
            {
                // Height, not MinHeight: the slot IS the 32-DIP pitch, so a column totals EXACTLY rows x RowPitch.
                ZStack = true, Height = DrawerVerdict.RowPitch, ClipToBounds = true, Corners = Radii.ControlAll, Margin = margin,
                Fill = ColorF.Transparent, HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                Focusable = false,                                // the ItemsView roving effect owns the one tab stop
                FocusVisualMargin = Design.FocusInsetRow,
                Draggable = _drag,
                OnPointerReleased = _release,
                OnKeyDown = _key,
                OnFocusChanged = _scope.OnFocusChanged,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Animate = s_checkShift,
                        Children =
                        [
                            SelectorVisualsBound.BoundCheckLane(_o.ChecksVisible, _scope.IsSelected, _scope.OnInteraction, leftMargin: 4f),
                            new BoxEl
                            {
                                Grow = 1f, Basis = 0f, MinWidth = 0f,
                                Children = [Embed.Comp(new DrawerRowProps(p.Album, index), s_rowFactory) with { Key = "content" }],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Key = "pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
                        Corners = CornerRadius4.All(1.5f), AlignSelf = FlexAlign.Center, Fill = _pillFill, HitTestVisible = false,
                        Opacity = _pillOpacity,
                    },
                ],
            };
            // Right-click / long-press / the row's "...": the selection-aware track menu (Explorer semantics).
            return Controls.IsNullOverlay(overlay) ? skin : skin.WithContextMenu(overlay, _menu);
        }

        /// <summary>The "Show all N tracks" cell: not selectable (the list's IsItemSelectable), opens the album on a
        /// click, Enter or Space.</summary>
        Element ShowAll(int total) => new BoxEl
        {
            Key = "row:show-all", Height = DrawerVerdict.RowPitch, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Cursor = CursorId.Hand, Role = AutomationRole.Button,
            Corners = Radii.ControlAll, HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
            Focusable = false, FocusVisualMargin = Design.FocusInsetRow,
            OnPointerReleased = _showAllRelease ??= _ => _o.GoAlbum(),
            OnKeyDown = _showAllKey ??= args =>
            {
                if (args.KeyCode == Keys.Enter || (args.KeyCode == Keys.Space && !args.IsRepeat)) { _o.GoAlbum(); args.Handled = true; }
            },
            OnFocusChanged = _scope.OnFocusChanged,
            Children = [Ui.Caption(Strings.Detail.Discography.ShowAllTracks(total)) with { Weight = 600, Color = Tok.AccentTextPrimary }],
        };
    }

    sealed record DrawerRowProps(Album Album, int Index);

    /// <summary>One drawer track cell — a component, so a play/pause/skip anywhere re-skins only THIS row. The track is
    /// read LIVE off the album's edge at its fixed index every render.</summary>
    sealed class DrawerRowHost : Component
    {
        DrawerRowProps? _p;
        readonly Action _play, _like;

        public DrawerRowHost()
        {
            _play = () => { if (_p is { } p && TrackOf(p) is { Slot: > Table.None } t) Track.Invoke(t, () => Playback.PlayContext(p.Album.Id, t.Id)); };
            _like = () =>
            {
                if (_p is not { } p || TrackOf(p) is not { Slot: > Table.None } t) return;
                var me = User.Me;
                if (me.Slot <= Table.None) return;
                if (me.Likes(t)) me.Unlike(t); else me.Like(t);
            };
        }

        static Track TrackOf(DrawerRowProps p)
        {
            var rows = Entities.Current.Edges.AlbumTracks.Targets(p.Album.Slot);
            return (uint)p.Index < (uint)rows.Length ? new Track(rows[p.Index]) : default;
        }

        public override Element Render()
        {
            var p = UsePropsOrDefault<DrawerRowProps>();
            if (p is null) return new BoxEl();
            _p = p;
            _ = Entities.Current.Tracks.Changed.Value;
            _ = Entities.Current.Edges.AlbumTracks.Changed.Value;
            var t = TrackOf(p);
            if (t.Slot <= Table.None) return new BoxEl();
            var st = Track.StateOf(t);
            Element title = Design.Type.DenseTitle(t.Title)
                with
                {
                    Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                };
            return Track.Grid(t, p.Index, in st, in s_drawerCols, s_drawerColumns, DrawerRowContentH, title,
                              new Track.GridOptions(OnPlay: _play, OnLike: _like, ActionsCell: Controls.MoreButton(null)));
        }
    }

    // ══ 9. THE DISCOGRAPHY PAGE (UI, ch 08 W22; 0.2.9 DiscographyPage) ═══════════════════════════════════════════════

    // MOUNT POINT (WP-5.N contract §4) — RouteKind.Discography
    /// <summary><c>disco:&lt;kind&gt;:&lt;artist&gt;</c>: the shared PageHead (CrumbTitleViews, 200, in every data state: a
    /// breadcrumb "Artist › Albums", the facet title, the facet views bar) and the SAME grid + drawer. Deliberately NO cover
    /// palette (system accent) and NO shell tint — the two visible 0.2.9 differences ch 08 W22 records (parity 88-89); the
    /// route row does not claim material.
    /// <para>The page is keyed by the ARTIST (<c>Shell.FrameRules.PageKeyOf</c>), not the facet: a facet click re-binds the
    /// mounted page, so the views pill slides and only the grid below re-skeletons in place. <c>DiscoPageProps.Key</c> is the
    /// whole route key, so each facet still restores its own scroll offset. The facet SWAP is therefore not a page transition
    /// (<c>Shell.PageTransition</c>); the route stays the truth.</para></summary>
    public static Element DiscographyPage(in Shell.Route route)
    {
        string key = Shell.NameOf(route);
        return Embed.Comp(new DiscoPageProps(route, key), static () => new DiscoPageHost())
               with { Key = "disco-page:" + Shell.FrameRules.PageKeyOf(route) };
    }

    sealed record DiscoPageProps(Shell.Route Route, string Key);

    static readonly string[] s_facetLabelKeys = [Strings.Artist.Albums, Strings.Artist.SinglesEps, Strings.Artist.Compilations];

    sealed class DiscoPageHost : Component
    {
        readonly Signal<float> _scroll = new(0f);
        /// <summary>The page viewport's scroll handle; <see cref="_scroll"/> is its offset on the 24-DIP write floor.</summary>
        readonly ScrollHandle _handle = new();
        readonly Action _watchScroll;
        int _scrollStep = int.MinValue;
        readonly Action _demand;
        Artist _artist;
        DiscoFacet _facet;
        Scope? _scope;
        EntityUri _subject;
        /// <summary>The views bar's selection: ONE signal per page instance (the bar never re-mounts, the pill slides), synced
        /// from the route's facet in a layout effect.</summary>
        readonly Signal<int> _facetIndex = new(0);
        readonly Action<int> _onView;
        readonly Action _syncFacet;

        public DiscoPageHost()
        {
            _demand = Demand;
            _onView = OnView;
            _syncFacet = SyncFacet;
            _watchScroll = () =>
            {
                float y = (float)_handle.Offset.Value;
                int step = (int)(y / 24f);
                if (step == _scrollStep) return;
                _scrollStep = step;
                _scroll.Value = y;
            };
        }

        /// <summary>A word chosen in the views bar: the ROUTE, not the control, is the truth. A change navigates and the layout
        /// effect re-seeds <see cref="_facetIndex"/> from the new route (0.2.9 DiscographyPage.cs:120-125).</summary>
        void OnView(int index)
        {
            var a = _artist;
            if ((DiscoFacet)index != _facet && a.IsValid) Shell.GoTo(DiscoRoute.For(a, (DiscoFacet)index));
        }

        void SyncFacet() => _facetIndex.SetIfChanged((int)_facet);

        void Demand()
        {
            _ = Entities.ScopeEpoch.Value;
            var a = _artist;
            if (!a.IsValid) return;
            _ = FacetEdge(_facet).Changed.Value;
            if (!a.Knows(ArtistFields.Identity)) Entities.Ensure(a, ArtistFields.Identity);
            DemandFacet(a, _facet);
        }

        public override Element Render()
        {
            var p = UseProps<DiscoPageProps>();
            string scrollScope = UseContext(Shell.PageScrollScope);
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Artists.Changed.Value;
            bool parsed = DiscoRoute.TryParse(p.Route, out var facet, out var artistUri);
            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(artistUri))
            {
                _scope = scope;
                _subject = artistUri;
                // The factory allocates an empty row the demand fills.
                _artist = parsed && artistUri.IsValid ? Entities.Artist(artistUri) : default;
            }
            _facet = facet;
            UseEffect(_demand, DepKey.From(_artist.Slot, (int)facet));
            UseLayoutEffect(_syncFacet, DepKey.From((int)facet));
            UseSignalEffect(_watchScroll);
            if (!parsed) return Controls.Vacancy(Controls.VacancyVoice.Error);

            var a = _artist;
            string name = a.IsValid && a.Knows(ArtistFields.Name) ? a.Name : Loc.Get(Strings.Artist.FallbackName);
            string title = FacetTitle(facet);
            var labels = new string[s_facetLabelKeys.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(s_facetLabelKeys[i]);
            var goArtist = a.Uri;

            float g = Shell.Ui.PageGutter.Value;
            // CrumbTitleViews (200) in every data state: a late artist name changes only the crumb text inside the reserved
            // AboveLine box. The head carries its own gutter; the grid below carries the page gutter and the bottom reserve.
            Element head = PageHead.Create(new PageHeadSpec(title)
            {
                Above = BreadcrumbBar.Create([name, title], i => { if (i == 0) Shell.GoTo(Shell.For(goArtist, name)); }),
                Views = labels, ViewsSelected = _facetIndex, OnView = _onView,
                Gutter = g, Key = "disco:head",
            });

            var content = new BoxEl
            {
                Direction = 1, Gap = 0f,
                Children =
                [
                    head,
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Padding = new Edges4(g, 0f, g, PageGeometry.BottomReserve),
                        // System accent (no palette) and a 28 inset for the drawer's reveal (expandedTopInset, not a margin): there
                        // is no sticky facet band on this page. This facet IS the whole page, so its continuation pages stay
                        // Visible priority.
                        Children = [a.IsValid ? Grid(a, facet, s_themeAccent, null, 28f, FetchPriority.Visible) : new BoxEl()],
                    },
                ],
            };
            var scroll = ScrollView(content) with
            {
                Grow = 1f, ScrollKey = scrollScope + p.Key, Handle = _handle,
            };
            return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)_scroll, scroll);
        }
    }
}
