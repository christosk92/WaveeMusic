// ── Entities/Home.Rules.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the Home/Browse rule sets that OUTLIVE the Wave-5 Home UI: the timeline merge, play routing, card text + identity
// colour, the section chart filter / routes, the Charts deck, and card navigation (CORE half — the UI half moved to
// Entities/Browse.Cards.cs). The page-side section pager (cursor / walk / near tail) is gone: the query layer walks a
// section whole (Spotify/Spotify.Api.Browse.cs). The landing projections, the facet strip, the hero / module /
// artist-row geometry, the wash source and the layout document + reducer + wire were the presentation half of the
// superseded Home page and were deleted with it (docs/plans/wavee/home-rebuild-implementation.md §7); the new Home
// page lives under src/apps/Wavee/Home/.
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: the pre-declared overflow partial of `Home.cs` (WP-5.P contract §1: used because Home.cs passed 2,340 lines)
// Spec: ch 10 §8, ch 11 §8, ch 12 §8; plan §6.1 rows 10-12; 0.2.9 `Features/Home/{HomeTimelineMerge, HomeCardPlayRouting,
//   HomeCards (:35-88, :248-264, :478-484, :1081-1088), HomeSectionRoutes, HomeSectionNavigation, HomeBrowseCards}.cs`
//
// VERBATIM, with ONE type map (contract §2): `HomeFeed` → `HomeFeedView`, `HomeSection` → `HomeSectionView`,
// `HomeCard`+`HomeCardMeta` → the `HomeCard` handle, a card's `Meta.X` → `card.X`, a card's uri comparison → its
// `DedupeKey` (one entity, one slot — and no allocation), `int?` cursors → the `SectionPaging` sentinels, and a
// `WaveeNotification` subclass → the flat `Notification`. Member names, constants and algorithms are 0.2.9's; where a
// rule had to change, the member's own comment says so. Every rule here is pure over its arguments or over the
// tables — no hooks, no elements.

using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// ══ 5. THE WASH SOURCE (0.2.9 HomeWashSource.cs) — the card-colour half only ═════════════════════════════════════════
//
// The Home-page-specific selection (which THREE cards feed the wash, off a `HomeFeedView`) went with the rest of the
// Wave-5 Home UI (docs/plans/wavee/home-rebuild-implementation.md §7; the new Home page picks its own wash through
// `Wavee.HomeUi.WashPick`, `Home/Reveal.cs`). The ONE-card resolution below is a general card-colour rule with its own
// live caller — Recents.Page.cs's own wash — so it stays.

/// <summary>One resolved leg: the FULL-ALPHA colour plus the artwork identity it was resolved from (the layer's key).</summary>
public readonly record struct HomeWashPick(ColorF Color, string Key);

/// <summary>The PURE selector behind a card's wash colour: payload accent first, the graded cover second, NOTHING third.</summary>
public static class HomeWashSource
{
    /// <summary>One card → its leg, or null. Tier 1 the payload accent, LIFTED; tier 2 the graded cover through the
    /// chrome derivation (a card with no artwork is never asked); there is no tier 3.</summary>
    public static HomeWashPick? Pick(HomeCard? card, Func<string?, Scheme?> schemeFor)
    {
        if (card is not { } c) return null;
        if (c.Accent != 0u)
            return new HomeWashPick(Design.Palette.Lift(Design.Palette.ToColor(c.Accent)) with { A = 1f }, KeyOf(c));
        if (c.ImageUrl is { Length: > 0 } url && schemeFor(url) is { } scheme)
            return new HomeWashPick(Design.Palette.ChromeAccent(scheme) with { A = 1f }, KeyOf(c));
        return null;
    }

    /// <summary>The artwork this card's colour is still WAITING on, or null (already resolved, or never gradeable).</summary>
    public static string? PlaneUrl(HomeCard? card)
        => card is { } c && c.Accent == 0u && c.ImageUrl is { Length: > 0 } url ? url : null;

    /// <summary>The leg's identity: the size-independent artwork key, else the card's uri.</summary>
    public static string KeyOf(HomeCard card)
    {
        if (card.ImageUrl is { Length: > 0 } url)
        {
            var key = Palette.KeyOf(url);
            if (key.Length > 0) return key.ToString();
        }
        return card.Uri;
    }
}

