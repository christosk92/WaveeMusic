using System;
using System.Collections.Generic;

namespace Wavee.Backend;

// ── Connect playback OWNERSHIP — the one authority ──────────────────────────────────────────────────────────────────
// Who owns playback right now: this device (Us), another Connect device (Foreign), or nobody. Before this there were
// two authorities that disagreed — the controller's sticky `_ownsActivePlayback` (demoted only on an active-id
// TRANSITION) and the projection's raw cluster `ActiveDeviceId` + a 5 s wall-clock "pending" window — plus several
// `is_active=true` writers that never asked either one. Every Connect incident of 2026-09-11 (the stray-reload loop,
// transfer-to-self playing here while the bar said "Playing on iPhone", the PLAY glyph over audible playback, the launch
// slot steal) was the two of them answering differently. Routing, the audio host, the now-playing display and the
// wire's is_active all read THIS state now.
//
// ORDERING. Only SERVER timestamps are ever compared with each other. A local claim cannot be ordered against a cluster
// push by clocks (the claim is UtcNow/monotonic, the push is server time, and a push the server built while our PUT was
// still in flight carries a server time AFTER our claim yet knows nothing of it). So a claim is judged by the server's
// own answer to it: the put-state RESPONSE is a Cluster, and its `server_timestamp_ms` becomes the FENCE. Pushes older
// than the fence cannot revoke; a push newer than the fence that names another device is a genuine takeover. The 5 s
// protection window only bounds the case where that response never arrives.
//
// References: librespot's spirc drops to inactive on any cluster naming another device and only becomes active through
// a transfer/play command or its own activation (connect/src/spirc.rs); fastpotify routes and renders from the one
// Spirc is_active flag (src/app.rs). Pure: no clock, no I/O, no logging — ConnectOwnership below adds the lock.

public enum OwnerKind { Nobody, Us, Foreign }

/// <summary>Only meaningful while <see cref="OwnerKind.Us"/>. Protected = claimed, the server's verdict pending;
/// Adopted = the cluster names us; Unadopted = we play but the cluster does not name us (a masked module / local-file
/// context the server will not adopt, a lost registration, no publisher attached).</summary>
public enum ClaimPhase { None, Protected, Adopted, Unadopted }

/// <summary>Why nobody owns playback — decides what the display shows (FromForeign keeps the departed device's
/// snapshot) and that a stale "us" cluster is not ownership.</summary>
public enum NobodyCause { Launch, StaleSelf, FromUs, FromForeign }

public enum ClaimCause { UserPlay, UserResume, NobodyTransferToSelf, InboundPlay, InboundTransfer, InboundResume, InboundSkip, InboundQueueStart }

public enum ReleaseCause { TransferAway, EndOfContext, LoadFailed, Logout }

public enum ClusterOrigin { Push, PutResponse }

public enum LoadOrigin { Claim, Advance, MediaKindRefresh, VideoRecovery, Restore }

[Flags]
public enum OwnerFx
{
    None = 0,
    /// <summary>The frame is older than one already folded — drop it everywhere (projection AND roster).</summary>
    DropFrame = 1,
    /// <summary>The audio/video host must not run: stop it (keep the session), bump the load generation.</summary>
    StopHost = 2,
    /// <summary>We WERE the owner and lost it — emit BecameInactive (Gabo segment close, publisher reset).</summary>
    EmitBecameInactive = 4,
    /// <summary>Tell the connect-state service we are no longer active.</summary>
    PublishInactive = 8,
    /// <summary>The server answered our claim by keeping another device — log connect.claim.rejected.</summary>
    ClaimRejected = 16,
}

/// <summary>One cluster as the ownership fold sees it. <paramref name="PutMsgId"/> is the put-state message id this
/// cluster answers (0 for a dealer push).</summary>
public readonly record struct ClusterFrame(ClusterOrigin Origin, uint PutMsgId, string ActiveId, long ServerTs,
    int UpdateReason = 0, IReadOnlyList<string>? ChangedDevices = null, long ActiveStartedPlayingAt = 0);

