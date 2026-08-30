using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The hub feed's tail: infinite scroll with three gates instead of an eager chain. The next page is fetched
/// only when (C) the tail is NEAR the viewport (the page feeds <see cref="NearTail"/> from its scroll geometry — far
/// away this renders a quiet spacer and the shimmer isn't even mounted, so no standing pulse), (B) after a short
/// arm-debounce so a fast fling through the threshold doesn't fire, and (A) never concurrently (<see cref="Loading"/>
/// is the single-in-flight guard). Each appended page remounts this under the NEW pagination key (the caller keys it
/// "concert-append:{key}") and the page drops <see cref="NearTail"/> after every append, so the chain only continues
/// when a real scroll event re-confirms the user is still heading down. A failed append re-arms a bounded number of
/// quiet retries, then the tail collapses instead of spinning forever.</summary>
sealed class ConcertAppendPreloader : Component
{
    const int MaxAttempts = 3;
    const int ArmDelayMs = 300;

    public required string PaginationKey;
    /// <summary>The hub's shared append-in-flight signal: flips true when <see cref="Start"/> takes, and back to false
    /// on completion or quiet failure — the false edge is what re-arms a retry here.</summary>
    public required Signal<bool> Loading;
    /// <summary>True while the scroller's bottom edge is within ~1.5 viewport heights of the content end. Page-owned;
    /// dropped to false after each successful append so only a fresh scroll event continues the chain.</summary>
    public required IReadSignal<bool> NearTail;
    public required Action Start;

    // Never leaves Pending: the shimmer is this component's whole life — success replaces it via the parent's remount.
    readonly Loadable<bool> _shimmer = Loadable<bool>.Pending(false);
    CancellationTokenSource? _arm;
    int _attempts;

    public override Element Render()
    {
        var post = UsePost();
        bool near = NearTail.Value;     // subscribe → mount/unmount the shimmer + gate the fetch
        bool loading = Loading.Value;   // subscribe → each failure edge (true→false) re-renders and re-arms below

        UseSignalEffect(() => Reactive.OnCleanup(() => { _arm?.Cancel(); _arm?.Dispose(); }));
        UseSignalEffect(() =>
        {
            bool n = NearTail.Value;
            bool l = Loading.Value;
            if (!n || l || _attempts >= MaxAttempts) { _arm?.Cancel(); return; }
            Arm(post);
        });

        if (!loading && _attempts >= MaxAttempts) return new BoxEl();   // repeated quiet failures: collapse the tail
        // Far from the tail: a quiet spacer — no shimmer subtree, no standing skeleton pulse, nothing to animate.
        if (!near && !loading) return new BoxEl { Height = Spacing.L };

        return Skel.Region(_shimmer,
            shimmerSource: ShimmerGrid,
            content: static _ => new BoxEl(),
            group: "concert-hub-append");
    }

    /// <summary>(B) the arm-debounce: cancel-and-restart on every gate re-evaluation, fire only if the gates still
    /// hold after the delay (checked on the UI thread via post).</summary>
    void Arm(Action<Action> post)
    {
        _arm?.Cancel();
        _arm?.Dispose();
        var cts = _arm = new CancellationTokenSource();
        _ = DelayedStart(cts, post);
    }

    async Task DelayedStart(CancellationTokenSource cts, Action<Action> post)
    {
        try { await Task.Delay(ArmDelayMs, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        post(() =>
        {
            if (cts.IsCancellationRequested || Loading.Peek() || !NearTail.Peek() || _attempts >= MaxAttempts) return;
            _attempts++;
            Start();
        });
    }

    // The same card family + grid metrics as the All Events section this trails, so the shimmer reads as "more of the
    // grid arriving" (the engine derives the shimmer bars from these shapes; none of the placeholder text is shown).
    static Element ShimmerGrid()
    {
        var start = new DateTimeOffset(2025, 1, 1, 20, 0, 0, TimeSpan.Zero);
        var cards = new Element[4];
        for (int i = 0; i < cards.Length; i++)
        {
            var seed = new Concert("seed:append:" + i, "Concert placeholder", "Venue placeholder", "City placeholder",
                start.AddDays(7 * i));
            cards[i] = ConcertUi.VerticalCard(seed, static () => { });
        }
        return AutoGrid(240f, Spacing.M, float.NaN, cards);
    }
}