// ══ 9. PLAY ROUTING, CARD TEXT, CARD COLOUR (0.2.9 HomeCardPlayRouting.cs, HomeCards.cs) ════════════════════════════

/// <summary>How a card's ▶ starts playback.</summary>
public static class HomeCardPlayRouting
{
    /// <summary>True for the kinds that are ONE playable item (a track, an episode); every other kind is a context.</summary>
    public static bool PlaysAsItem(HomeCardKind kind) => kind is HomeCardKind.Track or HomeCardKind.Episode;
}

/// <summary>The card vocabulary's number / duration / blurb formats (0.2.9 <c>HomeCards</c> helpers, culture-aware).</summary>
public static class HomeCardText
{
    /// <summary>"1 hr 5 min" / "45 min" (minutes rounded, at least 1); "" for no duration.</summary>
    public static string Duration(long ms)
    {
        if (ms <= 0) return "";
        int totalMin = (int)Math.Round(ms / 60000d);
        int h = totalMin / 60, m = totalMin % 60;
        return h > 0 ? Strings.Detail.DurationHrMin(h, m) : Strings.Detail.DurationMin(Math.Max(1, m));
    }

    /// <summary>The prototype's <c>hrs()</c>: "1.3 h" past the hour, "45 m" under it (audiobook lengths).</summary>
    public static string Hours(long ms)
    {
        if (ms <= 0) return "";
        var c = CultureInfo.CurrentCulture;
        return ms >= 3600000 ? (ms / 3600000d).ToString("0.0", c) + " h" : Math.Round(ms / 60000d).ToString("0", c) + " m";
    }

    /// <summary>Play / listener counts: "1.2B", "3.4M", "5.6K", else the grouped number — culture-aware on the value.</summary>
    public static string CompactNumber(long n)
    {
        var c = CultureInfo.CurrentCulture;
        return n >= 1_000_000_000 ? (n / 1_000_000_000d).ToString("0.#", c) + "B"
             : n >= 1_000_000 ? (n / 1_000_000d).ToString("0.#", c) + "M"
             : n >= 1_000 ? (n / 1_000d).ToString("0.#", c) + "K"
             : n.ToString("N0", c);
    }

    /// <summary>The first sentence of a blurb: STRIP FIRST (a raw fragment's first '.' is inside
    /// <c>spotify:playlist:…</c>), then cut at the first <c>". "</c> past index 20.</summary>
    public static string FirstSentence(string? html)
    {
        var plain = PlainText(html);
        if (string.IsNullOrWhiteSpace(plain)) return "";
        int end = plain.IndexOf(". ", StringComparison.Ordinal);
        return end > 20 ? plain[..(end + 1)] : plain;
    }

    /// <summary>0.2.9 <c>SpotifyExportMapper.ToPlainText</c>: drop real tags (a letter, '/' or '!' after '&lt;'), keep the
    /// text around them, collapse whitespace. The markup-free common case returns the input.</summary>
    public static string? PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf('<') < 0) return html;
        var sb = new System.Text.StringBuilder(html.Length);
        bool lastSpace = false;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<' && i + 1 < html.Length && IsTagNameStart(html[i + 1]))
            {
                int close = html.IndexOf('>', i + 1);
                if (close >= 0) { i = close; continue; }
            }
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
                continue;
            }
            sb.Append(c);
            lastSpace = false;
        }
        return sb.ToString().TrimEnd();

        static bool IsTagNameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '/' or '!';
    }
}

/// <summary>A card's identity colour — the ONE derivation (0.2.9 <c>HomeCards.RawAccent/Accent/SpineFallback/
/// SpineAccent/AccentOrChrome</c>): the payload accent, else the graded cover's tinted base, else NOTHING. The hash
/// palette only RE-HUES a real near-neutral seed; it never invents a colour for a card that has none.</summary>
public static class HomeCardAccent
{
    /// <summary>The payload accent alone, or null.</summary>
    public static ColorF? Raw(in HomeCard c) => c.Accent != 0u ? Design.Palette.ToColor(c.Accent) : null;

