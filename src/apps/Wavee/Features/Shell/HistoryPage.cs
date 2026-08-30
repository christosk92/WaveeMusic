using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Browse;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// AOT-safe JSON serialization for the history log. TicksUtc avoids DateTimeKind round-trip quirks.
internal record struct HistoryEntryDto(string Name, string? Arg, long TicksUtc);

[JsonSerializable(typeof(HistoryEntryDto[]))]
internal partial class HistoryJsonCtx : JsonSerializerContext { }

// HistoryEntry: one navigation event with its destination and timestamp.
// Kind drives filter chips; VisitCount is computed from the full log by the page (no stored state needed).
public sealed record HistoryEntry(Route Route, DateTime VisitedAt)
{
    public string Kind =>
        Route.Name.StartsWith("pl:", StringComparison.Ordinal) ? "playlist" :
        // A show route opens the shared detail surface (like album/artist), so it is an ENTITY visit, not a generic page —
        // it gets its own kind so the filter chips and the row label stay truthful now that ShellNav.Dest names it.
        Route.Name.StartsWith("show:", StringComparison.Ordinal) ? "show" :
        Route.Name is "albums" or "artists" or "liked" or "podcasts" or "local" ? "library" :
        Route.Name == "search" ? "search" :
        BrowseRoutes.IsHome(Route.Name) ? "browse" :
        "page";
}

// The live navigation log. Created once in WaveeShell, provided via Slot context.
// NavCtx carries the app-wide Go action so pages deep in the tree can navigate without frozen ctor args.
// Persistence: JSON file at the path set via Init(); LoadFromDisk() on startup, SaveToDisk() on every mutation.
// Thread-safety: all mutations run on the UI thread; SaveToDisk snapshots before handing off to the pool.
public sealed class HistoryStore
{
    const int MaxEntries = 500;

    readonly List<HistoryEntry> _entries = [];
    readonly Signal<int> _version = new(0);
    string? _path;

    public IReadSignal<int> Version => _version;
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    // Call once (before LoadFromDisk) with the full file path for the JSON history log.
    public void Init(string historyFilePath) => _path = historyFilePath;

    public void LoadFromDisk()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var bytes = File.ReadAllBytes(_path);
            var dtos  = JsonSerializer.Deserialize(bytes, HistoryJsonCtx.Default.HistoryEntryDtoArray);
            if (dtos is null) return;
            foreach (var d in dtos)
                _entries.Add(new HistoryEntry(new Route(d.Name, d.Arg),
                             new DateTime(d.TicksUtc, DateTimeKind.Utc).ToLocalTime()));
            // No _version bump here — no listeners exist yet at startup time
        }
        catch { /* corrupt or unreadable file — start empty */ }

        // One-time purge of the demo seed the shell used to write on a fresh install (routes under "pl:local:"):
        // those playlist uris address nothing, so every such row is a dead destination the page would still offer.
        // Rewriting the file here — rather than filtering at render time — means the log stops carrying them at all.
        int before = _entries.Count;
        _entries.RemoveAll(static e => e.Route.Name.StartsWith(DeadSeedPrefix, StringComparison.Ordinal));
        if (_entries.Count != before) SaveToDisk();
    }

    /// <summary>Route prefix of the removed fake-history seed. Nothing legitimate mints it: a real playlist route is
    /// <c>pl:spotify:playlist:…</c>.</summary>
    const string DeadSeedPrefix = "pl:local:";

    public void Add(Route r)
    {
        _entries.Add(new HistoryEntry(r, DateTime.Now));
        if (_entries.Count > MaxEntries) _entries.RemoveAt(0);   // FIFO evict oldest
        _version.Value++;
        SaveToDisk();
    }

    public void Remove(HistoryEntry e)
    {
        if (!_entries.Remove(e)) return;
        _version.Value++;
        SaveToDisk();
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        _version.Value++;
        if (_path is { } p)
            _ = Task.Run(() => { try { File.Delete(p); } catch { } });
    }

    void SaveToDisk()
    {
        if (_path is null) return;
        // Snapshot on the UI thread; the pool thread only touches the snapshot array and the path string.
        int count    = Math.Min(_entries.Count, MaxEntries);
        int start    = _entries.Count - count;
        var snapshot = new HistoryEntryDto[count];
        for (int i = 0; i < count; i++)
        {
            var e = _entries[start + i];
            snapshot[i] = new HistoryEntryDto(e.Route.Name, e.Route.Arg, e.VisitedAt.ToUniversalTime().Ticks);
        }
        string path = _path;
        _ = Task.Run(() =>
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null) Directory.CreateDirectory(dir);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, HistoryJsonCtx.Default.HistoryEntryDtoArray);
                File.WriteAllBytes(path, bytes);
            }
            catch { /* best-effort — a failed write is not fatal */ }
        });
    }

    public static readonly Context<HistoryStore?> Slot =
        new(null);
    public static readonly Context<Action<string, string?>> NavCtx =
        new((_, _) => { });
    /// <summary>Forward navigation that can attach a parent crumb (the journey answer). <see cref="NavCtx"/> stays
    /// the 2-arg wrapper that writes a null origin — latest arrival wins, including a clear.</summary>
    public static readonly Context<Action<string, string?, NavOrigin?>> GoWithOrigin =
        new((_, _, _) => { });
    public static readonly Context<NavOriginStore?> OriginSlot = new(null);
    /// <summary>The shell's real browser-style Back operation. Null outside a shell mount, where pages may choose a
    /// deterministic fallback without manufacturing a forward history entry.</summary>
    public static readonly Context<Action?> BackCtx = new(null);
}

