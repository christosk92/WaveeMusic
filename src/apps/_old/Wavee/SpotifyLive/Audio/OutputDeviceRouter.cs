using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;

namespace Wavee.SpotifyLive.Audio;

/// <summary>The re-route request the router hands the engine: re-open <see cref="TargetDeviceId"/> (null = system
/// default), optionally pausing first (device-loss UX). The engine posts a pending re-init to its decode thread.</summary>
public readonly record struct OutputDeviceReroute(string Reason, string? TargetDeviceId, bool PauseFirst);

/// <summary>Pure, COM-free output-selection / fallback / auto-return state machine (plan §A3). Owned by
/// <c>AudioPlayEngine</c>; all COM re-init happens on the decode thread — this type only decides. Everything is injectable
/// (monitor, log, clock, debounce delay) so it is deterministically unit-testable with a fake monitor + manual clock.</summary>
internal sealed class OutputDeviceRouter : IDisposable
{
    static readonly int[] RetryLadderMs = { 250, 1000, 3000 };

    readonly IAudioDeviceMonitor _monitor;
    readonly WaveeLogger _log;
    readonly Func<long> _clock;
    readonly TrailingCoalescer _debounce;
    readonly object _gate = new();
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    // Desired: null = SystemDefault; non-null = Explicit(deviceId).
    string? _desiredDeviceId;
    // Opened: the ACTUAL endpoint id the engine last reported open (null before the first open), and whether it was a fallback.
    string? _openedActualId;
    bool _openedAsFallback;
    bool _hasOpened;
    string? _awaitingReturnOf;
    // In-flight: a reroute has been emitted but not yet confirmed by NotifyOpened (dedup so a topology burst folds to one).
    string? _inflightTarget;
    bool _hasInflight;
    int _retryCount;
    long _nextRetryAtMs = long.MaxValue;
    OutputDeviceReroute? _retryReroute;
    bool _disposed;

    /// <summary>Fired when the engine must re-open an output. Raised OUTSIDE the router lock.</summary>
    public event Action<OutputDeviceReroute>? RouteInvalidated;
    /// <summary>User-facing device notices (toasts). Raised OUTSIDE the router lock.</summary>
    public event Action<OutputDeviceNotice>? Notice;

    public OutputDeviceRouter(IAudioDeviceMonitor monitor, WaveeLogger log, Func<long> clock,
        int debounceMs = 300, Func<int, CancellationToken, Task>? debounceDelay = null)
    {
        _monitor = monitor;
        _log = log;
        _clock = clock;
        _debounce = new TrailingCoalescer(debounceMs, clock, debounceDelay);
        _monitor.Changed += OnDeviceEvent;
    }

    /// <summary>What a fresh renderer Init should open (deviceId, or null = system default). Resolves the fallback while a
    /// lost explicit device is awaited.</summary>
    public string? ResolveTarget() { lock (_gate) return ResolveTargetLocked(); }

    // ── inputs ────────────────────────────────────────────────────────────────────────────────────────────────────────
    public void SetDesired(string? deviceId)
    {
        deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        var emit = new List<object>(1);
        lock (_gate)
        {
            if (_disposed) return;
            _desiredDeviceId = deviceId;
            _awaitingReturnOf = null;           // a new pick cancels any pending auto-return (plan test 6)
            ResetRetryLocked();
            if (_hasOpened && !WouldBeNoOp(deviceId))
                EmitReroute("set-desired", deviceId, pauseFirst: false, emit);
        }
        Flush(emit);
    }

    /// <summary>The engine's renderer just faulted (AUDCLNT_E_DEVICE_INVALIDATED) on the decode thread.</summary>
    public void ReportDeviceInvalidated()
    {
        var emit = new List<object>(2);
        lock (_gate)
        {
            if (_disposed || !_hasOpened) return;
            ResetRetryLocked();
            if (WouldBeNoOp(null)) return;   // already falling back to default (idempotent while faulted is polled)
            if (_desiredDeviceId is { } desired)
            {
                if (_awaitingReturnOf is null && !_openedAsFallback)
                {
                    _awaitingReturnOf = desired;
                    emit.Add(new OutputDeviceNotice(OutputDeviceNoticeKind.DeviceLost, desired, NameOf(desired), WasExplicit: true));
                }
            }
            else
            {
                // Following default, the device died with no default-changed event yet → pause + re-open current default.
                emit.Add(new OutputDeviceNotice(OutputDeviceNoticeKind.DeviceLost, _openedActualId ?? "", NameOf(_openedActualId), WasExplicit: false));
            }
            EmitReroute("device-invalidated", null, pauseFirst: true, emit);
        }
        Flush(emit);
    }

    /// <summary>A renderer Init failed to open (hr &lt; 0). Arms the bounded retry ladder; exhaustion → OutputFailed.</summary>
    public void ReportOpenFailed(int hr)
    {
        var emit = new List<object>(1);
        lock (_gate)
        {
            if (_disposed) return;
            _hasInflight = false;   // the emitted reroute did not result in an open; the ladder now owns the retry
            if (_retryCount >= RetryLadderMs.Length)
            {
                emit.Add(new OutputDeviceNotice(OutputDeviceNoticeKind.OutputFailed, _desiredDeviceId ?? "", NameOf(_desiredDeviceId), _desiredDeviceId is not null));
                _retryReroute = null;
                _nextRetryAtMs = long.MaxValue;
                _log.Info($"audio.device output-failed hr=0x{hr:X8} retries={_retryCount} — idling until next device event/command");
            }
            else
            {
                int delay = RetryLadderMs[_retryCount];
                _retryCount++;
                _nextRetryAtMs = _clock() + delay;
                _retryReroute = new OutputDeviceReroute("open-retry", ResolveTargetLocked(), PauseFirst: false);
                _log.Info($"audio.device open-failed hr=0x{hr:X8} retry={_retryCount} in {delay}ms");
            }
        }
        Flush(emit);
    }