    /// <summary>The identity seed: payload accent → the graded <c>BackgroundTintedBase</c> (else <c>BackgroundBase</c>) of
    /// the card's cover → null. A card with no artwork is never asked.</summary>
    public static ColorF? Seed(in HomeCard c, Func<string?, Scheme?> schemeFor)
    {
        if (Raw(in c) is { } payload) return payload;
        if (c.ImageUrl is not { Length: > 0 } url || schemeFor(url) is not { } g) return null;
        uint tone = g.BackgroundTintedBase != 0u ? g.BackgroundTintedBase : g.BackgroundBase;
        return tone != 0u ? Design.Palette.ToColor(tone) : null;
    }

    /// <summary>The lifted identity colour, or null ("not graded yet" — paint nothing).</summary>
    public static ColorF? Accent(in HomeCard c, Func<string?, Scheme?> schemeFor)
        => Seed(in c, schemeFor) is { } seed ? Design.Palette.Lift(seed) : null;

    /// <summary>The 2-DIP spine: null without a seed; a near-neutral seed is re-hued from the card's stable hash; then the
    /// contrast-solved hairline (3.25:1, 3.55:1 hovered).</summary>
    public static ColorF? SpineAccent(in HomeCard c, bool hovered, Func<string?, Scheme?> schemeFor)
    {
        if (Seed(in c, schemeFor) is not { } seed) return null;
        var (_, saturation, _) = seed.ToHsv();
        if (saturation <= Design.Palette.NeutralS) seed = SpineFallback(in c);
        return hovered ? Design.Palette.HairlineHover(seed) : Design.Palette.Hairline(seed);
    }

    /// <summary>For chrome that must paint regardless (the hero's wash and Play): the identity colour, else
    /// <paramref name="chrome"/> (the app accent the caller passes).</summary>
    public static ColorF AccentOrChrome(in HomeCard c, ColorF chrome, Func<string?, Scheme?> schemeFor)
        => Accent(in c, schemeFor) ?? chrome;

    /// <summary>The theme-aware semantic role a near-neutral cover re-hues to, by the card identity's stable hash.</summary>
    public static ColorF SpineFallback(in HomeCard c) => (StableHash(in c) & 3u) switch
    {
        0u => Tok.AccentDefault,
        1u => Tok.SystemFillSuccess,
        2u => Tok.SystemFillCaution,
        _ => Tok.SystemFillCritical,
    };

    /// <summary>FNV-1a over the identity: the gid bytes, or the text form's characters — deterministic across runs.</summary>
    public static uint StableHash(in HomeCard c)
    {
        uint h = 2166136261;
        if (c.IsBlank) return h;
        var id = c.Id;
        if (id.Form == EntityForm.Gid)
        {
            Span<byte> gid = stackalloc byte[16];
            id.WriteGid(gid);
            for (int i = 0; i < gid.Length; i++) h = (h ^ gid[i]) * 16777619;
        }
        else
        {
            string text = Entities.Strings.Resolve(id.TextId);
            for (int i = 0; i < text.Length; i++) h = (h ^ text[i]) * 16777619;
        }
        return h;
    }
}

// ══ 10. SECTIONS: THE CHART FILTER AND THE ROUTES (ch 12 §8) ════════════════════════════════════════════════════════

// The section's cursor arithmetic (`SectionPaging`, Home.cs) is the QUERY layer's now: a drill page demands its section
// WHOLE and the provider walks it (Spotify/Spotify.Api.Browse.cs, `BrowseWalk`). The page-side ports that paged it —
// 0.2.9's `HomeSectionPaging`, the Charts `BrowseSectionWalk` and the infinite-scroll `HomeNearTail` — are gone.

/// <summary>The Charts grid's title filter: ordinal-ignore-case, first occurrence; the span is what the pill paints.</summary>
public static class ChartTitleMatch
{
    public static bool TryFind(string? title, string? query, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (title is not { Length: > 0 } || string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        int i = title.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        start = i;
        length = q.Length;
        return true;
    }

    public static IReadOnlyList<HomeCard> Filter(IReadOnlyList<HomeCard> cards, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return cards;
        var hits = new List<HomeCard>(cards.Count);
        for (int i = 0; i < cards.Count; i++)
            if (TryFind(cards[i].Title, query, out _, out _)) hits.Add(cards[i]);
        return hits;
    }
}

/// <summary>The <c>home-section:</c> drill route. The ROUTE PREFIX, never the uri, selects the endpoint.</summary>
public static class HomeSectionRoutes
{
    public const string Prefix = "home-section:";
    /// <summary>A client-minted section identity: addresses nothing on any server, never a paging argument.</summary>
    public const string LocalPrefix = "wavee:local:";