// ── History page ──────────────────────────────────────────────────────────────────────────────────────────────────────
// A Spotify-/browser-style history view: date-grouped rows, live search, SelectorBar filter, visit-count badges,
// ComboBox sort, and per-row + clear-all remove actions.
sealed class HistoryPage : Component
{
    // Component-field signals persist across renders (Component instance lives for the page's lifetime).
    readonly Signal<string> _search      = new("");
    readonly Signal<int>    _filterIndex = new(0);   // index into s_filterLabels
    readonly Signal<int>    _sortIndex   = new(0);   // 0=Most recent, 1=Most visited

    // Shows sits after Playlists (the two entity kinds together) and before the generic buckets. Appending a chip is safe
    // for the persisted filter index only because nothing persists it — _filterIndex is per-mount page state.
    enum FilterKind { All, Playlists, Shows, Library, Search, Pages }
    enum SortKind   { Recent, MostVisited }

    static readonly string[] s_filterLabels =
    [
        Loc.Get(Strings.Nav.History.Filter.All),
        Loc.Get(Strings.Nav.History.Filter.Playlists),
        Loc.Get(Strings.Nav.History.Filter.Shows),
        Loc.Get(Strings.Nav.History.Filter.Library),
        Loc.Get(Strings.Nav.History.Filter.Search),
        Loc.Get(Strings.Nav.History.Filter.Pages),
    ];
    static readonly string[] s_sortLabels   =
    [
        Loc.Get(Strings.Nav.History.Sort.MostRecent),
        Loc.Get(Strings.Nav.History.Sort.MostVisited),
    ];

    public override Element Render()
    {
        var store   = UseContext(HistoryStore.Slot);
        var go      = UseContext(HistoryStore.NavCtx);
        // Read BEFORE the null-store early return: hooks run in stable call order.
        var overlay = UseContext(Overlay.Service);
        if (store is null) return new BoxEl { Grow = 1f };

        _ = store.Version.Value;                           // subscribe → re-render when entries change
        string     search      = _search.Value;            // subscribe
        FilterKind filter      = (FilterKind)_filterIndex.Value; // subscribe
        SortKind   sort        = _sortIndex.Value == 0 ? SortKind.Recent : SortKind.MostVisited; // subscribe
        int        filterIndex = (int)filter;

        var entries = store.Entries;
        var now     = DateTime.Now;

        // Build the filtered view — reversed so most-recent is first.
        var visible = new List<HistoryEntry>(entries.Count);
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (!PassesFilter(e, filter)) continue;
            if (search.Length > 0 && !PassesSearch(e, search)) continue;
            visible.Add(e);
        }

