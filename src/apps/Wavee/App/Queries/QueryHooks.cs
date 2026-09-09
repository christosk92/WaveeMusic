using System;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Presentation state for one typed query; the handle owns data, this view owns only its lifetime.</summary>
sealed class QueryPresentation<T>(T seed)
{
    public readonly Loadable<T> Loadable = Loadable<T>.Pending(seed);
    public readonly Signal<IQuerySignalBinding?> Binding = new(null);
    public CatalogScope? Scope;
    public object? Specification;
    public readonly Signal<bool> Refreshing = new(false);
    public readonly Signal<System.Collections.Generic.IReadOnlyList<FacetProblem>> Problems = new([]);
    public readonly Signal<Exception?> Failure = new(null);
    public bool IsRefreshing => Refreshing.Value;
}

static class QueryHooks
{
    // As with a hook in a loop, repeated calls in one component must keep their order. A null specification is a
    // deliberate empty pane, so it still takes the same hook positions and acquires no handle.
    /// <param name="initialLoad">The page's own initial-load boundary. Until it holds, a publication leaves the
    /// pending shape (and the whole-page skeleton) untouched; once the loadable is Ready every later publication
    /// lands as ordinary background data. Null means the first read reveals.</param>
    public static QueryPresentation<T> Use<T>(RenderContext context, Action<Loadable<T>, T> publish,
        IQueryService? queries, QuerySpec<T>? specification, T seed, QueryDemand? demand = null, bool keepPrevious = false,
        Func<QuerySnapshot<T>, bool>? initialLoad = null)
        => UseMapped(context, publish, queries, specification, static value => value, seed, demand, keepPrevious, initialLoad);

    public static QueryPresentation<TView> UseMapped<T, TView>(RenderContext context, Action<Loadable<TView>, TView> publish,
        IQueryService? queries, QuerySpec<T>? specification, Func<T, TView> project, TView seed,
        QueryDemand? demand = null,
        bool keepPrevious = false,
        Func<QuerySnapshot<T>, bool>? initialLoad = null)
    {
        var state = context.UseMemo(() => new QueryPresentation<TView>(seed), DepKey.Empty);
        var post = context.UsePost();
        var active = context.UseIsActive();
        ArgumentNullException.ThrowIfNull(publish);
        context.UseEffect(() =>
        {
            if (specification is null || queries is null) { state.Loadable.SetReady(seed); return (Action?)null; }
            if (!keepPrevious || !state.Loadable.IsReady || state.Scope != specification.Scope) state.Loadable.SetPending(seed);
            state.Scope = specification.Scope; state.Specification = specification;
            // `project` — a caller-supplied pure mapping, sometimes a whole-page one (LibraryPage's album/show detail
            // panes pass DetailPage.MapAlbum/MapShow) — runs on the binding's serialized pool worker, never in
            // acquisition, the observer callback or the UI post.
            var binding = new QuerySignalBinding<T, TView>(queries.Acquire(specification), post, project, (snapshot, view) =>
            {
                if (snapshot.Revision == 0) return; // acquisition seed, not a published query result
                state.Refreshing.Value = snapshot.Status.IsRefreshing;
                state.Problems.Value = snapshot.Problems;
                state.Failure.Value = snapshot.Failure is { } queryFailure ? new InvalidOperationException(queryFailure.Message) : null;
                if (state.Failure.Peek() is { } error)
                {
                    if (!state.Loadable.IsReady) state.Loadable.SetFailed(error);
                    return;
                }
                // The page author owns Pending/Ready/Failed. Resource completeness is diagnostic data only; a page that
                // names its own initial-load boundary keeps the skeleton until that boundary holds, and reveals once.
                if (initialLoad is not null && !state.Loadable.IsReady && !initialLoad(snapshot)) return;
                publish(state.Loadable, view);
            }, failed: error =>
            {
                state.Refreshing.Value = false; state.Failure.Value = error;
                if (!state.Loadable.IsReady) state.Loadable.SetFailed(error);
            });
            state.Binding.Value = binding;
            binding.SetDemand(demand ?? QueryDemand.Initial);
            binding.SetActive(active.Peek());
            return (Action?)(() => { binding.Dispose(); state.Binding.Value = null; });
        }, DepKey.From(specification?.GetHashCode() ?? 0));
        context.UseActivation(onActivated: () => state.Binding.Peek()?.SetActive(true),
            onDeactivated: () => state.Binding.Peek()?.SetActive(false));
        return state;
    }
}
