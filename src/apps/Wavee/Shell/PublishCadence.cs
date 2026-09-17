// ── Shell/PublishCadence.cs ────────────────────────────────────────────────────────────────────────────────────────
// when a dirty table set is allowed to publish while a scroll is live (W2-A1)
//
// Role: CORE (pure — no engine type, no clock, no static state)
// Owner: I
// Spec: docs/plans/wavee/scroll-feel-and-recording-defects-2026-09-16-implementation.md, Wave 2 cause A
//
// THE FAN-OUT. `Entities.Publish()` bumps the `Changed` counter of every table that changed in the drain, and every
// component that reads such a counter re-renders on the next frame: `TableRowContent` (Tracks/Albums/Artists/
// TrackArtists per row), `SectionsHost`, `StackHost`, `ReleasePanelHost`, `FacePileHost`, `PageHost`, `LikedArt`,
// `LazyGrid` through `DiscoGridHost.Count` — and through THEIR re-render, every child whose props record compares a
// delegate or an `Element` by reference (`NowPlayingOverlayHost`, `ShelfCardHost`, `ToolTip`). In `--fake` nothing is
// dirty during a scroll, so nothing publishes. In real mode covers stream in from the CDN, kind-179 gradings land,
// hydration answers post: SOMETHING is dirty on almost every frame, so the shell published on almost every frame, and
// the whole subtree above re-rendered on every scroll frame — 13 rows × ~12 `with {}` clones of a 123-member `BoxEl`
// plus three string formats plus closures, 0.5–1 MB per frame, gen2 collections inside a fling, missed vblanks.
//
// THE RULE. The DATA is not delayed — the tables commit the moment the answer lands — only the SIGNAL is. While a
// scroll is live, a publication goes out at most every fourth rendered frame or after 50 ms, whichever comes first;
// with no scroll live, every frame publishes exactly as before. The frame handler and the posted-answer path
// (`Shell.Host.cs`) both ask this class, so a fling does not fan out per posted answer either.

namespace Wavee;

/// <summary>The engine-free decision behind <c>Shell.Host.cs</c>'s two publish sites and the palette pump's self
/// re-arm: how often a dirty table set may fire its <c>Changed</c> signals while the user is scrolling.</summary>
public static class PublishCadence
{
    /// <summary>While a scroll is live, publish on every Nth rendered frame. FOUR: at 120 Hz that is ~33 ms, so a row
    /// realized during a fling still receives its data within two refreshes of a 60 Hz feel — later than that and a
    /// skeleton row becomes visible as a skeleton — while the re-render rate of every <c>Changed</c> subscriber drops
    /// 4× (13 track rows, the section hosts, the tooltips: see the file header). At 60 Hz the same four frames are
    /// ~67 ms and <see cref="ScrollMaxLagMs"/> takes over first.</summary>
    public const int ScrollEveryFrames = 4;

    /// <summary>The wall-clock bound on the frame rule: a publication is never held longer than this while scrolling,
    /// whatever the refresh rate. Fifty milliseconds is three 60 Hz frames — below the threshold at which a landing
    /// cover or title reads as "late" rather than "streaming in" — and it is also the wake the shell arms when a
    /// deferral could otherwise strand (no further frame renders inside the engine's post-scroll hold).</summary>
    public const float ScrollMaxLagMs = 50f;

    /// <summary>May the dirty set publish now? Always when no scroll is live (the pre-W2 behaviour, unchanged);
    /// during a scroll only when <paramref name="framesSinceLast"/> has reached <see cref="ScrollEveryFrames"/> or
    /// <paramref name="msSinceLast"/> has reached <see cref="ScrollMaxLagMs"/>.
    /// <para>Both counters are measured from the last publish OPPORTUNITY TAKEN, dirty or not: a no-op publish (the
    /// dirty set was empty) still resets them, which is what makes the cadence a steady beat rather than a burst the
    /// moment something becomes dirty.</para></summary>
    public static bool ShouldPublish(bool scrollActive, int framesSinceLast, float msSinceLast)
        => !scrollActive || framesSinceLast >= ScrollEveryFrames || msSinceLast >= ScrollMaxLagMs;

    /// <summary>May the cover-palette pump re-arm itself after a batch lands? Not while a scroll is live: a grid fling
    /// misses dozens of covers per frame and every landed batch would otherwise queue the next one AND publish, so the
    /// pump was itself a per-frame publisher. The pending rows stay queued and the shell re-arms the pump on the
    /// scroll-end edge (<c>Palette.ResumeIfPending</c>); a batch already in flight still lands, its publish riding the
    /// cadence above.</summary>
    public static bool PalettePumpAllowed(bool scrollActive) => !scrollActive;
}
