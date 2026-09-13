using System;
using Wavee.Core;

namespace Wavee;

/// <summary>Where the omnibar's suggestion request stands. The popup renders BY this, and only <see cref="Empty"/> may
/// say "No results found": a confirmed empty answer, never a request that has not been issued or answered yet.</summary>
public enum SuggestState : byte
{
    /// <summary>No query — the field is blank. The popup is closed.</summary>
    Idle,
    /// <summary>A query exists and its answer has not landed — including the debounce window BEFORE the request is
    /// even sent. The popup shows a progress row (plus any rows the previous answer left) and no sentence.</summary>
    Pending,
    /// <summary>The current query's answer landed with at least one row.</summary>
    Results,
    /// <summary>The current query's answer landed with nothing in it.</summary>
    Empty,
    /// <summary>The current query's request did not produce an answer (transport, HTTP, parse). Distinct from
    /// <see cref="Empty"/> — the popup offers a retry rather than claiming nothing matched.</summary>
    Failed,
}

/// <summary>
/// The omnibar's suggestion request lifecycle — a generation-keyed state machine, engine-free (System + Wavee.Core) so
/// the whole decision is unit-tested rather than inferred from a popup at 6 fps.
/// <para>
/// Two clocks used to disagree: the engine AutoSuggestBox opens its popup synchronously on the keystroke, while the
/// omnibar's fetch is debounced 150 ms behind it, and the popup's only two no-rows states were "loading" and
/// "No results found" — so every first letter flashed the sentence for the whole debounce window. Here
/// <see cref="Begin"/> is called on the UNDEBOUNCED keystroke and enters <see cref="SuggestState.Pending"/> at once;
/// the debounced fetch later answers the generation <see cref="Begin"/> handed out, and an answer for any other
/// generation is dropped (<see cref="Complete"/> / <see cref="Fail"/> return false). Equality of the query text is
/// deliberately NOT the publish guard: a superseded response for a retyped query must lose to the newer request even
/// when the two texts are identical.
/// </para>
/// <para>
/// Threading: every member is called on the UI thread (the omnibar posts its completions back before publishing), so
/// there is no lock. <see cref="Changed"/> fires synchronously after every state change on that same thread.
/// </para>
/// </summary>
sealed class OmnibarSuggestQuery
{
    int _generation;

    /// <summary>The generation the NEXT answer must carry to be published. Advances on every <see cref="Begin"/> that
    /// changes the query, on <see cref="Retry"/> and on <see cref="Clear"/>.</summary>
    public int Generation => _generation;

    /// <summary>The trimmed query the current generation is for ("" while <see cref="SuggestState.Idle"/>).</summary>
    public string Query { get; private set; } = "";

    public SuggestState State { get; private set; } = SuggestState.Idle;

    /// <summary>The rows to show. While <see cref="SuggestState.Pending"/> these are the PREVIOUS answer's rows (they
    /// stay under the progress bar until the new answer replaces them — the field does not blank on every keystroke);
    /// <see cref="SearchSuggestions.Empty"/> in every other non-<see cref="SuggestState.Results"/> state.</summary>
    public SearchSuggestions Suggestions { get; private set; } = SearchSuggestions.Empty;

    /// <summary>What the current generation's request died of; null unless <see cref="SuggestState.Failed"/>.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The failure's message, for the diagnostics line; the popup shows the localized sentence instead.</summary>
    public string? FailureText => Failure?.Message;

    public bool IsPending => State == SuggestState.Pending;

    /// <summary>Raised synchronously after every state change.</summary>
    public event Action? Changed;

    /// <summary>The keystroke edge (undebounced). A blank query clears; the same trimmed query (a trailing space)
    /// keeps the current generation and state, so an in-flight request stays valid; any other text starts a new
    /// generation and enters <see cref="SuggestState.Pending"/> NOW, before the debounced fetch fires. Returns the
    /// generation the eventual answer must carry.</summary>
    public int Begin(string query)
    {
        string q = query.Trim();
        if (q.Length == 0) { Clear(); return _generation; }
        if (q == Query && State != SuggestState.Idle) return _generation;
        _generation++;
        Query = q;
        State = SuggestState.Pending;
        Failure = null;
        Changed?.Invoke();
        return _generation;
    }

    /// <summary>The retry affordance: re-arms the failed query as a new pending generation. A no-op (returns the
    /// current generation) in every other state — there is nothing to retry.</summary>
    public int Retry()
    {
        if (State != SuggestState.Failed) return _generation;
        _generation++;
        State = SuggestState.Pending;
        Failure = null;
        Changed?.Invoke();
        return _generation;
    }

    /// <summary>Publishes an answer for <paramref name="generation"/>. Returns false (and changes nothing) when that
    /// generation has been superseded by a later keystroke, retry or clear.</summary>
    public bool Complete(int generation, SearchSuggestions suggestions)
    {
        if (generation != _generation) return false;
        Suggestions = suggestions;
        State = suggestions.Queries.Count == 0 && suggestions.Items.Count == 0 ? SuggestState.Empty : SuggestState.Results;
        Failure = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Publishes a failure for <paramref name="generation"/>. Returns false for a superseded generation, and
    /// for a cancellation of the CURRENT one: cancellation is not an answer — whoever cancelled either moved the
    /// generation on already or is tearing the field down, and a torn-down field re-issues a still-pending
    /// generation when it re-mounts.</summary>
    public bool Fail(int generation, Exception failure)
    {
        if (generation != _generation || failure is OperationCanceledException) return false;
        Suggestions = SearchSuggestions.Empty;   // rows for the previous query would claim an answer the server never gave
        State = SuggestState.Failed;
        Failure = failure;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Back to <see cref="SuggestState.Idle"/> (NOT <see cref="SuggestState.Empty"/>: a blank field has no
    /// answer to report). Advances the generation so a late answer for the cleared query is dropped.</summary>
    public void Clear()
    {
        if (State == SuggestState.Idle) return;
        _generation++;
        Query = "";
        State = SuggestState.Idle;
        Suggestions = SearchSuggestions.Empty;
        Failure = null;
        Changed?.Invoke();
    }
}
