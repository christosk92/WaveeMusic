using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Wavee;

/// <summary>Where one startup-activation step is allowed to run.
/// <list type="bullet">
/// <item><see cref="Core"/> — the UI thread, INSIDE the mount effect. This is the only phase a frame pays for, so
/// nothing in it may touch the disk, the registry, COM, WinRT or a credential store: it exists to publish the UI-thread
/// poster and subscribe the Core→Signal streams, which is what makes the transport accept commands at all.</item>
/// <item><see cref="Worker"/> — a thread-pool thread. No window affinity, no signal writes; anything a worker step
/// learns comes back through the host poster like every other off-thread result.</item>
/// <item><see cref="Window"/> — the UI thread that owns the HWND, but NOT inside the mount frame. The OS media
/// surfaces (SMTC, the taskbar thumb bar) are documented as affine to that thread, so they cannot go on a worker;
/// they are instead handed out ONE PER POSTED CALLBACK, so no single drain pays for more than one of them.</item>
/// </list></summary>
public enum StartupAffinity
{
    Core = 0,
    Worker = 1,
    Window = 2,
}

/// <summary>One step of the startup activation schedule: a stable name for the always-on accounting, the thread it is
/// allowed to run on, and the work itself.</summary>
public readonly record struct StartupStep(string Name, StartupAffinity Affinity, Action Body);

/// <summary>
/// §(startup) — the pre-activation command gate: hold what arrives too early, release it in arrival order, exactly once.
///
/// <para><b>Why this exists.</b> A cold launch can be handed a transport intent before the UI has mounted: a Jump List
/// task or a toast argument (<c>wavee://pause</c>) lands in <c>DeepLinkChannel</c> from <c>Program</c>, and the audio
/// host can report an output-device notice while the window is still being created. Before this type, every one of
/// those paths ended in <c>if (_post is not { } post) return;</c> — a silent drop — or in an
/// <c>InvalidOperationException("Activate playback before seeking.")</c>. Neither is acceptable now that activation is
/// deliberately split: the fix that moves work off the first frame must not also widen the window in which an intent
/// disappears.</para>
///
/// <para><b>Contract.</b> Closed until <see cref="Release"/>: <see cref="Submit"/> queues and returns false. Open
/// afterwards: <see cref="Submit"/> runs the action on the caller's thread and returns true. <see cref="Release"/> is
/// idempotent — the second call opens nothing new and runs nothing again. Nothing is ever dropped and nothing ever runs
/// twice. Safe from any thread; the gate flips under the lock BEFORE the drain begins, so a <see cref="Submit"/> racing
/// in from an OS callback either finds the gate open and runs itself or lands in the queue the drain is still walking.</para>
///
/// <para>ENGINE-FREE (System + System.Collections.Generic), source-included by <c>Wavee.Tests</c>.</para>
/// </summary>
public sealed class ActivationCommandQueue
{
    readonly object _gate = new();
    readonly Queue<Action> _pending = new();
    readonly Action<Exception>? _failed;
    bool _open;

    /// <param name="failed">A released command threw. It must not abort the rest of the drain — one bad early intent
    /// cannot be allowed to strand the others behind it.</param>
    public ActivationCommandQueue(Action<Exception>? failed = null) => _failed = failed;

    /// <summary>Are commands being executed straight through? False until <see cref="Release"/>.</summary>
    public bool IsOpen { get { lock (_gate) return _open; } }

    /// <summary>Commands held because they arrived before activation. Zero once <see cref="Release"/> has run.</summary>
    public int QueuedCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Run <paramref name="command"/> now if the gate is open, otherwise hold it in arrival order. Returns
    /// true when it ran synchronously. Safe from any thread.</summary>
    public bool Submit(Action command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (!_open)
            {
                _pending.Enqueue(command);
                return false;
            }
        }
        command();
        return true;
    }

    /// <summary>Open the gate and run everything held, in arrival order. Returns how many were released (0 on the
    /// second and later calls). Call on the thread the commands expect — for playback that is the UI thread, from
    /// inside <c>PlaybackBridge.Activate</c>, the moment the poster exists.</summary>
    public int Release()
    {
        lock (_gate) _open = true;
        int released = 0;
        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_pending.Count == 0) return released;
                next = _pending.Dequeue();
            }
            released++;
            try { next(); }
            catch (Exception ex) { _failed?.Invoke(ex); }
        }
    }
}