    public static string Page(string sectionUri) => Prefix + sectionUri;
    public static bool Is(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);
    public static string UriOf(string route) => Is(route) ? route[Prefix.Length..] : "";
    public static bool IsLocal(string? uri) => uri is not null && uri.StartsWith(LocalPrefix, StringComparison.Ordinal);
}

/// <summary>The <c>browse-section:</c> drill route (answered through <c>browseSection</c>, whatever its uri looks like).</summary>
public static class BrowseSectionRoutes
{
    public const string Prefix = "browse-section:";
    public static string Page(string sectionUri) => Prefix + sectionUri;
    public static bool Is(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);
    public static string UriOf(string route) => Is(route) ? route[Prefix.Length..] : "";
}

// ══ 11. THE CHARTS DECK (0.2.9 HomeBrowseCards.cs — the browse → home boundary) ══════════════════════════════════════

/// <summary>The Charts Fold deck over the five chart section rows.</summary>
public static class HomeBrowseCards
{
    /// <summary>Three blank Fold tiles — the Charts row's shimmer seed (title a single space, never shown).</summary>
    public static readonly IReadOnlyList<HomeSectionView> ChartDeckSeed = [BlankFold(0), BlankFold(1), BlankFold(2)];

    static HomeSectionView BlankFold(int i) => new(Table.None, null, " ", null,
        [HomeCard.Blank(HomeCardKind.Playlist, 900 + i * 3), HomeCard.Blank(HomeCardKind.Playlist, 901 + i * 3),
         HomeCard.Blank(HomeCardKind.Playlist, 902 + i * 3)], 3, 3);

    /// <summary>Ask every chart section row (as BROWSE sections) — the deck's demand.</summary>
    public static void EnsureChartDeck()
    {
        var uris = ChartSections.All;
        for (int i = 0; i < uris.Count; i++) Home.EnsureSection(Entities.BrowseSection(uris[i]), browse: true);
    }

    static Scope? s_scope;
    static ulong s_key = ulong.MaxValue;
    static bool s_live;
    static HomeLoad s_state;
    static IReadOnlyList<HomeSectionView> s_deck = Array.Empty<HomeSectionView>();

    /// <summary>One section view per <c>ChartSections.All</c> uri that answered with cards, Featured first; later null or
    /// card-less sections are omitted. <paramref name="state"/>: <see cref="HomeLoad.Pending"/> while any section is
    /// unanswered (in flight or not yet asked) and Featured has not failed; <see cref="HomeLoad.Failed"/> when Featured's
    /// ask concluded without an answer and <paramref name="hasLiveCatalog"/> (0.2.9's fail-loud throw);
    /// <see cref="HomeLoad.Ready"/> otherwise — an EMPTY deck when there is no live catalog and Featured never answered.
    /// Memoized on every row's Version + CardVersion. UI thread only.</summary>
    public static IReadOnlyList<HomeSectionView> ChartDeck(bool hasLiveCatalog, out HomeLoad state)
    {
        var uris = ChartSections.All;
        var scope = Entities.Current;
        var t = scope.Sections;
        ulong key = 14695981039346656037UL;
        for (int i = 0; i < uris.Count; i++)
        {
            var s = Entities.BrowseSection(uris[i]);
            key = (key ^ s.Version) * 1099511628211UL;
            key = (key ^ s.CardVersion) * 1099511628211UL;
            key = (key ^ t.Inflight[s.Slot]) * 1099511628211UL;
            key = (key ^ t.Asked[s.Slot]) * 1099511628211UL;
        }
        if (ReferenceEquals(scope, s_scope) && key == s_key && hasLiveCatalog == s_live) { state = s_state; return s_deck; }

        var featured = Entities.BrowseSection(uris[0]);
        bool featuredKnown = featured.Knows(SectionFields.Identity);
        bool featuredConcluded = !featuredKnown && t.Inflight[featured.Slot] == 0
                                 && (t.Asked[featured.Slot] & (uint)SectionFields.Identity) != 0;
        IReadOnlyList<HomeSectionView> deck = Array.Empty<HomeSectionView>();

        if (!featuredKnown)
        {
            state = featuredConcluded ? (hasLiveCatalog ? HomeLoad.Failed : HomeLoad.Ready)
                  : hasLiveCatalog ? HomeLoad.Pending : HomeLoad.Ready;
        }
        else
        {
            bool pending = false;
            var list = new List<HomeSectionView>(uris.Count);
            for (int i = 0; i < uris.Count; i++)
            {
                var s = Entities.BrowseSection(uris[i]);
                if (!s.Knows(SectionFields.Identity))
                {
                    if (t.Inflight[s.Slot] != 0 || (t.Asked[s.Slot] & (uint)SectionFields.Identity) == 0) pending = true;
                    continue;
                }
                var view = HomeSectionView.Of(s);
                if (view.Cards.Count > 0) list.Add(view);
            }
            state = pending && hasLiveCatalog ? HomeLoad.Pending : HomeLoad.Ready;
            deck = list;
        }

        s_scope = scope;
        s_key = key;
        s_live = hasLiveCatalog;
        s_state = state;
        s_deck = deck;
        return deck;
    }

