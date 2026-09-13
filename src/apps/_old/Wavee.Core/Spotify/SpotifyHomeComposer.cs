using System.Linq;
using System.Text.Json;

namespace Wavee.Core;

/// <summary>App-authored labels for modules which combine or supplement source sections. Server section titles remain
/// verbatim on their single owning <see cref="HomeGroup"/>; these labels are fallbacks for synthetic/personal modules.</summary>
public sealed record HomeModuleTitles(
    string JumpBackIn = "Jump back in",
    string Recents = "Recents",
    string MadeForYou = "Made for you",
    string TopMixes = "Your top mixes",
    string Radio = "Radio",
    string UpNext = "Up next",
    string Audiobooks = "Audiobooks for you",
    string EditorsPicks = "Editors' picks",
    string BecauseYouListened = "Because you listened",
    string Podcasts = "Podcasts")
{
    public static readonly HomeModuleTitles Default = new();
}

/// <summary>Projects Spotify Home into authored module previews and a lossless source-section ledger. Classification is
/// per card, but grouping and deduplication are per section: the same URI in two source sections survives in both. One
/// group owns each non-empty server title, giving every titled section exactly one drill affordance.</summary>
public static class SpotifyHomeComposer
{
    const int QuickPicks = 9;

    public static HomeContribution Compose(JsonElement homeRoot, IReadOnlyList<PlaylistSummary> library,
        HomeModuleTitles? titles = null)
    {
        var t = titles ?? HomeModuleTitles.Default;
        var groups = new List<HomeGroup>();
        var sourceSections = new List<HomeSection>();
        HomeGroup? spotlight = null;

        // Export/fake callers may supply library quick picks directly. This is a synthetic module, not a server section,
        // so it deliberately does not enter the source-section accounting ledger.
        if (library.Count > 0)
        {
            var quick = new List<HomeCard>(Math.Min(library.Count, QuickPicks));
            foreach (var p in library.Take(QuickPicks))
                quick.Add(new HomeCard(p.Uri, p.Name, p.OwnerName, p.Cover, HomeCardKind.Playlist, p.MosaicTiles));
            groups.Add(new HomeGroup(HomeGroupKind.QuickGrid, t.JumpBackIn, quick));
        }

        var sections = SpotifyExportMapper.Dig(homeRoot, "sectionContainer", "sections", "items");
        if (sections.ValueKind == JsonValueKind.Array)
            foreach (var rawSection in sections.EnumerateArray())
            {
                var data = SpotifyExportMapper.Dig(rawSection, "data");
                var type = Str(data, "__typename");
                var title = Str(data, "title", "transformedLabel") ?? Str(data, "title", "text");
                var subtitle = Str(data, "subtitle", "transformedLabel") ?? Str(data, "subtitle", "text");
                var uri = Str(rawSection, "uri") ?? Str(data, "uri");
                var items = SpotifyExportMapper.Dig(rawSection, "sectionItems", "items");
                int totalCount = IntAt(rawSection, "sectionItems", "totalCount");

                switch (type)
                {
                    case "HomeSpotlightSectionData":
                    {
                        var mapped = Cards(items);
                        var section = Section(uri, title, subtitle, totalCount, mapped);
                        sourceSections.Add(section);
                        if (mapped.Cards.Count == 0)
                        {
                            if (HasIdentity(section)) groups.Add(Group(HomeGroupKind.SectionEntry, section, mapped.Cards, true));
                            break;
                        }

                        var hero = Group(HomeGroupKind.Hero, section, mapped.Cards, true);
                        if (spotlight is null) spotlight = hero;
                        else groups.Add(hero);
                        break;
                    }

                    case "HomeFeedBaselineSectionData":
                    {
                        var mapped = Cards(items);
                        if (title is { Length: > 0 })
                            mapped = mapped with { Cards = mapped.Cards.Select(c => c with { Eyebrow = title }).ToList() };
                        var section = Section(uri, title, subtitle, totalCount, mapped);
                        sourceSections.Add(section);
                        groups.Add(Group(mapped.Cards.Count > 0 ? HomeGroupKind.DiscoverFeed : HomeGroupKind.SectionEntry,
                            section, mapped.Cards, true));
                        break;
                    }

                    case "HomeRecentlyPlayedSectionData":
                    {
                        var listData = FirstContentData(items);
                        var mapped = listData is { ValueKind: JsonValueKind.Object }
                            ? RecentCards(listData)
                            : new MappedCards([], RawCount(items), RawCount(items), 0);
                        // This one section's sectionItems array holds a SINGLE `List` wrapper, not the recents — so its
                        // sectionItems.totalCount is 1 (measured: items 1, totalCount 1) while the real count (20) sits
                        // on the wrapped list's own items page. Reading the wrapper's 1 made TotalCount == 1 for a
                        // 20-card shelf, which is exactly the comparison "Show all" arms on: it could never arm here.
                        var section = Section(uri, title ?? t.Recents, subtitle,
                            RecentsTotal(listData, totalCount), mapped);
                        sourceSections.Add(section);
                        groups.Add(Group(mapped.Cards.Count > 0 ? HomeGroupKind.Recents : HomeGroupKind.SectionEntry,
                            section, mapped.Cards, true));
                        break;
                    }

                    // Unknown future section types still enter the ledger and degrade to the card-driven classifier.
                    // This preserves their title/URI instead of silently deleting the whole section.
                    default:
                    {
                        var mapped = Cards(items);
                        var section = Section(uri, title, subtitle, totalCount, mapped);
                        sourceSections.Add(section);
                        EmitSectionGroups(section, groups);
                        break;
                    }
                }
            }

        // Spotlight is the preferred Hero preview regardless of response position. Other Hero sections remain present;
        // the view moves them into its drill-in deck after assigning the first Hero its one cinematic slot.
        if (spotlight is not null) groups.Insert(library.Count > 0 ? 1 : 0, spotlight);

        return new HomeContribution(groups, Priority: 0, Chips: MapChips(homeRoot), Greeting: Greeting(homeRoot),
            Sections: sourceSections);
    }