/// <summary>The whole ownership state. <see cref="DeviceId"/> is the foreign owner (Foreign) or the device that just
/// left (Nobody/FromForeign). <see cref="ClaimMsgId"/> is the first is_active put-state sent for the current claim (0 =
/// not sent yet). <see cref="FenceServerTs"/> is the server time at which the server acknowledged our claim.</summary>
public readonly record struct OwnerState(
    OwnerKind Kind,
    string DeviceId,
    ClaimPhase Claim,
    NobodyCause Cause,
    long ClaimId,
    uint ClaimMsgId,
    long ClaimStartedAtMs,
    long ProtectUntilMono,
    long FenceServerTs,
    long LastServerTs,
    string LastSeenActive,
    long LastSeenServerTs)
{
    public static OwnerState Initial => new(OwnerKind.Nobody, "", ClaimPhase.None, NobodyCause.Launch,
        0, 0, 0, 0, 0, 0, "", 0);

    public bool IsUs => Kind == OwnerKind.Us;

    /// <summary>Compact form for log lines: <c>Us/Protected</c>, <c>Foreign(756f53b6)</c>, <c>Nobody(FromForeign)</c>.</summary>
    public string Describe() => Kind switch
    {
        OwnerKind.Us => "Us/" + Claim,
        OwnerKind.Foreign => "Foreign(" + Short(DeviceId) + ")",
        _ => "Nobody(" + Cause + ")",
    };

    static string Short(string id) => id.Length > 8 ? id[..8] : id;
}

public readonly record struct OwnerTransition(OwnerState From, OwnerState To, OwnerFx Fx, string Cause);

public static class PlaybackOwnership
{
    /// <summary>How long a claim waits for the server's verdict (its put-state response) before it decides on what
    /// it has seen. Bounds a LOST response only; the normal verdict arrives in one round trip.</summary>
    public const long ClaimProtectMs = 5000;

    /// <summary>The cluster fold — rows F0–F6 (not ours), P1–P4 (claim pending), A1–A3 (claim settled) of the design
    /// (docs/plans/wavee/handoff-20260911-connect-detail.md; the approved plan's table).</summary>
    public static (OwnerState, OwnerFx) OnCluster(OwnerState s, in ClusterFrame f, string us)
    {
        bool isVerdict = s.Kind == OwnerKind.Us && s.Claim == ClaimPhase.Protected && f.Origin == ClusterOrigin.PutResponse
                         && s.ClaimMsgId != 0 && f.PutMsgId >= s.ClaimMsgId;

        // F0 — the ONE stale guard. A slow PUT's response can land after a newer push; folding it would regress the
        // active id. Frames without a server time are never dropped (nothing to order them by).
        if (f.ServerTs > 0 && f.ServerTs < s.LastServerTs)
        {
            // …except that the answer to our CLAIM still ends the wait: it was overtaken by a newer push, which P2
            // recorded, so that newer push is the truth now — a foreign device took over after the server adopted us,
            // or nobody holds it. Dropping the verdict instead would sit Protected (and audible) until expiry.
            if (!isVerdict) return (s, OwnerFx.DropFrame);
            if (s.LastSeenActive.Length > 0)
                return (ToForeign(s, s.LastSeenActive), OwnerFx.DropFrame | OwnerFx.StopHost | OwnerFx.EmitBecameInactive);
            return (ClearSeen(s with { Claim = ClaimPhase.Unadopted, FenceServerTs = s.LastServerTs }), OwnerFx.DropFrame);
        }
        s = s with { LastServerTs = Math.Max(s.LastServerTs, f.ServerTs) };
        string active = f.ActiveId ?? "";
        bool namesUs = active.Length > 0 && active == us;
        bool namesForeign = active.Length > 0 && !namesUs;

        switch (s.Kind)
        {
            case OwnerKind.Nobody:
                if (namesForeign) return (ToForeign(s, active), OwnerFx.StopHost);                          // F3
                if (namesUs) return (s with { Cause = NobodyCause.StaleSelf }, OwnerFx.None);                // F2
                return (s, OwnerFx.None);                                                                    // F1

            case OwnerKind.Foreign:
                if (namesForeign) return (ToForeign(s, active), OwnerFx.StopHost);                          // F4
                if (namesUs) return (ToNobody(s, NobodyCause.StaleSelf, ""), OwnerFx.None);                  // F6
                return (ToNobody(s, NobodyCause.FromForeign, s.DeviceId), OwnerFx.None);                     // F5
        }

        // Us.
        if (s.Claim == ClaimPhase.Protected)
        {
            if (namesUs)                                                                                     // P1
            {
                long fence = f.ServerTs > 0 ? f.ServerTs : s.LastServerTs;
                var adopted = s with { Claim = ClaimPhase.Adopted, FenceServerTs = fence };
                // A push that arrived DURING protection and named another device with a server time past the fence
                // was a real takeover racing our claim: honour it now instead of waiting for its next heartbeat.
                if (s.LastSeenActive.Length > 0 && s.LastSeenServerTs > fence)
                    return (ToForeign(adopted, s.LastSeenActive), OwnerFx.StopHost | OwnerFx.EmitBecameInactive);
                return (ClearSeen(adopted), OwnerFx.None);
            }
            if (isVerdict && namesForeign)                                                                   // P3
                return (ToForeign(s, active), OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.ClaimRejected);
            if (isVerdict)                                                                                   // P4
                return (ClearSeen(s with { Claim = ClaimPhase.Unadopted, FenceServerTs = Math.Max(f.ServerTs, s.LastServerTs) }), OwnerFx.None);
            // P2 — a push (or the answer to a put sent BEFORE the claim) naming someone else may predate our claim:
            // remember it, decide at the verdict or at expiry.
            return (s with { LastSeenActive = active, LastSeenServerTs = f.ServerTs }, OwnerFx.None);
        }

        // Adopted / Unadopted.
        if (namesUs)                                                                                         // A1
            return (s with { Claim = ClaimPhase.Adopted, FenceServerTs = Math.Max(s.FenceServerTs, f.ServerTs) }, OwnerFx.None);
        if (namesForeign && (f.ServerTs == 0 || f.ServerTs > s.FenceServerTs))                               // A2
            return (ToForeign(s, active), OwnerFx.StopHost | OwnerFx.EmitBecameInactive);
        if (active.Length == 0 && s.Claim == ClaimPhase.Adopted && f.ServerTs >= s.FenceServerTs)            // A3
            return (s with { Claim = ClaimPhase.Unadopted }, OwnerFx.None);
        return (s, OwnerFx.None);
    }

