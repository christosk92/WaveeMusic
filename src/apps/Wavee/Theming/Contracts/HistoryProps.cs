// ── Theming/Contracts/HistoryProps.cs — the props of the history page (themes plan 6.5, T11c) ──────────────────────────────────────
//
// The page keeps its three UI states (the search text, the filter chip, the sort) as the VIEW's own signals and asks the core for the projection of
// the log under them (`Project`): the kinds, the filter, the search, the visible list, the most-visited fold, the date buckets, the timestamps and the
// unique count are `Shell.History`'s (`Shell\Shell.cs` section 11); the store is `Shell.Host.cs`. A re-render is asked for by the log's version signal and by
// the view's own signals, exactly as the page component's was.

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.HistoryPage"/>. Equal for every mount (the delegate is one cached static), so the slot's template re-runs
/// on the signals it reads, never on a changed record.</summary>
public sealed record HistoryPageProps
{
    /// <summary>The log's version: bumped on every mutation; the page re-renders on it.</summary>
    public required IReadSignal<int> Version { get; init; }

    /// <summary>The projection of the log under (search text, filter chip index, sort index): what the page shows.</summary>
    public required Func<string, int, int, HistoryPageModel> Project { get; init; }
}

/// <summary>What the body shows.</summary>
public enum HistoryBodyKind : byte
{
    /// <summary>Nothing to list: <see cref="HistoryPageModel.EmptyTitle"/> and <see cref="HistoryPageModel.EmptyCaption"/> say why.</summary>
    Empty,
    /// <summary>Most recent: one card per date bucket, the last row of each without a divider.</summary>
    DateGroups,
    /// <summary>Most visited: ONE flat card, every row with its divider (the card clips the last), no count pill.</summary>
    MostVisited,
}

/// <summary>The page's projection of the log (built per render by the core).</summary>
public sealed record HistoryPageModel
{
    /// <summary>The whole log's length (the first stat pill).</summary>
    public required int TotalVisits { get; init; }

    /// <summary>The number of distinct routes in the whole log (the second stat pill).</summary>
    public required int UniqueRoutes { get; init; }

    /// <summary>What the body shows.</summary>
    public required HistoryBodyKind Body { get; init; }

    /// <summary>The sections of the body (none when <see cref="Body"/> is <see cref="HistoryBodyKind.Empty"/>).</summary>
    public required IReadOnlyList<HistorySection> Sections { get; init; }

    /// <summary>The empty copy's headline.</summary>
    public string EmptyTitle { get; init; } = "";

    /// <summary>The empty copy's caption.</summary>
    public string EmptyCaption { get; init; } = "";

    /// <summary>The empty state says "no match" (a search or a filter narrowed the page) rather than "nothing logged".</summary>
    public bool EmptyIsNoMatch { get; init; }
}

/// <summary>One card of the body: its header and its rows.</summary>
public sealed record HistorySection(string Label, string? Count, IReadOnlyList<HistoryRowFacts> Rows);

/// <summary>One visit. <see cref="Reachable"/> false: this build has no page for the route (retired, or developer-gated), so the row stays in the log inert
/// while its delete stays live.</summary>
public readonly record struct HistoryRowFacts(
    string Title, string Glyph, string KindLabel, string? Arg, string Timestamp, int VisitCount, bool IsPlaylist, bool Reachable,
    Action Open, Action Remove);
