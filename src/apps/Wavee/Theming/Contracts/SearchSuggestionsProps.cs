// ── Theming/Contracts/SearchSuggestionsProps.cs — the props of `Shell.SearchSuggestions` (themes plan 6.0, T4b) ──────────────
//
// The omnibar's popup body is drawn BY the store's state: rows when there are some, a skeleton in the reserved height while a
// request is pending with nothing to show, "No results found" for a confirmed empty answer, a retry offer for a failure. The core
// owns the store (`OmnibarModel`, the request lifecycle in `Shell.Omnibar`) and hands the theme a plain snapshot of it:
//
//   SearchAnswer          the state, the query completions and the rich rows, both already cut to the visible caps
//   SearchRichRow         one rich row: its uri (the key the row is mounted under) and the builder of its facts. The builder runs
//                         INSIDE the row's render (it allocates the entity's table row if unseen, on the UI thread, as the row
//                         did before the move) and is bound to the item the row was mounted with
//   SearchRichRowFacts    what the shared media surface (slot mode) needs: title, meta line, art, play / menu / drag verbs and
//                         the trailing affordances (heart / Follow); the choice is the row component's (it knows its index)
//
// The sizing rules of the pending body (how tall the reserved skeleton is, how many rows it holds) are the core's and arrive as
// functions; the row heights are the values those rules are written in.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>Where the suggestion request stands. Only <see cref="Empty"/> may say "No results found".</summary>
public enum SearchAnswerState : byte
{
    /// <summary>No query: the field is blank and the popup is closed (a closing popup draws an empty body).</summary>
    Idle,

    /// <summary>A query exists and its answer has not landed: the progress slot runs over the previous answer's rows.</summary>
    Pending,

    /// <summary>There is an answer with rows.</summary>
    Results,

    /// <summary>A confirmed answer with no rows.</summary>
    Empty,

    /// <summary>The request did not produce an answer: a retry, never "nothing matched".</summary>
    Failed,
}

/// <summary>What one rich row shows and does (see the file header).</summary>
public sealed record SearchRichRowFacts
{
    /// <summary>The entity's uri text (the card's id).</summary>
    public required string Uri { get; init; }

    /// <summary>The row's title.</summary>
    public required string Title { get; init; }

    /// <summary>The meta line: the kind word and the detail, joined.</summary>
    public required string Meta { get; init; }

    /// <summary>The art url, or null.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>People are circles; everything else a rounded square.</summary>
    public bool Circular { get; init; }

    /// <summary>The row's play verb, or null for a row that cannot play (a profile, a genre).</summary>
    public Action? Play { get; init; }

    /// <summary>The row's context menu, built at open time, or null for a row with no menu.</summary>
    public Func<ContextMenuModel?>? Menu { get; init; }

    /// <summary>The drag source, or null for a row that is not draggable.</summary>
    public DragSource? Drag { get; init; }

    /// <summary>The heart / Follow affordances, or null for a row with no table entry.</summary>
    public Element? Trailing { get; init; }
}

/// <summary>One rich row: the key it mounts under and the builder of its facts.</summary>
/// <param name="Uri">The entity's uri text.</param>
/// <param name="Facts">Builds the facts; the argument runs after the row's play verb (the popup closes).</param>
public sealed record SearchRichRow(string Uri, Func<Action?, SearchRichRowFacts> Facts);

/// <summary>The store's state as the popup draws it: the visible query completions, then the visible rich rows.</summary>
/// <param name="State">Where the request stands.</param>
/// <param name="Queries">The query completions that fit the cap.</param>
/// <param name="Rows">The rich rows that fit the cap.</param>
public sealed record SearchAnswer(SearchAnswerState State, IReadOnlyList<string> Queries, IReadOnlyList<SearchRichRow> Rows);

/// <summary>What the popup body needs of the core, shared by every popup (the part of the props that does not change per popup).</summary>
public sealed record SearchSuggestionSource
{
    /// <summary>The store's state. Reading it subscribes the reader to the store's version.</summary>
    public required Func<SearchAnswer> Answer { get; init; }

    /// <summary>The typed text (the omnibar's text cell, read only).</summary>
    public required IReadSignal<string> Text { get; init; }

    /// <summary>The keyboard cursor over the visible rows; -1 = none.</summary>
    public required IReadSignal<int> Highlight { get; init; }

    /// <summary>Retry a failed request.</summary>
    public required Action Retry { get; init; }

    /// <summary>The height a pending body with nothing to show reserves, from the previous answer's row counts and whether it was a
    /// notice.</summary>
    public required Func<int, int, bool, float> PendingBodyHeight { get; init; }

    /// <summary>The skeleton rows a pending body is made of (query rows, rich rows), from the previous answer's row counts.</summary>
    public required Func<int, int, (int Query, int Rich)> PendingSkeletonRows { get; init; }

    /// <summary>The height one query row takes (its floor plus its margins).</summary>
    public required float QueryRowHeight { get; init; }

    /// <summary>The height one rich row takes (its floor plus its margins).</summary>
    public required float RichRowHeight { get; init; }

    /// <summary>The scroll viewport's cap, and so the most the body takes.</summary>
    public required float PopupBodyMaxHeight { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ShellSearchSuggestions"/>: one popup.</summary>
public sealed record SearchSuggestionsProps
{
    /// <summary>The core's side.</summary>
    public required SearchSuggestionSource Source { get; init; }

    /// <summary>The live width of the popup (the anchor's).</summary>
    public required IReadSignal<float> Width { get; init; }

    /// <summary>Choose the row at this index (query rows first); the caller closes the popup when it was a real choice.</summary>
    public required Action<int> Choose { get; init; }

    /// <summary>Close the popup, or null.</summary>
    public Action? Close { get; init; }
}
