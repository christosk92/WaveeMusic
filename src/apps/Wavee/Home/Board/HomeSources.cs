// ── Home/Board/HomeSources.cs: the source registry (design 1.4, spec section 10) ────────────────────────────────────
//
// Every kind of thing Home can hold, with the sizes it declares, its canonical auto size, its refresh budget class, its
// options schema and its search words. A deferred source is simply not registered.

using System.Collections.Immutable;

namespace Wavee;

public enum HomeBudget : byte { Event, Hz1, Frame, Media, NetworkSlow, NetworkExternal }   // spec section 9
public enum OptionKind : byte { Choice, Toggle, Entity }
public sealed record OptionValue(string Value, string LabelKey);
public sealed record OptionDef(string Key, string LabelKey, OptionKind Kind, ImmutableArray<OptionValue> Values,
    string Default, string? EntityKind = null);

public sealed record HomeSourceDef
{
    public required string Id { get; init; }                    // "widget.nowPlaying", "pin", "spotify.section", "spotify.fill", ...
    public required string TitleKey { get; init; }
    public required string DescriptionKey { get; init; }
    public required string SearchTermsKey { get; init; }        // localized kind words: "queue up next"
    public required ImmutableArray<HomeSize> Sizes { get; init; }
    public required HomeSize CanonicalAuto { get; init; }       // a theme's rules record may override per source (spec 4.3)
    public required HomeBudget Budget { get; init; }
    public required string DefaultGroup { get; init; }          // "today" | "pinned" | "spotify"
    public ImmutableArray<OptionDef> Options { get; init; } = [];
    public bool Live { get; init; }                             // gets the "live" toggle
    public string Glyph { get; init; } = "";

    /// <summary>True when the source declares <paramref name="size"/> (Auto is always allowed).</summary>
    public bool Declares(HomeSize size)
    {
        if (size == HomeSize.Auto) return true;
        foreach (var s in Sizes) if (s == size) return true;
        return false;
    }

    /// <summary>The size a CycleSize step reaches from <paramref name="current"/> (Auto counts as the canonical size).</summary>
    public HomeSize Next(HomeSize current)
    {
        if (Sizes.IsDefaultOrEmpty) return current;
        var at = current == HomeSize.Auto ? CanonicalAuto : current;
        int i = Sizes.IndexOf(at);
        return Sizes[(i + 1) % Sizes.Length];
    }

    /// <summary>Has anything for the open tile to show: options, or the live toggle.</summary>
    public bool HasOptions => !Options.IsDefaultOrEmpty || Live;
}

public sealed class HomeSourceRegistry
{
    public const string NowPlaying = "widget.nowPlaying", Daylist = "widget.daylist", Recents = "widget.recents",
        UpNext = "widget.upNext", Releases = "widget.releases", Pin = "pin", Section = "spotify.section", Fill = "spotify.fill";

    public static HomeSourceRegistry Default { get; } = new(Build());

    readonly ImmutableArray<HomeSourceDef> _all;
    readonly Dictionary<string, HomeSourceDef> _byId;
    readonly HomeSourceDef[] _widgets;

    public HomeSourceRegistry(ImmutableArray<HomeSourceDef> all)
    {
        _all = all;
        _byId = new Dictionary<string, HomeSourceDef>(StringComparer.Ordinal);
        var widgets = new List<HomeSourceDef>();
        foreach (var d in all)
        {
            _byId[d.Id] = d;
            if (d.Id.StartsWith("widget.", StringComparison.Ordinal)) widgets.Add(d);
        }
        _widgets = widgets.ToArray();
    }

    public IReadOnlyList<HomeSourceDef> All => _all;

    /// <summary>The definition behind a document source, or null (an unknown source, an unregistered widget).</summary>
    public HomeSourceDef? Of(HomeSource source) => source switch
    {
        WidgetSource w => ById("widget." + w.Widget),
        PinSource => ById(Pin),
        SpotifySectionSource => ById(Section),
        SpotifyFeedSource => ById(Section),
        FillSource => ById(Fill),
        _ => null,
    };

    public HomeSourceDef? ById(string id) => _byId.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyList<HomeSourceDef> Widgets => _widgets;

    /// <summary>The widget name of a registry id ("widget.upNext" → "upNext"), or null.</summary>
    public static string? WidgetName(string id) => id.StartsWith("widget.", StringComparison.Ordinal) ? id["widget.".Length..] : null;