    /// <summary>The engine successfully opened an output — the ACTUAL endpoint id + whether it was a fallback.</summary>
    public void NotifyOpened(string? actualDeviceId, bool asFallback)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _openedActualId = actualDeviceId;
            _openedAsFallback = asFallback;
            _hasOpened = true;
            _hasInflight = false;
            ResetRetryLocked();
            if (!string.IsNullOrEmpty(actualDeviceId) && _monitor.GetFriendlyName(actualDeviceId!) is { Length: > 0 } nm)
                _names[actualDeviceId!] = nm;
        }
    }

    /// <summary>The decode loop polls this each iteration; a due retry deadline fires the pending re-route.</summary>
    public void Tick(long nowMs)
    {
        OutputDeviceReroute? fire = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_retryReroute is { } r && nowMs >= _nextRetryAtMs)
            {
                fire = r;
                _retryReroute = null;
                _nextRetryAtMs = long.MaxValue;
            }
        }
        if (fire is { } rr) Raise(rr);   // a retry bypasses the no-op guard (it RE-attempts the same target)
    }

    // ── device-topology events (OS callback thread → debounced) ───────────────────────────────────────────────────────
    void OnDeviceEvent(AudioDeviceEvent _)
    {
        lock (_gate) { if (_disposed) return; ResetRetryLocked(); }   // any topology change resets an exhausted ladder
        _debounce.Post(Recompute);
    }

    void Recompute()
    {
        var emit = new List<object>(2);
        lock (_gate)
        {
            if (_disposed || !_hasOpened) return;   // events before first Init only record state; Init resolves via ResolveTarget()

            var active = _monitor.EnumerateRenderEndpoints();
            foreach (var e in active) _names[e.Id] = e.Name;

            if (_desiredDeviceId is null)
            {
                var def = _monitor.GetDefaultRenderId();
                if (!string.IsNullOrEmpty(def) && !WouldBeNoOp(def))
                {
                    _log.Info($"audio.device default-changed → seamless follow to {def}");
                    EmitReroute("follow-default", null, pauseFirst: false, emit);   // target null = "the current default"
                }
                Flush(emit);
                return;
            }

            string desired = _desiredDeviceId;
            bool desiredActive = false;
            foreach (var e in active) if (SameId(e.Id, desired)) { desiredActive = true; break; }

            if (desiredActive)
            {
                if (!SameId(_openedActualId, desired) && !WouldBeNoOp(desired))
                {
                    bool displaced = (_awaitingReturnOf is not null && SameId(_awaitingReturnOf, desired)) || _openedAsFallback;
                    _awaitingReturnOf = null;
                    if (displaced)
                        emit.Add(new OutputDeviceNotice(OutputDeviceNoticeKind.DeviceRestored, desired, NameOf(desired), WasExplicit: true));
                    EmitReroute("device-restored", desired, pauseFirst: false, emit);
                }
            }
            else
            {
                bool alreadyFellBack = _openedAsFallback || (_awaitingReturnOf is not null && SameId(_awaitingReturnOf, desired));
                if (!alreadyFellBack)
                {
                    _awaitingReturnOf = desired;
                    emit.Add(new OutputDeviceNotice(OutputDeviceNoticeKind.DeviceLost, desired, NameOf(desired), WasExplicit: true));
                    EmitReroute("device-lost", null, pauseFirst: true, emit);
                }
            }
        }
        Flush(emit);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────
    string? ResolveTargetLocked() => _awaitingReturnOf is not null ? null : _desiredDeviceId;
    void ResetRetryLocked() { _retryCount = 0; _retryReroute = null; _nextRetryAtMs = long.MaxValue; }
    static bool SameId(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    // A reroute to a target that is already open (or already being opened) is a no-op — absorbs A2DP/HFP profile flaps
    // and folds topology bursts into a single re-init (plan §A3 debounce + test 7).
    bool WouldBeNoOp(string? target) => _hasInflight ? SameId(target, _inflightTarget) : _hasOpened && SameId(target, _openedActualId);

    void EmitReroute(string reason, string? target, bool pauseFirst, List<object> emit)
    {
        _inflightTarget = target;
        _hasInflight = true;
        emit.Add(new OutputDeviceReroute(reason, target, pauseFirst));
    }

    string NameOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (_names.TryGetValue(id!, out var n) && n.Length > 0) return n;
        var live = _monitor.GetFriendlyName(id!);
        if (!string.IsNullOrEmpty(live)) { _names[id!] = live!; return live!; }
        return "";
    }

    void Flush(List<object> emit)
    {
        foreach (var m in emit)
        {
            if (m is OutputDeviceNotice n) { try { Notice?.Invoke(n); } catch { } }
            else if (m is OutputDeviceReroute r) Raise(r);
        }
    }

    void Raise(OutputDeviceReroute r) { try { RouteInvalidated?.Invoke(r); } catch { } }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _monitor.Changed -= OnDeviceEvent;
        _debounce.Dispose();
    }
}
