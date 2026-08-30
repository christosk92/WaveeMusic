using System.Collections;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The parking bay for a detail model that arrived MID-GESTURE. A live in-place refresh (a dealer push, our own ack,
/// a background revalidate) normally lands straight on the page's <see cref="Loadable{T}"/>; while a same-list drag is
/// aiming at those very rows, publishing it re-keys the list under the pointer and the drop lands somewhere the user
/// did not aim. So the model is held here and applied on SESSION END.
/// <para>Keyed by the loadable INSTANCE (reference identity), so two panes each showing their own playlist hold their
/// own deferral and neither can flush the other's.</para>
/// </summary>
static class PlaylistReorderDefer
{
    // Touched only from the UI thread (the bridge's `post` marshal and a render effect), so no lock: a Dictionary here
    // is the same threading contract every other UI-thread-owned map in the app has.
    static readonly Dictionary<object, DetailModel> s_pending = new(ReferenceEqualityComparer.Instance);

    /// <summary>Hold <paramref name="model"/> for <paramref name="target"/> instead of publishing it — but ONLY while a
    /// same-list reorder of <paramref name="playlistUri"/> is genuinely live. Returns false when there is nothing to
    /// defer to, and the caller must publish immediately.
    /// <para>The liveness test lives HERE, in the same statement as the write, rather than at the call site: the
    /// deferral is released by a drag-epoch edge, so a model parked after the LAST such edge would wait for the next
    /// drag to end — an unbounded delay on the drop's own result. Checking and holding atomically on the UI thread
    /// makes that window structurally impossible.</para>
    /// <para>Last writer wins: a burst of pushes during one gesture converges to the newest snapshot, which is the only
    /// one worth applying.</para></summary>
    public static bool TryHold(Loadable<DetailModel> target, DetailModel model, string? playlistUri)
    {
        if (!WaveeResourceDrag.LiveSameListReorder(playlistUri)) return false;
        s_pending[target] = model;
        return true;
    }

    /// <summary>Publish whatever was held for <paramref name="target"/> (no-op when nothing was). Called when the drag
    /// session ends — drop, cancel and Escape alike, so there is no path that strands a deferred model.</summary>
    public static void Flush(Loadable<DetailModel> target)
    {
        if (!s_pending.Remove(target, out var pending)) return;
        target.SetReady(pending);
    }

    /// <summary>Drop a held model without publishing it — the page is going away (unmount / route swap), so the model
    /// belongs to a list nobody is looking at any more.</summary>
    public static void Discard(Loadable<DetailModel> target) => s_pending.Remove(target);
}

/// <summary>Owns the ONE <c>UseDragState()</c> subscription that flushes a deferred detail model. Renders nothing.
/// <para>The <c>SidebarDragPeekWatcher</c> pattern: a zero-size component is the cheapest place to hold a drag
/// subscription, because the epoch is edge-triggered and re-rendering a 0×0 box costs nothing — whereas subscribing the
/// PAGE to it would re-render the whole detail surface on every target/caption edge of every drag in the app.</para></summary>
sealed class PlaylistReorderDeferWatcher(Loadable<DetailModel> model) : Component
{
    public override Element Render()
    {
        // Active stays true across the ~250ms Stationary settle window, so the model lands after the chip has finished
        // animating home rather than under it. The publish happens in a LAYOUT EFFECT, not in the render body: SetReady
        // writes a signal, and a signal written during render is the one thing the reactive core will not tolerate.
        bool active = UseDragState().Active;
        UseLayoutEffect(() => { if (!active) PlaylistReorderDefer.Flush(model); }, active ? 1 : 0);
        UseEffect(() => (System.Action?)(() => PlaylistReorderDefer.Discard(model)), DepKey.Empty);
        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
