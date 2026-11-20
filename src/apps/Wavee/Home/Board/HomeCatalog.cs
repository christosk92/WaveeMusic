// ── Home/Board/HomeCatalog.cs: everything Home can hold, and one search over it (design 1.4) ────────────────────────
//
// Widgets from the registry, the answer's sections (hidden ones first), library items from the sidebar's local
// projection. No network call. Find is the concept's found(): every word typed must be found in the title, the
// description, the source's kind words or the group word; case- and diacritic-insensitive (SidebarSearch's folding).

using System.Collections.Immutable;
using FluentGpu.Localization;

namespace Wavee;

public enum HomeCatalogGroup : byte { Widgets, Spotify, Library, Extensions }

public sealed record HomeCandidate(string Id, HomeCatalogGroup Group, HomeSource Source, string Title, string Line,
    string TargetGroupId, bool OnHome, string? Cover, bool Round)
{
    /// <summary>The words a search matches beyond the title and the line: the source's kind words and the group word.</summary>
    public string Terms { get; init; } = "";

    /// <summary>The board entry this candidate already is (OnHome), else null.</summary>
    public string? EntryId { get; init; }
}

/// <summary>Hit counts per catalogue group, and how many of the hits are on Home already or still to add.</summary>
public readonly record struct HomeCatalogCounts(int Widgets, int Spotify, int Library, int Extensions, int OnHome, int ToAdd)
{
    public int Total => Widgets + Spotify + Library + Extensions;

    public int Of(HomeCatalogGroup g) => g switch
    {
        HomeCatalogGroup.Widgets => Widgets,
        HomeCatalogGroup.Spotify => Spotify,
        HomeCatalogGroup.Library => Library,
        _ => Extensions,
    };
}

public static class HomeCatalog
{
    /// <summary>The library's unfiltered list (the sidebar's local projection); replaceable for the inspector.</summary>
    public static Func<IReadOnlyList<SidebarLibraryEntry>> Library { get; set; } = static () => Sidebar.Model.LibraryAll;

    /// <summary>Everything Home can hold right now: widgets, the answer's sections (hidden ones first), library items.</summary>
    public static IReadOnlyList<HomeCandidate> All(HomeDoc doc, BoardModel shown, HomeSourceRegistry sources)
    {
        var list = new List<HomeCandidate>(64);
        string widgetWord = Loc.Get("home.board.catalog.terms.widgets");
        string spotifyWord = Loc.Get("home.board.catalog.terms.spotify");
        string libraryWord = Loc.Get("home.board.catalog.terms.library");

        foreach (var def in sources.Widgets)
        {
            string name = HomeSourceRegistry.WidgetName(def.Id) ?? "";
            string? entry = null;
            foreach (var e in shown.Entries)
                if (e.Raw is WidgetSource w && w.Widget == name) { entry = e.Id; break; }
            list.Add(new HomeCandidate(def.Id, HomeCatalogGroup.Widgets, new WidgetSource(name, null), Loc.Get(def.TitleKey),
                Loc.Get(def.DescriptionKey), def.DefaultGroup, entry is not null, null, false)
            { Terms = Loc.Get(def.SearchTermsKey) + " " + widgetWord, EntryId = entry });
        }

        // Sections: hidden ones first (the user is most likely looking for what they hid), then the rest in board order.
        var sectionDef = sources.ById(HomeSourceRegistry.Section);
        string sectionTerms = (sectionDef is null ? "" : Loc.Get(sectionDef.SearchTermsKey)) + " " + spotifyWord;
        string sectionLine = sectionDef is null ? "" : Loc.Get(sectionDef.DescriptionKey);
        for (int pass = 0; pass < 2; pass++)
            foreach (var e in shown.Entries)
            {
                if (e.Raw is not SpotifySectionSource s || e.Kind is BoardEntryKind.Fold) continue;
                bool hidden = e.Hidden || HomeCommands.IsHidden(doc, s.Ref);
                if ((pass == 0) != hidden) continue;
                string title = e.Title.Length > 0 ? e.Title : s.TitleHint;
                if (title.Length == 0) continue;
                list.Add(new HomeCandidate("section:" + s.Ref.Key, HomeCatalogGroup.Spotify, s with { TitleHint = title }, title, sectionLine,
                    HomeDocs.Spotify, !hidden, null, false) { Terms = sectionTerms, EntryId = hidden ? null : e.Id });
            }

        var pinDef = sources.ById(HomeSourceRegistry.Pin);
        string pinTerms = (pinDef is null ? "" : Loc.Get(pinDef.SearchTermsKey)) + " " + libraryWord;
        IReadOnlyList<SidebarLibraryEntry> library;
        try { library = Library(); } catch (InvalidOperationException) { library = []; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in library)
        {
            if (l.Kind is not (SidebarEntryKind.Playlist or SidebarEntryKind.Album or SidebarEntryKind.Artist or SidebarEntryKind.Show or SidebarEntryKind.AppRoute)) continue;
            if (HomePinId.Canonical(l.Uri.Length > 0 ? l.Uri : l.Id) is not { } pinId || !seen.Add(pinId)) continue;
            string cover = l.Cover.IsEmpty ? "" : Entities.Strings.Resolve(l.Cover);
            var (pg, pi) = HomeCommands.FindPin(doc, pinId);
            string kindWord = KindWord(l.Kind);
            list.Add(new HomeCandidate("pin:" + pinId, HomeCatalogGroup.Library, new PinSource(pinId, l.Name, cover), l.Name,
                l.Creator.Length > 0 ? kindWord + " · " + l.Creator : kindWord, HomeDocs.Pinned, pg >= 0, cover.Length > 0 ? cover : null, l.Circular)
            { Terms = pinTerms + " " + kindWord, EntryId = pg >= 0 ? doc.Groups[pg].Items[pi].Id : null });
        }
        return list;
    }

