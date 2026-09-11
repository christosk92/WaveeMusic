using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

// The startup activation schedule (App/StartupActivation.cs) — the pure ordering/gating decision behind Wavee's first
// frame, and the pre-activation command gate beside it. Mirrors SetupGatingTests / ShutdownUpdatePolicyTests in shape:
// these drive the REAL production types (source-included), never a copy.
//
// WHY THESE EXIST. Every launch's first completed UI frame measured 106-143 ms, essentially all of it after submit in
// the engine's passive-effect drain, because one mount effect activated everything inline and that chain reached WinRT,
// shell COM, the registry and DPAPI on the UI thread. The fix is a phase split, and a phase split is exactly the kind of
// thing that silently rots: a step re-tagged Core, a worker hand-off accidentally made from inside a poster callback, a
// command gate opened at the wrong end of the schedule. None of that shows up in a screenshot, and only one of them
// shows up in a stopwatch. So the invariants are pinned here.
public class StartupActivationTests
{
    // ── the fakes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stand-in for the engine's UI poster. Records how deeply nested it currently is, so a step's body can
    /// be asked "did you run inside a dispatch callback?". Queueing by default — which is what the real host does: it
    /// drains at the top of the NEXT frame, and its drain snapshots the queue length first, so an action that re-posts
    /// lands in a later drain.</summary>
    sealed class FakeDispatcher
    {
        readonly Queue<Action> _queue = new();

        /// <summary>Nesting depth of dispatch callbacks currently on the stack. 0 = not inside one.</summary>
        public int Depth { get; private set; }

        public int Posted { get; private set; }

        /// <summary>Run callbacks the instant they are posted (the pathological case, used to prove that even then the
        /// worker phase is never handed out from inside a callback).</summary>
        public bool Inline { get; init; }

        public void Post(Action action)
        {
            Posted++;
            if (Inline) { Invoke(action); return; }
            _queue.Enqueue(action);
        }

        /// <summary>Drain exactly ONE queued callback — one host drain. Returns false when the queue is empty.
        /// Deliberately one-at-a-time: "the window ladder pays for one OS surface per drain" is a claim about this.</summary>
        public bool DrainOne()
        {
            if (_queue.Count == 0) return false;
            Invoke(_queue.Dequeue());
            return true;
        }

        public int Pending => _queue.Count;

        void Invoke(Action action)
        {
            Depth++;
            try { action(); }
            finally { Depth--; }
        }
    }

    /// <summary>A stand-in for <c>Task.Run</c> that does NOT run the closure — the schedule hands work over and the test
    /// decides when (or whether) it executes. Records the dispatcher depth at the hand-off, because "the worker phase is
    /// scheduled from the mount call, not from inside a poster callback" is the property that keeps the OS work off both
    /// the frame and the drain.</summary>
    sealed class FakeWorker
    {
        readonly FakeDispatcher _dispatcher;
        Action? _work;

        public FakeWorker(FakeDispatcher dispatcher) => _dispatcher = dispatcher;

        public int HandOffs { get; private set; }
        public int DepthAtHandOff { get; private set; } = -1;

        public void Schedule(Action work)
        {
            HandOffs++;
            DepthAtHandOff = _dispatcher.Depth;
            _work = work;
        }

        /// <summary>Run the handed-over closure, as the pool eventually would.</summary>
        public bool Run()
        {
            if (_work is not { } work) return false;
            _work = null;
            work();
            return true;
        }
    }

    /// <summary>What actually happened: every step body, in execution order, with the dispatcher depth it ran at.</summary>
    sealed class Trace
    {
        readonly FakeDispatcher _dispatcher;
        public Trace(FakeDispatcher dispatcher) => _dispatcher = dispatcher;

        public List<string> Order { get; } = [];
        public Dictionary<string, int> DepthOf { get; } = new();

        public Action Step(string name) => () =>
        {
            Order.Add(name);
            DepthOf[name] = _dispatcher.Depth;
        };
    }