    static ImmutableArray<HomeSourceDef> Build()
    {
        var live = new OptionDef("live", "home.board.option.live", OptionKind.Toggle,
            [new OptionValue("1", "home.board.option.on"), new OptionValue("0", "home.board.option.off")], "1");
        return
        [
            new HomeSourceDef
            {
                Id = NowPlaying, TitleKey = "home.board.source.nowPlaying.title", DescriptionKey = "home.board.source.nowPlaying.description",
                SearchTermsKey = "home.board.source.nowPlaying.terms", Sizes = [HomeSize.S, HomeSize.M, HomeSize.L],
                CanonicalAuto = HomeSize.M, Budget = HomeBudget.Frame, DefaultGroup = HomeDocs.Today, Live = true, Glyph = "play",
                Options =
                [
                    new OptionDef("lyric", "home.board.option.lyric", OptionKind.Toggle,
                        [new OptionValue("1", "home.board.option.on"), new OptionValue("0", "home.board.option.off")], "0"),
                    live,
                ],
            },
            new HomeSourceDef
            {
                Id = Daylist, TitleKey = "home.board.source.daylist.title", DescriptionKey = "home.board.source.daylist.description",
                SearchTermsKey = "home.board.source.daylist.terms", Sizes = [HomeSize.M, HomeSize.L, HomeSize.Full],
                CanonicalAuto = HomeSize.Full, Budget = HomeBudget.Hz1, DefaultGroup = HomeDocs.Today, Live = true, Glyph = "clock",
                Options = [live],
            },
            new HomeSourceDef
            {
                Id = Recents, TitleKey = "home.board.source.recents.title", DescriptionKey = "home.board.source.recents.description",
                SearchTermsKey = "home.board.source.recents.terms", Sizes = [HomeSize.S, HomeSize.M, HomeSize.L, HomeSize.Full],
                CanonicalAuto = HomeSize.L, Budget = HomeBudget.Event, DefaultGroup = HomeDocs.Pinned, Glyph = "history",
            },
            new HomeSourceDef
            {
                Id = UpNext, TitleKey = "home.board.source.upNext.title", DescriptionKey = "home.board.source.upNext.description",
                SearchTermsKey = "home.board.source.upNext.terms", Sizes = [HomeSize.S, HomeSize.M, HomeSize.L],
                CanonicalAuto = HomeSize.M, Budget = HomeBudget.Event, DefaultGroup = HomeDocs.Today, Glyph = "queue",
            },
            new HomeSourceDef
            {
                Id = Releases, TitleKey = "home.board.source.releases.title", DescriptionKey = "home.board.source.releases.description",
                SearchTermsKey = "home.board.source.releases.terms", Sizes = [HomeSize.M, HomeSize.L, HomeSize.Full],
                CanonicalAuto = HomeSize.Full, Budget = HomeBudget.NetworkSlow, DefaultGroup = HomeDocs.Spotify, Live = true, Glyph = "spark",
                Options =
                [
                    new OptionDef("from", "home.board.option.from", OptionKind.Choice,
                        [new OptionValue("artists", "home.board.option.fromArtists")], "artists"),
                    live,
                ],
            },
            new HomeSourceDef
            {
                Id = Pin, TitleKey = "home.board.source.pin.title", DescriptionKey = "home.board.source.pin.description",
                SearchTermsKey = "home.board.source.pin.terms", Sizes = [HomeSize.S, HomeSize.M, HomeSize.L],
                CanonicalAuto = HomeSize.S, Budget = HomeBudget.Event, DefaultGroup = HomeDocs.Pinned, Glyph = "pin",
            },
            new HomeSourceDef
            {
                Id = Section, TitleKey = "home.board.source.section.title", DescriptionKey = "home.board.source.section.description",
                SearchTermsKey = "home.board.source.section.terms", Sizes = [HomeSize.M, HomeSize.L, HomeSize.Full],
                CanonicalAuto = HomeSize.Full, Budget = HomeBudget.NetworkSlow, DefaultGroup = HomeDocs.Spotify, Glyph = "shelf",
            },
            new HomeSourceDef
            {
                Id = Fill, TitleKey = "home.board.source.fill.title", DescriptionKey = "home.board.source.fill.description",
                SearchTermsKey = "home.board.source.fill.terms", Sizes = [HomeSize.S],
                CanonicalAuto = HomeSize.S, Budget = HomeBudget.NetworkSlow, DefaultGroup = HomeDocs.Pinned, Glyph = "pin",
            },
        ];
    }
}