/// <summary>
/// §(startup) — THE ordering/gating decision behind Wavee's first-frame activation, extracted so it can be pinned by a
/// unit test instead of by a stopwatch on a native tour.
///
/// <para><b>The problem it exists for.</b> Every launch's first completed UI frame measured 106-143 ms, essentially all
/// of it after submit, inside the engine's passive-effect drain: the app root's one mount effect activated the playback
/// bridge and every sibling bridge synchronously, and that chain reached WinRT (SMTC), shell COM (the taskbar thumb bar,
/// the Jump List's custom destination list), the registry (the toast activator) and DPAPI (the stored-credential probe)
/// on the UI thread, in a frame. The engine's poster is not an escape hatch: it drains at the top of the NEXT frame, so
/// posting the same work merely moves the stall. The only causal fix is to run the work off the UI thread, and to hand
/// the handful of genuinely window-affine calls out one at a time.</para>
///
/// <para><b>The three invariants this class owns.</b>
/// <list type="number">
/// <item>EXACTLY ONCE. <see cref="Run"/> latches. A second call (a remount, a re-entrant effect, two racing callers)
/// runs nothing and returns false.</item>
/// <item>CORE BEFORE COMMANDS. Every <see cref="StartupAffinity.Core"/> step completes, in schedule order, before
/// <see cref="IsReady"/> turns true — and therefore before any queued command is released. The <see cref="StartupAffinity.Worker"/>
/// and <see cref="StartupAffinity.Window"/> phases are started only after that, so nothing they do can be observed
/// half-applied by a command.</item>
/// <item>QUEUE, NEVER DROP. A command that arrives before the core phase is held in arrival order by
/// <see cref="Submit"/> and released in that same order the moment the core phase finishes. Nothing is dropped and
/// nothing runs twice.</item>
/// </list></para>
///
/// <para><b>ENGINE-FREE BY CONSTRUCTION</b> (System + System.Collections.Generic + Stopwatch). Load-bearing exactly like
/// <c>SetupGating</c>: this file is source-included by <c>Wavee.Tests</c>, which has no FluentGpu.Engine reference, so
/// <c>StartupActivationTests</c> drives the REAL sequencer rather than a copy of it — including the assertion that a
/// worker step never executes inside a poster callback, and the one that actually matters for the frame budget: with a
/// worker that does not run its closure inline, the mount call performs NO worker work at all.</para>
/// </summary>
public sealed class StartupActivation
{
    readonly Action<Action> _post;
    readonly Action<Action> _worker;
    readonly Action<string, StartupAffinity, double>? _measured;
    readonly Action<StartupAffinity, int, double>? _phaseCompleted;
    readonly Action<string, Exception>? _failed;

    readonly ActivationCommandQueue _commands;
    readonly object _gate = new();
    bool _started;

    // The Window ladder's remaining steps, walked one per posted callback (see StartupAffinity.Window).
    List<StartupStep>? _windowSteps;
    int _windowNext;
    double _windowMs;

    // §(gated window ladder) — see StartWindowLadder. Null _scheduleFallback ⇒ the OLD, ungated behaviour: the ladder
    // is posted the instant Run collects it, exactly as before this was added (every existing production caller and
    // every existing test that doesn't pass this stays byte-for-byte the same).
    readonly Action<Action, double>? _scheduleFallback;
    readonly double _windowLadderFallbackMs;
    bool _windowLadderArmed;

