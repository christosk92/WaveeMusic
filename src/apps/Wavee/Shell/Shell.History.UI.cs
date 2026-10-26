// ── Shell/Shell.History.UI.cs ──────────────────────────────────────────────────────────────────────────────────────
// the history page's core host
//
// Role: CORE host (the picture is the Common `Pages.History` view: `Themes\Common\CommonHistory.cs`, T11c)
// Owner: I (stage B, I2)
// Wave: 4
// Spec: ch 16 §0 item 15, §1.2, §2 W17-W21 + W25, §3.2, §3.3, §5 rows 27-28, §6.2, §9.1 item 14
//
// The navigation log as a page. EVERY decision it renders — the kinds, the filter, the search, the visible list, the most-visited fold, the date
// buckets, the timestamps, the unique count — is `Shell.cs` §11 (`Shell.History`), and the store is `Shell.Host.cs`. This file only projects
// them into the model the view draws (`HistoryPageModel`): the view owns the page's three UI states (search text, filter chip, sort) and asks for
// the projection under them.

using FluentGpu.Dsl;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Shell
{
    // MOUNT POINT (stage B contract)
    /// <summary>The `history` route's page. Registered by the frame through <see cref="SetPage"/>; the view reads the
    /// store's version signal itself and takes no route data.</summary>
    public static Element HistoryPage() => Slots.View(ViewKeys.HistoryPage, new HistoryPageProps
    {
        Version = History.Store.Version,
        Project = ProjectHistory,
    });

    /// <summary>The page's projection of the log under (search text, filter chip index, sort index): the visible list, its sort, the sections and
    /// their rows' facts, or the empty copy. Built per render of the view, like the page component's body was.</summary>
    static HistoryPageModel ProjectHistory(string query, int filterIndex, int sortIndex)
    {
        var filter = (History.Filter)Math.Clamp(filterIndex, 0, (int)History.Filter.Pages);
        var sort = sortIndex == 0 ? History.Sort.MostRecent : History.Sort.MostVisited;
        var entries = History.Store.Entries;
        var now = DateTime.Now;
        bool developerMode = FrameFacts.DeveloperNow;
        int total = entries.Count;
        int unique = History.CountUniqueRoutes(entries);

        var visible = History.Visible(entries, filter, query);
        if (visible.Count == 0)
        {
            var (title, caption, noMatch) = HistoryEmptyCopy(query, filter);
            return new HistoryPageModel
            {
                TotalVisits = total, UniqueRoutes = unique, Body = HistoryBodyKind.Empty, Sections = [],
                EmptyTitle = title, EmptyCaption = caption, EmptyIsNoMatch = noMatch,
            };
        }

        if (sort == History.Sort.MostVisited)
        {
            var counts = History.VisitCounts(entries);   // over the FULL unfiltered log
            var rowsInOrder = History.MostVisited(visible, counts);
            var rows = new HistoryRowFacts[rowsInOrder.Count];
            for (int k = 0; k < rows.Length; k++)
            {
                var e = rowsInOrder[k];
                int visits = counts.TryGetValue(NameOf(e.Route), out int v) ? v : 1;
                rows[k] = HistoryRowOf(e, now, developerMode, visitCount: visits);
            }
            return new HistoryPageModel
            {
                TotalVisits = total, UniqueRoutes = unique, Body = HistoryBodyKind.MostVisited,
                Sections = [new HistorySection(Loc.Get(Strings.Nav.History.MostVisited), null, rows)],
            };
        }

        var sections = new List<HistorySection>();
        int i = 0;
        while (i < visible.Count)
        {
            string label = History.DateGroupLabel(visible[i].VisitedAt, now);
            int start = i;
            while (i < visible.Count && History.DateGroupLabel(visible[i].VisitedAt, now) == label) i++;
            var rows = new HistoryRowFacts[i - start];
            for (int k = 0; k < rows.Length; k++)
                rows[k] = HistoryRowOf(visible[start + k], now, developerMode, visitCount: 0);
            sections.Add(new HistorySection(label, Strings.Nav.History.VisitCount(rows.Length), rows));
        }
        return new HistoryPageModel
        {
            TotalVisits = total, UniqueRoutes = unique, Body = HistoryBodyKind.DateGroups, Sections = sections,
        };
    }

    /// <summary>Three copies, picked by what narrowed the page: a search, a filter, or nothing logged at all.</summary>
    static (string Title, string Caption, bool NoMatch) HistoryEmptyCopy(string query, History.Filter filter)
    {
        if (query.Length > 0)
            return (Loc.Get(Strings.Nav.History.Empty.NoResults), Strings.Nav.History.Empty.NoMatch(query), true);
        if (filter != History.Filter.All)
            return (Loc.Get(Strings.Nav.History.Empty.NothingHere), Loc.Get(Strings.Nav.History.Empty.TryAll), true);
        return (Loc.Get(Strings.Nav.History.Empty.NoHistory), Loc.Get(Strings.Nav.History.Empty.StartNavigating), false);
    }

    /// <summary>One visit's facts. A route this build has no page for (retired, or developer-gated) is not reachable: the view keeps it inert.</summary>
    static HistoryRowFacts HistoryRowOf(HistoryEntry e, DateTime now, bool developerMode, int visitCount)
    {
        var (title, glyph) = Dest(e.Route);
        string kind = History.KindOf(e.Route);
        string kindLabel = kind switch
        {
            "playlist" => Loc.Get(Strings.Nav.History.Kind.Playlist),
            "show" => Loc.Get(Strings.Nav.History.Kind.Show),
            "library" => Loc.Get(Strings.Nav.History.Kind.Library),
            "search" => Loc.Get(Strings.Nav.History.Kind.Search),
            _ => Loc.Get(Strings.Nav.History.Kind.Page),   // a browse route labels itself Page — there is no "Browse" kind label
        };
        var route = e.Route;
        return new HistoryRowFacts(
            title, glyph, kindLabel, ArgOf(e.Route), History.FormatTimestamp(e.VisitedAt, now), visitCount,
            IsPlaylist: kind == "playlist", Reachable: IsKnown(e.Route, developerMode),
            Open: () => GoTo(route), Remove: () => HistoryVerbs.Remove(e));
    }
}