    /// <summary>A claim: an explicit local play/resume, a transfer-to-self fallback, or an inbound command addressed to
    /// us. <paramref name="acknowledged"/> = a publisher is attached that will send the claim and fold its response;
    /// without one (unit tests, the fake backend) there is no verdict to wait for and the claim is Unadopted at once.</summary>
    public static (OwnerState, OwnerFx) OnClaim(OwnerState s, ClaimCause c, long id, long startedAtMs, long nowMono, bool acknowledged)
    {
        if (s.Kind == OwnerKind.Us)
        {
            // Already ours: keep the phase. A NEW playback (not a resume/skip) restamps started_at, which is what the
            // server's newest-starter rule compares across devices.
            bool restamp = c is ClaimCause.UserPlay or ClaimCause.InboundPlay or ClaimCause.InboundTransfer;
            return (restamp ? s with { ClaimId = id, ClaimStartedAtMs = startedAtMs } : s, OwnerFx.None);
        }
        var claimed = s with
        {
            Kind = OwnerKind.Us,
            DeviceId = "",
            Claim = acknowledged ? ClaimPhase.Protected : ClaimPhase.Unadopted,
            Cause = NobodyCause.Launch,
            ClaimId = id,
            ClaimMsgId = 0,
            ClaimStartedAtMs = startedAtMs,
            ProtectUntilMono = acknowledged ? nowMono + ClaimProtectMs : 0,
            FenceServerTs = acknowledged ? 0 : s.LastServerTs,
        };
        return (ClearSeen(claimed), OwnerFx.None);
    }

    /// <summary>Binds the claim to the first is_active put-state sent after it — its response is the verdict.</summary>
    public static OwnerState OnPutSent(OwnerState s, uint msgId, bool isActive)
        => s.Kind == OwnerKind.Us && s.Claim == ClaimPhase.Protected && s.ClaimMsgId == 0 && isActive
            ? s with { ClaimMsgId = msgId }
            : s;