    /// <summary>Map one <c>homeSection</c> response — the "Show all" paging axis for a single Home section — into the
    /// same lossless <see cref="HomeSection"/> ledger <see cref="Compose"/> builds, plus the server's own paging cursor.
    /// Returns null when the document carries no section at all (the caller treats that as a failed read, not as an
    /// empty section).
    /// <para>Shape: <c>data.homeSections.sections[0].sectionItems { items[], pagingInfo { nextOffset }, totalCount }</c>.
    /// Each item is the SAME <c>{ content: { data } }</c> wrapper the inline Home sections use, which is why this reuses
    /// <see cref="Cards"/> (and therefore <c>SpotifyExportMapper.CardFromEntity</c>) verbatim rather than owning a
    /// second card mapper.</para>
    /// <para><c>nextOffset</c> is genuinely nullable on the wire and is the ONLY terminator — see
    /// <see cref="HomeSectionPageResult"/>.</para></summary>
    public static HomeSectionPageResult? SectionPage(JsonElement root, string? fallbackTitle = null)
    {
        var rawSection = FirstSection(SpotifyExportMapper.Dig(root, "data", "homeSections", "sections"));
        if (rawSection.ValueKind != JsonValueKind.Object) return null;

        var data = SpotifyExportMapper.Dig(rawSection, "data");
        var title = Str(data, "title", "transformedLabel") ?? Str(data, "title", "text") ?? fallbackTitle;
        var subtitle = Str(data, "subtitle", "transformedLabel") ?? Str(data, "subtitle", "text");
        var uri = Str(rawSection, "uri") ?? Str(data, "uri");
        var mapped = Cards(SpotifyExportMapper.Dig(rawSection, "sectionItems", "items"));
        int totalCount = IntAt(rawSection, "sectionItems", "totalCount");
        return new HomeSectionPageResult(Section(uri, title, subtitle, totalCount, mapped),
            NullableIntAt(rawSection, "sectionItems", "pagingInfo", "nextOffset"));
    }