    static IReadOnlyList<StartupStep> Schedule(Trace trace) =>
    [
        new StartupStep("core-a", StartupAffinity.Core, trace.Step("core-a")),
        new StartupStep("window-a", StartupAffinity.Window, trace.Step("window-a")),
        new StartupStep("worker-a", StartupAffinity.Worker, trace.Step("worker-a")),
        new StartupStep("core-b", StartupAffinity.Core, trace.Step("core-b")),
        new StartupStep("window-b", StartupAffinity.Window, trace.Step("window-b")),
        new StartupStep("worker-b", StartupAffinity.Worker, trace.Step("worker-b")),
    ];

    // ── the frame-budget invariant ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE assertion this whole design exists for: the mount call runs the core steps and NOTHING else. No OS
    /// surface, no registry write, no credential probe — nothing that used to make the first frame 106-143 ms — happens
    /// on the call stack that the engine's passive-effect drain is sitting on.</summary>
    [Fact]
    public void TheMountCall_RunsCoreStepsAndNothingElse()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule);

        Assert.True(activation.Run(Schedule(trace)));

        Assert.Equal(new[] { "core-a", "core-b" }, trace.Order);
        Assert.Equal(1, worker.HandOffs);      // handed over, not run
        Assert.Equal(1, dispatcher.Posted);    // the window ladder was armed with ONE post, not six
    }

    /// <summary>Core steps run inline on the mount thread, in schedule order — not through the poster. Posting them
    /// would not help: the host drains at the top of the next frame, so it moves the stall rather than removing it.</summary>
    [Fact]
    public void CoreSteps_RunInline_NeverThroughThePoster()
    {
        var dispatcher = new FakeDispatcher();
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, new FakeWorker(dispatcher).Schedule);

        activation.Run(Schedule(trace));

        Assert.Equal(0, trace.DepthOf["core-a"]);
        Assert.Equal(0, trace.DepthOf["core-b"]);
    }

    // ── "no activation work inside a dispatch callback" ───────────────────────────────────────────────────────────────

    /// <summary>The worker phase is never handed out from inside a poster callback, and its steps never execute inside
    /// one — even with a dispatcher pathological enough to run every callback the instant it is posted.
    ///
    /// <para>This is the regression that would otherwise be invisible: moving the OS work "off the frame" by posting it
    /// and then calling <c>Task.Run</c> from inside that callback looks identical in a diff and is exactly as bad,
    /// because the host's drain is synchronous UI-thread time either way.</para></summary>
    [Fact]
    public void WorkerPhase_IsNeverScheduledOrRun_InsideADispatchCallback()
    {
        var dispatcher = new FakeDispatcher { Inline = true };
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule);

        activation.Run(Schedule(trace));
        Assert.True(worker.Run());

        Assert.Equal(0, worker.DepthAtHandOff);          // handed over from the mount call, not from a callback
        Assert.Equal(0, trace.DepthOf["worker-a"]);      // and the bodies ran outside any callback too
        Assert.Equal(0, trace.DepthOf["worker-b"]);
    }

    // ── the window ladder ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Window steps are HWND/apartment-affine, so they stay on the UI thread — but one per drain. A ladder that
    /// collapsed back into a single drain would restore the original stall while looking, in the log, like it had been
    /// fixed (the host's post drain runs outside the frame it times).</summary>
    [Fact]
    public void WindowSteps_RunOnePerDrain_InScheduleOrder()
    {
        var dispatcher = new FakeDispatcher();
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, new FakeWorker(dispatcher).Schedule);

        activation.Run(Schedule(trace));
        Assert.DoesNotContain("window-a", trace.Order);

        Assert.True(dispatcher.DrainOne());
        Assert.Equal(new[] { "core-a", "core-b", "window-a" }, trace.Order);

        Assert.True(dispatcher.DrainOne());
        Assert.Equal(new[] { "core-a", "core-b", "window-a", "window-b" }, trace.Order);

        Assert.False(dispatcher.DrainOne());   // the ladder ends; it does not keep re-posting forever
    }

    /// <summary>Window steps DO run inside a dispatch callback — that is what they are for. Pinned so the previous test
    /// cannot be "satisfied" by accidentally moving them onto the worker, where the engine's wrappers would bind raw COM
    /// pointers to an ephemeral pool-thread apartment.</summary>
    [Fact]
    public void WindowSteps_RunOnTheUiThread_InsideTheDrain()
    {
        var dispatcher = new FakeDispatcher();
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, new FakeWorker(dispatcher).Schedule);

        activation.Run(Schedule(trace));
        while (dispatcher.DrainOne()) { }

        Assert.Equal(1, trace.DepthOf["window-a"]);
        Assert.Equal(1, trace.DepthOf["window-b"]);
    }

    // ── exactly once ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_IsExactlyOnce()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule);

        Assert.True(activation.Run(Schedule(trace)));
        Assert.False(activation.Run(Schedule(trace)));

        Assert.Equal(new[] { "core-a", "core-b" }, trace.Order);
        Assert.Equal(1, worker.HandOffs);
        Assert.Equal(1, dispatcher.Posted);
    }

    // ── core before commands, and nothing dropped ─────────────────────────────────────────────────────────────────────

    /// <summary>A command that arrives before activation is HELD and released in arrival order the moment the core phase
    /// is done — before any worker or window step can be handed out, so nothing can observe a half-applied transport.
    /// The old code dropped these (<c>if (_post is not { } post) return;</c>) or threw out of an OS callback.</summary>
    [Fact]
    public void CommandsArrivingEarly_AreQueued_ThenReleasedInOrder_BeforeTheLaterPhases()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule);

        Assert.False(activation.Submit(trace.Step("early-1")));
        Assert.False(activation.Submit(trace.Step("early-2")));
        Assert.Equal(2, activation.QueuedCount);
        Assert.False(activation.IsReady);
        Assert.Empty(trace.Order);

        activation.Run(Schedule(trace));

        Assert.True(activation.IsReady);
        Assert.Equal(0, activation.QueuedCount);
        // Core, then the released commands in arrival order. The window/worker steps are not in this list at all.
        Assert.Equal(new[] { "core-a", "core-b", "early-1", "early-2" }, trace.Order);
    }

    [Fact]
    public void CommandsArrivingAfterTheCorePhase_RunImmediately()
    {
        var dispatcher = new FakeDispatcher();
        var trace = new Trace(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, new FakeWorker(dispatcher).Schedule);

        activation.Run(Schedule(trace));
        Assert.True(activation.Submit(trace.Step("late")));

        Assert.Equal("late", trace.Order[^1]);
        Assert.Equal(0, activation.QueuedCount);
    }

    /// <summary>The schedule and the playback bridge share ONE gate instance in production, so "core before commands"
    /// has a single owner. Opening it is idempotent from either side — the bridge's own Activate step opens it the
    /// moment the poster exists, and the schedule opens it again at the end of the core phase.</summary>
    [Fact]
    public void TheSharedGate_IsOpenedByTheSchedule_AndReleaseIsIdempotent()
    {
        var dispatcher = new FakeDispatcher();
        var trace = new Trace(dispatcher);
        var gate = new ActivationCommandQueue();
        var activation = new StartupActivation(dispatcher.Post, new FakeWorker(dispatcher).Schedule, commands: gate);

        Assert.False(gate.Submit(trace.Step("held")));
        activation.Run(Schedule(trace));

        Assert.True(gate.IsOpen);
        Assert.Contains("held", trace.Order);
        Assert.Equal(0, gate.Release());                                     // nothing left, nothing re-run
        Assert.Equal(1, trace.Order.FindAll(n => n == "held").Count);
    }

    [Fact]
    public void TheGate_RunsEveryHeldCommandExactlyOnce_EvenWhenOneThrows()
    {
        var failures = new List<Exception>();
        var gate = new ActivationCommandQueue(failures.Add);
        var ran = new List<string>();

        gate.Submit(() => ran.Add("first"));
        gate.Submit(() => throw new InvalidOperationException("boom"));
        gate.Submit(() => ran.Add("third"));

        Assert.Equal(3, gate.Release());
        // A single bad early intent must not strand the ones queued behind it.
        Assert.Equal(new[] { "first", "third" }, ran);
        Assert.Single(failures);
    }

    // ── isolation ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A step that throws is reported and skipped; the rest of its phase still runs. One OS surface the
    /// platform refuses must not cost the app the others — and it must never propagate into the frame or the pool.</summary>
    [Fact]
    public void AThrowingStep_IsReported_AndTheRestOfThePhaseStillRuns()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var failed = new List<string>();
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule,
            failed: (step, _) => failed.Add(step));

        activation.Run(
        [
            new StartupStep("core-a", StartupAffinity.Core, trace.Step("core-a")),
            new StartupStep("core-boom", StartupAffinity.Core, () => throw new InvalidOperationException("no")),
            new StartupStep("core-b", StartupAffinity.Core, trace.Step("core-b")),
            new StartupStep("worker-boom", StartupAffinity.Worker, () => throw new InvalidOperationException("no")),
            new StartupStep("worker-a", StartupAffinity.Worker, trace.Step("worker-a")),
        ]);
        worker.Run();

        Assert.Equal(new[] { "core-a", "core-b", "worker-a" }, trace.Order);
        Assert.Equal(new[] { "core-boom", "worker-boom" }, failed);
    }

    // ── honest accounting ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every step reports the phase it ACTUALLY ran in, and each non-empty phase reports once. This is what
    /// keeps the always-on <c>[playback.buckets] startup</c> lines honest: work that moved off the UI thread has to show
    /// up under Worker/Window, not simply vanish from the accounting.</summary>
    [Fact]
    public void EveryStep_ReportsThePhaseItRanIn_AndEachPhaseRollsUpOnce()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var measured = new List<(string Step, StartupAffinity Phase)>();
        var phases = new List<(StartupAffinity Phase, int Steps)>();
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule,
            measured: (step, phase, _) => measured.Add((step, phase)),
            phaseCompleted: (phase, steps, _) => phases.Add((phase, steps)));

        activation.Run(Schedule(trace));
        worker.Run();
        while (dispatcher.DrainOne()) { }

        Assert.Equal(new[]
        {
            ("core-a", StartupAffinity.Core),
            ("core-b", StartupAffinity.Core),
            ("worker-a", StartupAffinity.Worker),
            ("worker-b", StartupAffinity.Worker),
            ("window-a", StartupAffinity.Window),
            ("window-b", StartupAffinity.Window),
        }, measured);

        Assert.Contains((StartupAffinity.Core, 2), phases);
        Assert.Contains((StartupAffinity.Worker, 2), phases);
        Assert.Contains((StartupAffinity.Window, 2), phases);
        Assert.Equal(3, phases.Count);
    }

    /// <summary>A schedule with no worker steps must not hand an empty closure to the pool, and one with no window steps
    /// must not arm an empty ladder. A launch that posts for nothing is a wake-up for nothing.</summary>
    [Fact]
    public void EmptyPhases_CostNothing()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule);

        activation.Run([new StartupStep("core-only", StartupAffinity.Core, () => { })]);

        Assert.Equal(0, worker.HandOffs);
        Assert.Equal(0, dispatcher.Posted);
    }

    // ── the gated Window ladder (first-content-reveal + fallback deadline) ───────────────────────────────────────────
    //
    // WHY THIS EXISTS. A native tour with the ladder starting the instant Run collected it still showed 124.8 ms of
    // UI-thread time (toast-register, smtc, taskbar, power-os, jumplist) landing in the first ~150 ms after the first
    // route, interleaved with that route's own first content frames and stretching the startup reveal past its 100 ms
    // target — even though none of those steps are charged to any frame, because none of SMTC/taskbar/jump-list is
    // needed until the user actually reaches for it. So the ladder now waits for an explicit `StartWindowLadder()`
    // call instead of starting itself, UNLESS the constructor's `scheduleFallback` is left null — in which case
    // nothing below applies and the ladder starts exactly as it always did (every test above this section proves
    // that byte-for-byte, since none of them pass `scheduleFallback`).

    /// <summary>A stand-in for the app's fallback-deadline timer: records the (callback, delayMs) StartupActivation
    /// handed it, WITHOUT running it — the test decides when (or whether) the deadline "elapses".</summary>
    sealed class FakeFallback
    {
        public Action? Callback { get; private set; }
        public double DelayMs { get; private set; } = double.NaN;
        public int ScheduleCalls { get; private set; }

        public void Schedule(Action callback, double delayMs)
        {
            ScheduleCalls++;
            Callback = callback;
            DelayMs = delayMs;
        }

        /// <summary>Simulate the deadline actually elapsing.</summary>
        public void Elapse() => Callback?.Invoke();
    }

    [Fact]
    public void WindowLadder_DoesNotStart_UntilStartWindowLadderIsCalled()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var fallback = new FakeFallback();
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule, scheduleFallback: fallback.Schedule);

        activation.Run(Schedule(trace));

        // Core ran, the worker was handed off, but NOTHING was posted for the window ladder — Run only handed the
        // fallback deadline to `scheduleFallback`, it did not post RunNextWindowStep the way the ungated tests above
        // prove happens when scheduleFallback is null.
        Assert.Equal(new[] { "core-a", "core-b" }, trace.Order);
        Assert.Equal(0, dispatcher.Posted);
        Assert.Equal(1, fallback.ScheduleCalls);
        Assert.DoesNotContain("window-a", trace.Order);

        // The reveal signal arrives: the caller invokes StartWindowLadder, and only THEN does the ladder post its
        // first drain.
        activation.StartWindowLadder();
        Assert.Equal(1, dispatcher.Posted);
        Assert.True(dispatcher.DrainOne());
        Assert.Contains("window-a", trace.Order);
    }

    [Fact]
    public void WindowLadder_StartsAtTheFallbackDeadline_WithoutAReveal()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var fallback = new FakeFallback();
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule,
            scheduleFallback: fallback.Schedule, windowLadderFallbackMs: 2000);

        activation.Run(Schedule(trace));
        Assert.Equal(2000, fallback.DelayMs);   // the deadline this test's app-level caller asked for was actually handed through
        Assert.Equal(0, dispatcher.Posted);     // nobody called StartWindowLadder — no reveal happened in this test

        // The deadline elapses with no reveal ever having fired. The ladder must still start.
        fallback.Elapse();
        Assert.Equal(1, dispatcher.Posted);
        while (dispatcher.DrainOne()) { }
        Assert.Equal(new[] { "core-a", "core-b", "window-a", "window-b" }, trace.Order);
    }

    /// <summary>The reveal signal and the fallback deadline are a RACE — whichever calls <c>StartWindowLadder</c>
    /// first wins, and the loser must cost nothing (no second drain of the ladder, no re-run of any step).</summary>
    [Fact]
    public void StartWindowLadder_IsIdempotent_TheLoserOfTheRaceIsANoOp()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var trace = new Trace(dispatcher);
        var fallback = new FakeFallback();
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule, scheduleFallback: fallback.Schedule);

        activation.Run(Schedule(trace));

        // Reveal wins the race...
        activation.StartWindowLadder();
        Assert.Equal(1, dispatcher.Posted);
        while (dispatcher.DrainOne()) { }
        Assert.Equal(new[] { "core-a", "core-b", "window-a", "window-b" }, trace.Order);

        // ...and the fallback deadline losing it later is a complete no-op: no extra post, no repeated step.
        fallback.Elapse();
        Assert.Equal(1, dispatcher.Posted);
        Assert.Equal(new[] { "core-a", "core-b", "window-a", "window-b" }, trace.Order);
    }

    /// <summary>A schedule with no Window steps at all: <c>scheduleFallback</c> is never even consulted, and a later
    /// <c>StartWindowLadder</c> call (the reveal firing anyway, or the fallback timer) is a harmless no-op.</summary>
    [Fact]
    public void WindowLadder_WithNoWindowSteps_NeverSchedulesTheFallback_AndStartIsANoOp()
    {
        var dispatcher = new FakeDispatcher();
        var worker = new FakeWorker(dispatcher);
        var fallback = new FakeFallback();
        var activation = new StartupActivation(dispatcher.Post, worker.Schedule, scheduleFallback: fallback.Schedule);

        activation.Run([new StartupStep("core-only", StartupAffinity.Core, () => { })]);

        Assert.Equal(0, fallback.ScheduleCalls);
        activation.StartWindowLadder();
        Assert.Equal(0, dispatcher.Posted);
    }
}