        // Visit-count map (over the FULL unfiltered log) — used for badges in MostVisited mode.
        Dictionary<string, int>? visitCounts = null;
        if (sort == SortKind.MostVisited)
        {
            visitCounts = new Dictionary<string, int>(entries.Count);
            foreach (var e in entries)
            {
                var k = e.Route.Name;
                visitCounts[k] = visitCounts.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            // Re-order visible list by descending visit count (stable — most-recent within same count).
            visible.Sort((a, b) =>
            {
                int ca = visitCounts.TryGetValue(a.Route.Name, out int va) ? va : 1;
                int cb = visitCounts.TryGetValue(b.Route.Name, out int vb) ? vb : 1;
                return cb.CompareTo(ca);
            });
        }

        // Stats line — shown in header
        int totalVisits  = entries.Count;
        int uniqueRoutes = CountUniqueRoutes(entries);

        // Body content: grouped list or empty state
        Element body;
        if (visible.Count == 0)
        {
            body = EmptyState(search, filter);
        }
        else if (sort == SortKind.MostVisited)
        {
            body = MostVisitedList(visible, visitCounts!, store, go, now);
        }
        else
        {
            body = DateGroupedList(visible, store, go, now);
        }

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                PageHeader(store, overlay, search, filterIndex, totalVisits, uniqueRoutes),
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = Spacing.L,
                    Padding = new Edges4(Spacing.PageWide, Spacing.M, Spacing.PageWide, Spacing.XXL),
                    Children = [ body ],
                }) with { Grow = 1f },
            ],
        };
    }

    // ── Filtering / search ────────────────────────────────────────────────────────────────────────
    static bool PassesFilter(HistoryEntry e, FilterKind f) => f switch
    {
        FilterKind.Playlists => e.Kind == "playlist",
        FilterKind.Shows     => e.Kind == "show",
        FilterKind.Library   => e.Kind == "library",
        FilterKind.Search    => e.Kind == "search",
        FilterKind.Pages     => e.Kind is "page" or "browse",
        _                    => true,
    };

    static bool PassesSearch(HistoryEntry e, string q)
    {
        var (title, _) = ShellNav.Dest(e.Route);
        return title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (e.Route.Arg?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
               e.Route.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    static int CountUniqueRoutes(IReadOnlyList<HistoryEntry> entries)
    {
        var seen = new HashSet<string>(entries.Count);
        foreach (var e in entries) seen.Add(e.Route.Name);
        return seen.Count;
    }

    // ── Date grouping helpers ─────────────────────────────────────────────────────────────────────
    static string DateGroupLabel(DateTime dt, DateTime now)
    {
        int days = (now.Date - dt.Date).Days;
        return days switch
        {
            0    => Loc.Get(Strings.Nav.History.Group.Today),
            1    => Loc.Get(Strings.Nav.History.Group.Yesterday),
            < 7  => Loc.Get(Strings.Nav.History.Group.ThisWeek),
            < 30 => Loc.Get(Strings.Nav.History.Group.ThisMonth),
            _    => Loc.Get(Strings.Nav.History.Group.Earlier),
        };
    }

    static string FormatTimestamp(DateTime dt, DateTime now)
    {
        int days = (now.Date - dt.Date).Days;
        string t = dt.ToString("HH:mm");
        return days switch
        {
            0   => t,
            1   => Strings.Nav.History.Ts.Yesterday(t),
            < 7 => Strings.Nav.History.Ts.Weekday(dt.ToString("dddd"), t),
            _   => Strings.Nav.History.Ts.Date(dt.ToString("MMM d yyyy"), t),
        };
    }

    // ── Date-grouped list ─────────────────────────────────────────────────────────────────────────
    static Element DateGroupedList(List<HistoryEntry> entries, HistoryStore store, Action<string, string?> go, DateTime now)
    {
        var sections = new List<Element>();
        int i = 0;
        while (i < entries.Count)
        {
            string label = DateGroupLabel(entries[i].VisitedAt, now);
            int start = i;
            while (i < entries.Count && DateGroupLabel(entries[i].VisitedAt, now) == label) i++;
            // Slice entries[start..i)
            var rows = new Element[i - start];
            for (int k = 0; k < rows.Length; k++)
                rows[k] = EntryRow(entries[start + k], store, go, now, visitCount: 0, showDivider: k < rows.Length - 1);
            sections.Add(DateGroup(label, i - start, rows));
        }
        return new BoxEl { Direction = 1, Gap = Spacing.L, Children = sections.ToArray() };
    }

    static Element DateGroup(string label, int count, Element[] rows)
    {
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            Children =
            [
                // Section header: label • divider line • count badge
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Margin = new Edges4(0f, 0f, 0f, 4f),
                    Children =
                    [
                        WaveeType.Eyebrow(label) with { Color = Tok.TextSecondary },
                        new BoxEl { Grow = 1f, Height = 1f, Margin = new Edges4(4f, 0f, 4f, 0f), Fill = Tok.StrokeDividerDefault },
                        new BoxEl
                        {
                            Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.FullAll,
                            Fill = Tok.FillSubtleSecondary,
                            Children = [ new TextEl(Strings.Nav.History.VisitCount(count)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary } ],
                        },
                    ],
                },
                // Card containing all rows for this group
                new BoxEl
                {
                    Direction = 1, Corners = CornerRadius4.All(Radii.Card),
                    Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    ClipToBounds = true,
                    Children = rows,
                },
            ],
        };
    }

    // ── Most-visited flat list (deduplicated by route) ────────────────────────────────────────────
    static Element MostVisitedList(List<HistoryEntry> entries, Dictionary<string, int> counts,
                                   HistoryStore store, Action<string, string?> go, DateTime now)
    {
        // Deduplicate: keep only the most-recent entry per route (entries is already sorted by count desc).
        var seen = new HashSet<string>(entries.Count);
        var rows  = new List<Element>(entries.Count);
        foreach (var e in entries)
        {
            if (!seen.Add(e.Route.Name)) continue;
            int vc = counts.TryGetValue(e.Route.Name, out int v) ? v : 1;
            rows.Add(EntryRow(e, store, go, now, visitCount: vc, showDivider: true));
        }
        // Remove last divider (trim) — can't do that easily without knowing last; just leave it, the card clips.
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Margin = new Edges4(0f, 0f, 0f, 4f),
                    Children =
                    [
                        WaveeType.Eyebrow(Loc.Get(Strings.Nav.History.MostVisited)) with { Color = Tok.TextSecondary },
                        new BoxEl { Grow = 1f, Height = 1f, Margin = new Edges4(4f, 0f, 4f, 0f), Fill = Tok.StrokeDividerDefault },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Corners = CornerRadius4.All(Radii.Card),
                    Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    ClipToBounds = true,
                    Children = rows.ToArray(),
                },
            ],
        };
    }

    // ── Single entry row ──────────────────────────────────────────────────────────────────────────
    static Element EntryRow(HistoryEntry e, HistoryStore store, Action<string, string?> go,
                             DateTime now, int visitCount, bool showDivider)
    {
        var (title, glyph) = ShellNav.Dest(e.Route);
        string ts         = FormatTimestamp(e.VisitedAt, now);
        bool isPlaylist   = e.Kind == "playlist";
        // A log entry outlives the routes that wrote it — a destination can be retired, and an older file can still
        // carry keys this build has no page for. ShellNav.Dest would happily label such a row ("Your Library") and the
        // row would then navigate to the not-found page. Render it dimmed and inert instead: still a record of where
        // the user went, no longer an offer to go back there. The DELETE button stays live, so the row is removable.
        bool reachable    = ShellRoutes.IsKnown(e.Route.Name);

        // Kind label + color
        string kindLabel = e.Kind switch
        {
            "playlist" => Loc.Get(Strings.Nav.History.Kind.Playlist),
            "show"     => Loc.Get(Strings.Nav.History.Kind.Show),
            "library"  => Loc.Get(Strings.Nav.History.Kind.Library),
            "search"   => Loc.Get(Strings.Nav.History.Kind.Search),
            _          => Loc.Get(Strings.Nav.History.Kind.Page),
        };
        ColorF iconFg  = isPlaylist ? Tok.AccentDefault : Tok.TextSecondary;
        ColorF iconBg  = isPlaylist
            ? Tok.AccentDefault with { A = 0.12f }
            : Tok.FillSubtleSecondary;

        var rowChildren = new List<Element>
        {
            // Icon box
            new BoxEl
            {
                Width = 36f, Height = 36f, Corners = CornerRadius4.All(Radii.Control),
                Fill = iconBg, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [ Icon(glyph, 16f, iconFg) ],
            },
            // Title + kind badge
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = 2f,
                Children =
                [
                    Body(title) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                        Children = BuildKindAndArg(e, kindLabel),
                    },
                ],
            },
            // Timestamp
            new TextEl(ts) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
        };

        // Visit-count badge (only shown in MostVisited mode when visitCount > 0)
        if (visitCount > 1)
        {
            rowChildren.Add(new BoxEl
            {
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
                Fill = Tok.FillSubtleSecondary,
                Children = [ new TextEl(Strings.Nav.History.VisitMultiplier(visitCount)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary } ],
            });
        }

        // Delete button
        rowChildren.Add(new BoxEl
        {
            Width = 28f, Height = 28f, Margin = new Edges4(0f, 0f, 0f, 0f),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            Opacity = 0.5f,
            OnClick = () => store.Remove(e),
            Children = [ Icon(Icons.Cancel, 12f, Tok.TextSecondary) ],
        }.Interactive(Interaction.Subtle));

        Element row = new BoxEl
        {
            Direction = 0, Height = 56f, AlignItems = FlexAlign.Center,
            Gap = Spacing.M, Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
            Opacity = reachable ? 1f : 0.6f,
            OnClick = reachable ? (Action)(() => go(e.Route.Name, e.Route.Arg)) : null,
            Children = rowChildren.ToArray(),
            // isEnabled goes THROUGH Interactive: the recipe writes IsEnabled itself (and picks the disabled resting
            // fill from the StateBrush), so setting it in the initializer above would be silently overwritten.
        }.Interactive(Interaction.Subtle, isEnabled: reachable);

        if (!showDivider) return row;

        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                row,
                new BoxEl { Height = 1f, Margin = new Edges4(Spacing.M + 36f + Spacing.M, 0f, 0f, 0f), Fill = Tok.StrokeDividerDefault },
            ],
        };
    }

    static Element[] BuildKindAndArg(HistoryEntry e, string kindLabel)
    {
        var items = new List<Element>
        {
            new TextEl(kindLabel) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
        };
        if (e.Route.Arg is { Length: > 0 } arg)
        {
            items.Add(new TextEl("·") { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary });
            items.Add(new TextEl(arg) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 });
        }
        return items.ToArray();
    }

    // ── Empty state ───────────────────────────────────────────────────────────────────────────────
    static Element EmptyState(string search, FilterKind filter)
    {
        bool isSearch = search.Length > 0;
        bool isFilter = filter != FilterKind.All;
        string heading = isSearch ? Loc.Get(Strings.Nav.History.Empty.NoResults)
                       : isFilter ? Loc.Get(Strings.Nav.History.Empty.NothingHere)
                                   : Loc.Get(Strings.Nav.History.Empty.NoHistory);
        string sub = isSearch
            ? Strings.Nav.History.Empty.NoMatch(search)
            : isFilter ? Loc.Get(Strings.Nav.History.Empty.TryAll)
            : Loc.Get(Strings.Nav.History.Empty.StartNavigating);

        // Qualified: this page has a private EmptyState(...) BUILDER method of its own that shadows the type name.
        return Wavee.EmptyState.Build(heading, sub);
    }

    // ── Page header (title + stats + search + SelectorBar filter + ComboBox sort) ─────────────────
    Element PageHeader(HistoryStore store, IOverlayService? overlay, string search, int filterIndex,
                       int totalVisits, int uniqueRoutes)
    {
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.M),
            // No Fill — inherits the content card background so it doesn't conflict in light theme
            BorderColor = Tok.StrokeDividerDefault, BorderWidth = 0f,
            Children =
            [
                // ── Title row ──────────────────────────────────────────────────────
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        Icon(Icons.Clock, 22f, Tok.TextPrimary),
                        WaveeType.PageHero(Loc.Get(Strings.Nav.History.Title)) with { Grow = 1f },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                            Children =
                            [
                                StatPill($"{totalVisits}", Loc.Get(Strings.Nav.History.Stat.Visits)),
                                StatPill($"{uniqueRoutes}", Loc.Get(Strings.Nav.History.Stat.Unique)),
                            ],
                        },
                        // Destructive and unrecoverable (Clear also DELETES the log file), so it asks first — the
                        // same ContentDialog confirmation the Settings storage tab puts in front of a cache wipe.
                        Button.Standard(Loc.Get(Strings.Nav.History.ClearAll), () => SettingsShared.Confirm(
                            overlay,
                            Loc.Get(Strings.Nav.History.ClearAllConfirm),
                            Loc.Get(Strings.Nav.History.ClearAllConfirmBody),
                            Loc.Get(Strings.Nav.History.ClearAll),
                            store.Clear)),
                    ],
                },
                // ── Search box — fills the header width on its own (grow self-measures + cross-stretches). ──
                AutoSuggestBox.Create(
                    Array.Empty<string>(),
                    placeholder: Loc.Get(Strings.Nav.History.SearchPlaceholder),
                    grow: 1f,
                    text: _search,
                    onQuerySubmitted: q => _search.Value = q,
                    onChange: q => _search.Value = q,
                    minHeight: 36f,
                    cornerRadius: Radii.Control),
                // ── Filter (SelectorBar) + Sort (ComboBox) ────────────────────────
                // Bottom margin clears the SelectorBar's selection underline from the first scroll-content group header.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.S),
                    Children =
                    [
                        SelectorBar.Create(s_filterLabels, _filterIndex),
                        new BoxEl { Grow = 1f },
                        ComboBox.Create(s_sortLabels, _sortIndex, width: 160f),
                    ],
                },
            ],
        };
    }

    static Element StatPill(string value, string label) =>
        new BoxEl
        {
            Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
            Fill = Tok.FillSubtleSecondary,
            Children =
            [
                new TextEl(value) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary },
                new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary },
            ],
        };
}