    /// <summary>The single section a <c>homeSection</c> answer carries. The capture answers a bare ARRAY at
    /// <c>homeSections.sections</c>; <c>home</c> wraps the equivalent list in an <c>items</c> object
    /// (<c>sectionContainer.sections.items</c>), so both are accepted rather than silently mapping nothing.</summary>
    static JsonElement FirstSection(JsonElement sections)
    {
        if (sections.ValueKind == JsonValueKind.Object) sections = SpotifyExportMapper.Dig(sections, "items");
        return sections.ValueKind == JsonValueKind.Array && sections.GetArrayLength() > 0 ? sections[0] : default;
    }

    static void EmitSectionGroups(HomeSection section, List<HomeGroup> groups)
    {
        var cards = section.Cards;
        if (cards.Count == 0)
        {
            if (HasIdentity(section)) groups.Add(Group(HomeGroupKind.SectionEntry, section, cards, true));
            return;
        }

        int editorial = 0;
        foreach (var card in cards) if (IsEditorialFormat(card.Meta?.Format)) editorial++;
        if (editorial * 2 > cards.Count)
        {
            groups.Add(Group(HomeGroupKind.Topic, section, cards, true));
            return;
        }

        var byKind = new Dictionary<HomeGroupKind, List<HomeCard>>();
        foreach (var card in cards)
        {
            var kind = ModuleFor(card);
            if (!byKind.TryGetValue(kind, out var list)) byKind.Add(kind, list = []);
            list.Add(card);
        }

        HomeGroupKind? dominant = null;
        foreach (var pair in byKind)
            if (pair.Value.Count * 2 > cards.Count) { dominant = pair.Key; break; }

        bool moduleOwnsTitle = dominant is not null && section.Title is { Length: > 0 };
        if (dominant is null || !moduleOwnsTitle)
            groups.Add(Group(HomeGroupKind.SectionEntry, section, cards, true));

        foreach (var pair in byKind)
            groups.Add(Group(pair.Key, section, pair.Value, moduleOwnsTitle && dominant == pair.Key));
    }

    static bool IsEditorialFormat(string? format) =>
        format is "editorial" or "format-shows-shuffle" or "artistsets" or "descripto";

    static bool HasIdentity(HomeSection section) =>
        section.Uri is { Length: > 0 } || section.Title is { Length: > 0 };

    static HomeGroup Group(HomeGroupKind kind, HomeSection section, IReadOnlyList<HomeCard> cards, bool ownsTitle) =>
        new(kind, ownsTitle ? section.Title : null, cards,
            ownsTitle ? section.Subtitle : null, section.Uri, section.TotalCount);

    static HomeSection Section(string? uri, string? title, string? subtitle, int totalCount, MappedCards mapped) =>
        new(uri, title, subtitle, mapped.Cards, totalCount > 0 ? totalCount : mapped.Cards.Count,
            mapped.Raw, mapped.Unsupported, mapped.Duplicates);

    /// <summary>The server greeting is already localized for the account. Empty means the source has no greeting.</summary>
    static string Greeting(JsonElement homeRoot) =>
        Str(homeRoot, "greeting", "transformedLabel")
        ?? Str(homeRoot, "greeting", "translatedBaseText")
        ?? "";

    static HomeGroupKind ModuleFor(HomeCard card) => card.Kind switch
    {
        HomeCardKind.Episode => HomeGroupKind.QueueList,
        HomeCardKind.Audiobook => HomeGroupKind.RatedShelf,
        HomeCardKind.Podcast => HomeGroupKind.PodcastShelf,
        HomeCardKind.Playlist => ModuleForFormat(card.Meta?.Format),
        _ => HomeGroupKind.QuickGrid,
    };

    static HomeGroupKind ModuleForFormat(string? format) => format switch
    {
        "daylist" => HomeGroupKind.Hero,
        "daily-mix" => HomeGroupKind.MixBand,
        "discover-weekly" or "release-radar" => HomeGroupKind.WeeklyPair,
        "topic-mix" or "artist-mix-reader" => HomeGroupKind.ChipCards,
        "inspiredby-mix" => HomeGroupKind.RadioDial,
        "editorial" or "format-shows-shuffle" or "artistsets" or "descripto" => HomeGroupKind.Featured,
        _ => HomeGroupKind.QuickGrid,
    };

