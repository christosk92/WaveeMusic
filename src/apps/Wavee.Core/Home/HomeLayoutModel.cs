namespace Wavee.Core.Home;

// The Home customizer PAYLOAD MODEL — the document the customizer edits, the reducer rewrites, HomeLandingProjection
// applies before row synthesis, and the versioned home-layout.json carries. Deliberately in Wavee.Core, not Wavee:
//   * framework-neutral (no FluentGpu type in any shape),
//   * Wavee.Tests exercises it without dragging the engine in,
//   * it sits beside Library/HomeFeed.cs (HomeGroupKind) the same way SidebarLayoutModel sits beside Library/Sidebar.cs.
//
// NO POLYMORPHIC JSON. A module is a closed record (kind + hidden). Commands (HomeLayoutCommands.cs) ARE a record
// hierarchy — they are in-memory only and never serialized. Unknown kinds/fields survive on the wire via the app-side
// carry (HomeLayoutWire), not here.

/// <summary>One authored Home module. <see cref="Kind"/> values are persisted as strings — append only, never
/// renumber the enum. An unknown (future) kind never enters this record: the wire carry holds the raw blob.</summary>
public sealed record HomeModuleSpec(HomeGroupKind Kind, bool Hidden = false);

/// <summary>The payload home-layout.json carries for v1: per-module visibility + order over the FIXED landing
/// modules <c>HomeLandingProjection.Project</c> already materializes, plus an ordered deck-id list reserved for
/// dynamic section-deck customization (unused by v1 UI; present so the schema is stable).</summary>
public sealed record HomeLayoutDoc(
    IReadOnlyList<HomeModuleSpec> Modules,
    IReadOnlyList<string>? DeckOrder = null)
{
    public static readonly HomeLayoutDoc Empty = new(Array.Empty<HomeModuleSpec>());

    /// <summary>Every fixed landing module visible, in the prototype's default order. First-run and Reset land here.</summary>
    public static HomeLayoutDoc Default { get; } = HomeLayoutModules.BuildDefault();

    public IReadOnlyList<string> DeckList => DeckOrder ?? Array.Empty<string>();

    public int ModuleCount => Modules.Count;

    /// <summary>True when this kind is authored hidden. A kind the document never mentioned is visible (new modules
    /// appear rather than vanishing — preserve-don't-destroy).</summary>
    public bool IsHidden(HomeGroupKind kind)
    {
        for (int i = 0; i < Modules.Count; i++)
            if (Modules[i].Kind == kind) return Modules[i].Hidden;
        return false;
    }

    public int IndexOf(HomeGroupKind kind)
    {
        for (int i = 0; i < Modules.Count; i++)
            if (Modules[i].Kind == kind) return i;
        return -1;
    }

    /// <summary>Fixed landing kinds the user left visible, in authored order. Hidden kinds are omitted so a hidden
    /// Hero does not leave a hole and a reorder is what the landing actually paints.</summary>
    public IReadOnlyList<HomeGroupKind> VisibleFixedModules()
    {
        var list = new List<HomeGroupKind>(Modules.Count);
        for (int i = 0; i < Modules.Count; i++)
        {
            var m = Modules[i];
            if (!m.Hidden && HomeLayoutModules.IsFixedLanding(m.Kind)) list.Add(m.Kind);
        }
        return list;
    }
}

/// <summary>Per-kind facts the reducer, the wire, the projection and the customizer all need in ONE place.</summary>
public static class HomeLayoutModules
{
    /// <summary>The FIXED landing modules <c>HomeLandingProjection.Project</c> materializes, in the prototype's
    /// designed rhythm. v1 customizer covers exactly this set. Shelf / Topic / SectionEntry stay off the list —
    /// they are source-section presentations, not authored landing modules. Dynamic deck ids live on
    /// <see cref="HomeLayoutDoc.DeckOrder"/>.</summary>
    public static readonly HomeGroupKind[] DefaultOrder =
    [
        HomeGroupKind.Hero,
        HomeGroupKind.WeeklyPair,
        HomeGroupKind.QuickGrid,
        HomeGroupKind.Recents,
        HomeGroupKind.MixBand,
        HomeGroupKind.ChipCards,
        HomeGroupKind.RadioDial,
        HomeGroupKind.QueueList,
        HomeGroupKind.RatedShelf,
        HomeGroupKind.PodcastShelf,
        HomeGroupKind.Featured,
        HomeGroupKind.DiscoverFeed,
    ];

    public static HomeLayoutDoc BuildDefault()
    {
        var modules = new HomeModuleSpec[DefaultOrder.Length];
        for (int i = 0; i < modules.Length; i++) modules[i] = new HomeModuleSpec(DefaultOrder[i]);
        return new HomeLayoutDoc(modules);
    }

    public static bool IsFixedLanding(HomeGroupKind kind)
    {
        var all = DefaultOrder;
        for (int i = 0; i < all.Length; i++)
            if (all[i] == kind) return true;
        return false;
    }

    /// <summary>Wire kind strings. Values are persisted — never rename one. An unknown string is the caller's
    /// carry problem, not a throw.</summary>
    public static string KindName(HomeGroupKind kind) => kind switch
    {
        HomeGroupKind.Hero => "hero",
        HomeGroupKind.QuickGrid => "quickGrid",
        HomeGroupKind.Shelf => "shelf",
        HomeGroupKind.Featured => "featured",
        HomeGroupKind.MixBand => "mixBand",
        HomeGroupKind.WeeklyPair => "weeklyPair",
        HomeGroupKind.ChipCards => "chipCards",
        HomeGroupKind.RadioDial => "radioDial",
        HomeGroupKind.RatedShelf => "ratedShelf",
        HomeGroupKind.QueueList => "queueList",
        HomeGroupKind.DiscoverFeed => "discoverFeed",
        HomeGroupKind.Recents => "recents",
        HomeGroupKind.Topic => "topic",
        HomeGroupKind.SectionEntry => "sectionEntry",
        HomeGroupKind.PodcastShelf => "podcastShelf",
        _ => "shelf",
    };

    /// <summary>Parse a wire kind. Returns false for a kind THIS build does not know — the caller preserves the raw
    /// module blob instead of dropping it.</summary>
    public static bool TryParseKind(string? s, out HomeGroupKind kind)
    {
        switch (s)
        {
            case "hero": kind = HomeGroupKind.Hero; return true;
            case "quickGrid": kind = HomeGroupKind.QuickGrid; return true;
            case "shelf": kind = HomeGroupKind.Shelf; return true;
            case "featured": kind = HomeGroupKind.Featured; return true;
            case "mixBand": kind = HomeGroupKind.MixBand; return true;
            case "weeklyPair": kind = HomeGroupKind.WeeklyPair; return true;
            case "chipCards": kind = HomeGroupKind.ChipCards; return true;
            case "radioDial": kind = HomeGroupKind.RadioDial; return true;
            case "ratedShelf": kind = HomeGroupKind.RatedShelf; return true;
            case "queueList": kind = HomeGroupKind.QueueList; return true;
            case "discoverFeed": kind = HomeGroupKind.DiscoverFeed; return true;
            case "recents": kind = HomeGroupKind.Recents; return true;
            case "topic": kind = HomeGroupKind.Topic; return true;
            case "sectionEntry": kind = HomeGroupKind.SectionEntry; return true;
            case "podcastShelf": kind = HomeGroupKind.PodcastShelf; return true;
            default: kind = HomeGroupKind.Shelf; return false;
        }
    }
}