    /// <summary>The claim's put-state failed: no verdict will come. Keep playing, Unadopted; the next announce
    /// (reconnect / re-announce) re-asserts is_active.</summary>
    public static (OwnerState, OwnerFx) OnPutFailed(OwnerState s, uint msgId)
        => s.Kind == OwnerKind.Us && s.Claim == ClaimPhase.Protected && s.ClaimMsgId != 0 && msgId == s.ClaimMsgId
            ? (ClearSeen(s with { Claim = ClaimPhase.Unadopted, FenceServerTs = s.LastServerTs }), OwnerFx.None)
            : (s, OwnerFx.None);

    /// <summary>Protection expiry (the verdict never arrived): decide on what was seen meanwhile.</summary>
    public static (OwnerState, OwnerFx) OnTick(OwnerState s, long nowMono)
    {
        if (s.Kind != OwnerKind.Us || s.Claim != ClaimPhase.Protected || nowMono < s.ProtectUntilMono) return (s, OwnerFx.None);
        if (s.LastSeenActive.Length > 0)
            return (ToForeign(s, s.LastSeenActive), OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.ClaimRejected);
        return (s with { Claim = ClaimPhase.Unadopted, FenceServerTs = s.LastServerTs }, OwnerFx.None);
    }

    /// <summary>We give playback up. Pause never releases (ownership is not audibility).</summary>
    public static (OwnerState, OwnerFx) OnRelease(OwnerState s, ReleaseCause c)
    {
        if (s.Kind != OwnerKind.Us) return (s, OwnerFx.None);
        var nobody = ToNobody(s, NobodyCause.FromUs, "");
        return c is ReleaseCause.TransferAway or ReleaseCause.Logout
            ? (nobody, OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.PublishInactive)
            : (nobody, OwnerFx.PublishInactive);
    }

    /// <summary>Local unless another device owns playback (then every transport verb forwards to it).</summary>
    public static bool RoutesLocal(in OwnerState s) => s.Kind != OwnerKind.Foreign;

    /// <summary>The wire's is_active — its ONE writer.</summary>
    public static bool IsActiveOnWire(in OwnerState s) => s.Kind == OwnerKind.Us;

    /// <summary>May the host load media for <paramref name="origin"/>? Foreign: never. Nobody: only a PAUSED restore
    /// (launch recovery seeds without claiming). Us: always.</summary>
    public static bool AllowsLoad(in OwnerState s, LoadOrigin origin, bool paused) => s.Kind switch
    {
        OwnerKind.Us => true,
        OwnerKind.Nobody => origin == LoadOrigin.Restore && paused,
        _ => false,
    };

    /// <summary>Does now-playing show the LOCAL session (vs the cluster's player_state)? A departed foreign device's
    /// snapshot stays on screen (paused) until the user acts — no flip to a stale local session on a phone flap.</summary>
    public static bool ShowsLocalNowPlaying(in OwnerState s, bool hasLocalSession) => s.Kind switch
    {
        OwnerKind.Us => true,
        OwnerKind.Nobody => s.Cause != NobodyCause.FromForeign && hasLocalSession,
        _ => false,
    };

    static OwnerState ToForeign(OwnerState s, string id) => ClearSeen(s with
    {
        Kind = OwnerKind.Foreign, DeviceId = id, Claim = ClaimPhase.None, ClaimMsgId = 0, ClaimStartedAtMs = 0,
        ProtectUntilMono = 0, FenceServerTs = 0,
    });

    static OwnerState ToNobody(OwnerState s, NobodyCause cause, string departed) => ClearSeen(s with
    {
        Kind = OwnerKind.Nobody, DeviceId = departed, Claim = ClaimPhase.None, Cause = cause, ClaimMsgId = 0,
        ClaimStartedAtMs = 0, ProtectUntilMono = 0, FenceServerTs = 0,
    });

    static OwnerState ClearSeen(OwnerState s) => s with { LastSeenActive = "", LastSeenServerTs = 0 };
}