    static IReadOnlyList<HomeChip>? MapChips(JsonElement homeRoot)
    {
        var items = SpotifyExportMapper.Dig(homeRoot, "homeChips");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;

        var chips = new List<HomeChip>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
            if (MapChip(item) is { } chip) chips.Add(chip);
        return chips.Count > 0 ? chips : null;
    }

    static HomeChip? MapChip(JsonElement item)
    {
        var id = SpotifyExportMapper.Str(item, "id");
        var label = SpotifyExportMapper.Str(item, "label", "transformedLabel")
                    ?? SpotifyExportMapper.Str(item, "label", "translatedBaseText");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(label)) return null;

        var rawChildren = SpotifyExportMapper.Dig(item, "subChips");
        List<HomeChip>? children = null;
        if (rawChildren.ValueKind == JsonValueKind.Array)
            foreach (var rawChild in rawChildren.EnumerateArray())
                if (MapChip(rawChild) is { } child) (children ??= new List<HomeChip>(2)).Add(child);

        return new HomeChip(id, label, (IReadOnlyList<HomeChip>?)children ?? Array.Empty<HomeChip>());
    }

    readonly record struct MappedCards(List<HomeCard> Cards, int Raw, int Unsupported, int Duplicates);

    static MappedCards Cards(JsonElement items)
    {
        var cards = new List<HomeCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int raw = 0, unsupported = 0, duplicates = 0;
        if (items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                raw++;
                if (SpotifyExportMapper.CardFromEntity(SpotifyExportMapper.Dig(item, "content", "data")) is not { } card)
                {
                    unsupported++;
                    continue;
                }
                if (!seen.Add(card.Uri)) { duplicates++; continue; }
                cards.Add(card);
            }
        return new MappedCards(cards, raw, unsupported, duplicates);
    }

    static MappedCards RecentCards(JsonElement listData)
    {
        var rawItems = SpotifyExportMapper.Dig(listData, "items", "items");
        int raw = RawCount(rawItems);
        // Map without deduplication so this ledger, not the mapper, can distinguish unsupported items from duplicates.
        var mapped = SpotifyExportMapper.RecentCardsFromListData(listData, raw, deduplicate: false);
        var cards = new List<HomeCard>(mapped.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0;
        foreach (var card in mapped)
        {
            if (!seen.Add(card.Uri)) { duplicates++; continue; }
            cards.Add(card);
        }
        return new MappedCards(cards, raw, Math.Max(0, raw - mapped.Count), duplicates);
    }

    /// <summary>The recents shelf's REAL server total, which lives on the WRAPPED list, not on the section wrapper.
    /// <para>Once there is a list to read, the wrapper's own count is discarded rather than used as a fallback: it counts
    /// the one <c>List</c> child (measured: items 1, totalCount 1), so keeping it would pin a twenty-card shelf at "1".
    /// Zero lets <see cref="Section"/> fall through to the mapped card count, which is at least true.
    /// <paramref name="wrapperTotal"/> survives only for a response that carried no list at all.</para></summary>
    static int RecentsTotal(JsonElement listData, int wrapperTotal) =>
        listData.ValueKind == JsonValueKind.Object ? IntAt(listData, "items", "totalCount") : wrapperTotal;

    static int RawCount(JsonElement items) => items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;

    static JsonElement FirstContentData(JsonElement items) =>
        items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
            ? SpotifyExportMapper.Dig(items[0], "content", "data")
            : default;

    /// <summary>A read where ABSENT and <c>null</c> mean something the caller must act on (<c>pagingInfo.nextOffset</c>:
    /// "there is no next page"), so they cannot be flattened into 0 the way <see cref="IntAt"/> does — 0 is a legal
    /// cursor value the server sends on a COMPLETE section.</summary>
    static int? NullableIntAt(JsonElement element, params string[] path)
    {
        var value = SpotifyExportMapper.Dig(element, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
    }

    static int IntAt(JsonElement element, params string[] path)
    {
        var value = SpotifyExportMapper.Dig(element, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : 0;
    }

    static string? Str(JsonElement element, params string[] path)
    {
        var value = SpotifyExportMapper.Dig(element, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