    /// <param name="post">The host's UI-thread poster (<c>Context.UsePost()</c>). Used for the Window ladder and for
    /// the worker phase's completion accounting — never to carry the work itself.</param>
    /// <param name="worker">The off-thread scheduler (in production <c>a => Task.Run(a)</c>). Takes ONE closure per
    /// activation, so the worker steps stay sequential relative to each other and cannot interleave.</param>
    /// <param name="measured">Always-on accounting: step name, the phase it actually ran in, wall-clock ms. This is
    /// what keeps <c>[playback.buckets] startup</c> honest — a step that moved to a worker reports its cost under
    /// <see cref="StartupAffinity.Worker"/>, not as a UI-thread cost that vanished.</param>
    /// <param name="phaseCompleted">A whole phase finished: which phase, how many steps, the summed ms. This is the
    /// rollup the accounting line reports, so "the UI thread's share is near zero and the worker's is not" is a fact in
    /// the log rather than a claim in a commit message.</param>
    /// <param name="failed">A step threw. Every step is isolated: one failing OS surface must not abandon the rest of
    /// the schedule, and must never propagate into the frame or the pool.</param>
    /// <param name="commands">The pre-activation command gate to open once the core phase is done — in production the
    /// SAME instance <c>PlaybackBridge</c> holds, so there is exactly one gate and it cannot drift out of step with the
    /// schedule. Null in tests that only care about the step sequencing.</param>
    /// <param name="scheduleFallback">When supplied, <see cref="Run"/> does NOT post the Window ladder itself — it
    /// hands <paramref name="scheduleFallback"/> a <c>(callback, delayMs)</c> pair and the CALLER decides when to run
    /// it: normally <see cref="StartWindowLadder"/>, called once the app's own "first content painted" signal fires,
    /// but never later than <paramref name="windowLadderFallbackMs"/> after <see cref="Run"/> — so the OS surfaces
    /// always appear even on a route that never reveals (a crash loop in the page, a stuck query). Whichever caller
    /// gets there first wins; <see cref="StartWindowLadder"/> is idempotent, so the loser costs nothing. Null (the
    /// default) preserves the original behaviour: the ladder starts the instant <see cref="Run"/> collects it.</param>
    /// <param name="windowLadderFallbackMs">The hard deadline passed to <paramref name="scheduleFallback"/>. Ignored
    /// when <paramref name="scheduleFallback"/> is null.</param>
    public StartupActivation(Action<Action> post, Action<Action> worker,
        Action<string, StartupAffinity, double>? measured = null,
        Action<StartupAffinity, int, double>? phaseCompleted = null,
        Action<string, Exception>? failed = null,
        ActivationCommandQueue? commands = null,
        Action<Action, double>? scheduleFallback = null,
        double windowLadderFallbackMs = 2000)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(worker);
        _post = post;
        _worker = worker;
        _measured = measured;
        _phaseCompleted = phaseCompleted;
        _failed = failed;
        _commands = commands ?? new ActivationCommandQueue(ex => failed?.Invoke("queued-command", ex));
        _scheduleFallback = scheduleFallback;
        _windowLadderFallbackMs = windowLadderFallbackMs;
    }

    /// <summary>Have the <see cref="StartupAffinity.Core"/> steps finished? Once true, <see cref="Submit"/> runs its
    /// action straight through instead of queueing it. Deliberately NOT gated on the worker/window phases: those add OS
    /// surfaces, they do not make the transport any more or less able to accept a command, and gating commands on a
    /// shell COM call is how a queued intent turns into a hang.</summary>
    public bool IsReady => _commands.IsOpen;

    /// <summary>Commands held because they arrived before the core phase. Zero once <see cref="IsReady"/> is true.</summary>
    public int QueuedCount => _commands.QueuedCount;

    /// <summary>Run the schedule. Returns false when activation already happened (idempotent by contract, not by luck).
    ///
    /// <para>Call from the mount effect, on the UI thread. On return, the core phase is complete and every command that
    /// was waiting has been released; the worker phase is in flight and the window ladder is posted.</para></summary>
    public bool Run(IReadOnlyList<StartupStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        lock (_gate)
        {
            if (_started) return false;
            _started = true;
        }

        int coreSteps = 0;
        double coreMs = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Affinity != StartupAffinity.Core) continue;
            coreSteps++;
            coreMs += Execute(steps[i], StartupAffinity.Core);
        }
        if (coreSteps > 0) _phaseCompleted?.Invoke(StartupAffinity.Core, coreSteps, coreMs);

        // Commands are accepted from HERE, not from the end of Run: the transport is fully wired, and the two phases
        // below only decorate the OS shell — gating an intent on a shell COM call is how a queued command turns into a
        // hang. Release is idempotent, so the bridge's own Activate step having already opened the same gate is fine.
        _commands.Release();

        List<StartupStep>? workerSteps = Collect(steps, StartupAffinity.Worker);
        List<StartupStep>? windowSteps = Collect(steps, StartupAffinity.Window);

        if (workerSteps is not null)
            _worker(() => RunWorkerPhase(workerSteps));

        if (windowSteps is not null)
        {
            _windowSteps = windowSteps;
            _windowNext = 0;
            if (_scheduleFallback is null)
                _post(RunNextWindowStep);   // no gating configured — unchanged from before this was added
            else
                _scheduleFallback(StartWindowLadder, _windowLadderFallbackMs);   // the reveal path calls StartWindowLadder itself; whichever wins, the other is a no-op
        }
        return true;
    }

    /// <summary>Start the Window ladder now. The two callers race — the app's first-content-reveal signal, and the
    /// fallback deadline handed to <c>scheduleFallback</c> — and whichever gets here first wins; the other call is a
    /// no-op, so losing the race costs nothing. Also a no-op when there is nothing to run (no Window steps in the
    /// schedule) or <see cref="Run"/> has not been called yet. Safe from any thread — the fallback timer does not run
    /// on the UI thread, and <c>_post</c> is what actually marshals the first step onto it.</summary>
    public void StartWindowLadder()
    {
        lock (_gate)
        {
            if (_windowSteps is null || _windowLadderArmed) return;
            _windowLadderArmed = true;
        }
        _post(RunNextWindowStep);
    }

    /// <summary>Run <paramref name="command"/> now if the core phase is done, otherwise HOLD it in arrival order until
    /// it is. Returns true when it ran synchronously, false when it was queued. Safe from any thread — the pre-mount
    /// callers are an audio host and an OS activation callback, not the UI thread.
    ///
    /// <para>This replaces the old <c>if (_post is not { } post) return;</c> shape, which silently dropped a notice
    /// that arrived a few milliseconds too early — the cold-launch case being exactly the one worth reporting.</para></summary>
    public bool Submit(Action command) => _commands.Submit(command);

    void RunWorkerPhase(List<StartupStep> steps)
    {
        double ms = 0;
        for (int i = 0; i < steps.Count; i++)
            ms += Execute(steps[i], StartupAffinity.Worker);
        // The rollup is a log line's worth of work, so it rides the poster like every other off-thread result rather
        // than writing from the pool thread.
        if (_phaseCompleted is { } done) _post(() => done(StartupAffinity.Worker, steps.Count, ms));
    }

    // UI-thread only (Run, then its own posted callbacks) — no lock needed on the ladder's cursor.
    void RunNextWindowStep()
    {
        var steps = _windowSteps;
        if (steps is null || _windowNext >= steps.Count) { _windowSteps = null; return; }
        var step = steps[_windowNext++];
        _windowMs += Execute(step, StartupAffinity.Window);
        // One surface per drain. Re-posting from inside the callback is what spreads the ladder across drains instead
        // of collapsing it back into a single stall.
        if (_windowNext < steps.Count) { _post(RunNextWindowStep); return; }
        _windowSteps = null;
        _phaseCompleted?.Invoke(StartupAffinity.Window, steps.Count, _windowMs);
    }

    double Execute(in StartupStep step, StartupAffinity ranAs)
    {
        long t0 = Stopwatch.GetTimestamp();
        try { step.Body(); }
        catch (Exception ex) { _failed?.Invoke(step.Name, ex); }
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _measured?.Invoke(step.Name, ranAs, ms);
        return ms;
    }

    static List<StartupStep>? Collect(IReadOnlyList<StartupStep> steps, StartupAffinity affinity)
    {
        List<StartupStep>? matched = null;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Affinity != affinity) continue;
            matched ??= new List<StartupStep>(steps.Count);
            matched.Add(steps[i]);
        }
        return matched;
    }
}