    /// <summary>A browse card's kind from its uri's kind; anything the parser cannot name reads as a playlist (deliberate).</summary>
    public static HomeCardKind KindOf(EntityKind kind) => kind switch
    {
        EntityKind.Artist => HomeCardKind.Artist,
        EntityKind.Album => HomeCardKind.Album,
        EntityKind.Show => HomeCardKind.Podcast,
        EntityKind.Episode => HomeCardKind.Episode,
        EntityKind.Track => HomeCardKind.Track,
        _ => HomeCardKind.Playlist,
    };
}

// ══ 12. CARD NAVIGATION — the CORE half (0.2.9 HomeSectionNavigation.cs; the UI half is Entities/Browse.Cards.cs) ═════

public static partial class HomeCardNav
{
    /// <summary>Ch 12 §0.19: a BROWSE section of exactly one card opens that card, never a one-tile section page. A Home
    /// section has no such rule.</summary>
    public static bool OneCardOpensCard(int cardCount, bool browse) => browse && cardCount == 1;

    /// <summary>Where a card goes: Liked → liked; artist / album / show (podcast and audiobook) / playlist → its page with
    /// the title as the frame-one arg; a track or an episode PLAYS instead (<see cref="Shell.Route.None"/>). Composed by
    /// <see cref="Shell.For"/> — the one "go to this thing" composer — so a Home card and a sidebar row mint the same key,
    /// and the arg is interned by the shell rather than borrowed from a column that a later answer may release.</summary>
    public static Shell.Route RouteFor(in HomeCard card)
    {
        if (card.IsBlank || HomeCardPlayRouting.PlaysAsItem(card.Kind) || card.Id.IsEmpty) return Shell.Route.None;
        return Shell.For(new EntityUri(card.Id), card.Title);
    }

    /// <summary>A section's drill route — <c>browse-section:</c> or <c>home-section:</c> by the CALLER's family, with the
    /// section title as the arg so the masthead paints on frame one. <see cref="Shell.Route.None"/> for a view with no
    /// row (a seed tile); a section with no uri has no route in 0.3 (0.2.9 minted a <c>wavee:local:</c> preview id, which
    /// the 0.3 uri parser reads as a LOCAL-provider entity).</summary>
    public static Shell.Route SectionRoute(HomeSectionView s, bool browse)
    {
        if (s.Slot <= Table.None || s.Slot >= Entities.Current.Sections.Count) return Shell.Route.None;
        var section = new Section(s.Slot);
        if (section.Id.IsEmpty) return Shell.Route.None;
        var title = s.Title.AsSpan().Trim();
        return new Shell.Route(browse ? Shell.RouteKind.BrowseSection : Shell.RouteKind.HomeSection,
            new EntityUri(section.Id), title.IsEmpty ? StringId.Empty : Entities.Strings.Intern(title));
    }
}