    static string KindWord(SidebarEntryKind k) => k switch
    {
        SidebarEntryKind.Playlist => Loc.Get("home.board.catalog.kind.playlist"),
        SidebarEntryKind.Album => Loc.Get("home.board.catalog.kind.album"),
        SidebarEntryKind.Artist => Loc.Get("home.board.catalog.kind.artist"),
        SidebarEntryKind.Show => Loc.Get("home.board.catalog.kind.show"),
        _ => Loc.Get("home.board.catalog.kind.page"),
    };

    /// <summary>Every word typed must be found in the title, the description, the source's kind words or the group word.
    /// Case- and diacritic-insensitive. Pure. An empty query finds nothing (the surface shows the whole list).</summary>
    public static IReadOnlyList<HomeCandidate> Find(IReadOnlyList<HomeCandidate> all, string query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        var hits = new List<HomeCandidate>();
        foreach (var c in all)
        {
            bool every = true;
            foreach (var w in words)
                if (!SidebarSearch.Contains(c.Title, w) && !SidebarSearch.Contains(c.Line, w) && !SidebarSearch.Contains(c.Terms, w))
                { every = false; break; }
            if (every) hits.Add(c);
        }
        return hits;
    }

    public static HomeCatalogCounts Count(IReadOnlyList<HomeCandidate> hits)
    {
        int w = 0, s = 0, l = 0, x = 0, on = 0;
        foreach (var c in hits)
        {
            switch (c.Group)
            {
                case HomeCatalogGroup.Widgets: w++; break;
                case HomeCatalogGroup.Spotify: s++; break;
                case HomeCatalogGroup.Library: l++; break;
                default: x++; break;
            }
            if (c.OnHome) on++;
        }
        return new HomeCatalogCounts(w, s, l, x, on, hits.Count - on);
    }

    /// <summary>The sentence a search speaks: "3 results for mix." / "Nothing to add matches zzzz."</summary>
    public static string Spoken(string query, int count)
        => count == 0 ? Loc.Format("home.board.search.none", ("query", query))
            : Loc.Format("home.board.search.count", ("count", count), ("query", query));
}