/// <summary>The thread-safe holder: one <see cref="OwnerState"/> behind a lock, every transition raised on
/// <see cref="Changed"/> OUTSIDE the lock (listeners stop hosts, publish, log — never under our gate). Owned by
/// <c>NowPlayingProjection</c>; the controller, the publisher and the bridge read <see cref="Current"/>.</summary>
public sealed class ConnectOwnership
{
    readonly object _gate = new();
    readonly string _us;
    readonly Func<long> _nowMono;
    readonly Func<long> _startedAtMs;
    OwnerState _s = OwnerState.Initial;
    long _nextClaimId;
    bool _acknowledged;

    /// <param name="startedAtMs">The claim's started_playing_at stamp — the server-corrected clock when synced (the
    /// server compares it against other devices' stamps), else UtcNow.</param>
    public ConnectOwnership(string us, Func<long> nowMono, Func<long> startedAtMs)
    {
        _us = us;
        _nowMono = nowMono;
        _startedAtMs = startedAtMs;
    }

    public event Action<OwnerTransition>? Changed;

    public OwnerState Current { get { lock (_gate) return _s; } }

    /// <summary>A publisher is attached: claims now wait for the server's verdict (Protected) instead of settling
    /// Unadopted at once.</summary>
    public void AttachAcknowledger() { lock (_gate) _acknowledged = true; }

    public OwnerFx OnCluster(in ClusterFrame f, string cause = "cluster")
    {
        OwnerState from, to; OwnerFx fx;
        lock (_gate) { from = _s; (to, fx) = PlaybackOwnership.OnCluster(_s, f, _us); _s = to; }
        Raise(from, to, fx, cause);
        return fx;
    }

    /// <summary>Claims playback and returns the claim id.</summary>
    public long Claim(ClaimCause c)
    {
        OwnerState from, to; OwnerFx fx; long id;
        lock (_gate)
        {
            from = _s;
            id = ++_nextClaimId;
            (to, fx) = PlaybackOwnership.OnClaim(_s, c, id, _startedAtMs(), _nowMono(), _acknowledged);
            _s = to;
        }
        Raise(from, to, fx, "claim:" + c);
        return id;
    }

    public void OnPutSent(uint msgId, bool isActive)
    {
        OwnerState from, to;
        lock (_gate) { from = _s; to = PlaybackOwnership.OnPutSent(_s, msgId, isActive); _s = to; }
        Raise(from, to, OwnerFx.None, "put-sent");
    }

    public OwnerFx OnPutFailed(uint msgId)
    {
        OwnerState from, to; OwnerFx fx;
        lock (_gate) { from = _s; (to, fx) = PlaybackOwnership.OnPutFailed(_s, msgId); _s = to; }
        Raise(from, to, fx, "put-failed");
        return fx;
    }

    public OwnerFx Tick()
    {
        OwnerState from, to; OwnerFx fx;
        lock (_gate) { from = _s; (to, fx) = PlaybackOwnership.OnTick(_s, _nowMono()); _s = to; }
        Raise(from, to, fx, "protect-expired");
        return fx;
    }

    public OwnerFx Release(ReleaseCause c)
    {
        OwnerState from, to; OwnerFx fx;
        lock (_gate) { from = _s; (to, fx) = PlaybackOwnership.OnRelease(_s, c); _s = to; }
        Raise(from, to, fx, "release:" + c);
        return fx;
    }

    void Raise(in OwnerState from, in OwnerState to, OwnerFx fx, string cause)
    {
        // Raised on real changes only — a steady-state fold (the 1 Hz heartbeat naming the same owner) is silent, and so
        // is a dropped stale frame (the caller reads DropFrame off the return value). StopHost on an unchanged Foreign
        // state IS raised: it is level-triggered on purpose, so a host that started late still gets stopped.
        bool same = from.Kind == to.Kind && from.Claim == to.Claim && from.DeviceId == to.DeviceId
            && from.Cause == to.Cause && from.ClaimMsgId == to.ClaimMsgId && from.ClaimStartedAtMs == to.ClaimStartedAtMs;
        if (same && (fx & ~OwnerFx.DropFrame) == OwnerFx.None) return;
        Changed?.Invoke(new OwnerTransition(from, to, fx, cause));
    }
}
