// ── Playback/Playback.Host.cs ──────────────────────────────────────────────────────────────────────────────────────
// Post loop, signals, Pending, ticker
//
// Role: SHELL
// Owner: G
// Wave: 3
// Budget: 800 lines
// Spec: plan
// Named partial: `Playback.Host.Context.cs` (gap batch B3) — playing a context, autoplay, the queue reorder, the launch
// restore and the play report
// Named partial: `Playback.Host.Remote.cs` (gap batch B3c) — the Connect intake: the mailbox and the inbound play,
// transfer and queue bodies in arrival order, and a controller's queue applied
// Named partial: `Playback.Host.Wire.cs` (gap batch R4-1) — the PutState's parity half captured, the foreign owner's
// queue, the queue forwards, the sign-out's inactive PUT
// Named partial: `Playback.Host.Autoplay.cs` (queue refill) — autoplay, its pages, the session watch
//
// THE LOOP. `Playback.cs` decides; this file is the only thing that touches the world. Three jobs and no fourth:
//
//   IN     `Post(in Input)` — thread-safe, allocation-free, bounded. It writes the VALUE into a ring and wakes the UI
//          thread with ONE cached delegate; it never runs `Step`. Every other thread in the app (dealer, api, audio
//          pump, OS bridges, module rpc) reaches playback through it and through nothing else (C1/C2).
//   DRAIN  `Drain()` — UI thread, at the top of a frame, inside the engine's own Batch: `Spotify.Connect`'s mailbox
//          and the host's own Connect intakes (a play/transfer body, a controller's queue) first and in ARRIVAL order
//          (`Playback.Host.Remote.cs`), then the queue watch, then the ring, then the queue follow (its buckets put back
//          under the cursor the ring moved). One `Step` per value, effects executed ONCE, signals written ONCE. Ten
//          `Next` clicks in one drain are ten `Step`s, one `Load` and one render (C3). The shell's poster runs
//          `Entities.Publish` after every posted drain, so a follow or a queue write made here reaches the bound
//          surfaces in the same frame.
//   OUT    `Execute()` — the effect slots become calls on owner H's audio/video/OS seams and on `Spotify.Connect`'s
//          glue, each stamped with the epoch it was decided for so the shell can cancel what it has superseded (C4).
//
// THE ONE CLOCK. `Now()` converts the engine's frame stamp to milliseconds ONCE per drain and every input made here
// carries it (memory rule `animations-sample-frame-time`). The core reads no clock at all, which is why a whole
// playback session replays deterministically in a unit test.
//
// THE SEAMS TO OWNER H (`Playback.Audio.cs` / `Playback.Video.cs` / `Playback.Os.cs` — parts of THIS partial class).
// Execute routes by the host that holds the row (`s_hostKind`): a Video row goes to `Video`, everything else to
// `Audio`, and a load that changes hosts stops the outgoing one first (`MediaSwitch.HostChanges`).
//
//   static class Audio { Load(row, id, kind, epoch, fromMs); Stop(); Pause(); Resume(); Seek(ms, epoch);
//                        SetVolume(linear01); Prepare(row, id); }
//   static class Video { Load(source, epoch, fromMs); Stop(); Play(); Pause(); Seek(ms); SetVolume(amplitude); }
//   static class Os    { Publish(in State s); }
//   partial void PumpAdopt(uint from, uint to);  PumpPrefetch(row, id);  PumpCancelPrepared();   ← owner H implements
//
// The three `Pump*` are PARTIAL METHODS on purpose: the reducer's hand-off/prefetch/cancel shapes land before the
// pump batch implements them, and an unimplemented partial compiles to nothing (a prefetch that never warms, a slot
// the next load disposes anyway) rather than to a broken build.
//
// Rules: bounded queues, never unbounded (C8); the UI thread never blocks (C9); one named timer (P10).

using FluentGpu.Foundation;
using FluentGpu.Signals;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;
using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;

namespace Wavee;

public static partial class Playback
{
    // ── 1. the UI-thread seam ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE marshaller onto the UI thread — the same contract as <c>Store.Post</c> and <c>Spotify.Post</c>:
    /// <c>App.cs</c> sets it to <c>AppHost.Post</c>, a test leaves it, and the default runs the action inline, which
    /// is what makes every fact in this file's suite single-threaded and deterministic.
    ///
    /// <para>It is a separate name from <see cref="Post(in Input)"/> because the two are different things: this is
    /// "run something on the UI thread", that is "an input happened".</para></summary>
    public static Action<Action> ToUi { get; set; } = static a => a();

    /// <summary>Milliseconds on the host's monotonic FRAME clock. Sampled once per drain and stamped onto every input
    /// made here; the core never reads a clock (the memory rule, and the reason `Step` is testable).</summary>
    public static Func<long> FrameNowMs { get; set; } = static () => Environment.TickCount64;

    /// <summary>Unix milliseconds — a DATE, for the places that genuinely need one: the PUT body's
    /// <c>client_side_timestamp</c>, <see cref="State.TunedInAtMs"/>, a transfer's extrapolation and the play log.
    /// Never used for motion or extrapolation on screen.</summary>
    public static Func<long> UnixNowMs { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── 2. the state, the effects, the bounded inbox (C8) ───────────────────────────────────────────────────────────

    static State s_state = State.Initial;
    static Effects s_fx;
    static DeviceIdentity s_identity;

    /// <summary>Which host holds the row the reducer last loaded: the audio pump, or the video host. Pause, resume and
    /// seek follow it; a load for the other kind stops it first (G-140).</summary>
    static PlayableKind s_hostKind = PlayableKind.Audio;

    /// <summary>`DeviceInfo.spirc_version` — the protocol version the Connect service checks, ported verbatim from
    /// 0.2.9's `SpotifyClientIdentity`. It is NOT the app version and does not move when the client pin moves.</summary>
    public const string SpircVersion = "3.2.6";

    /// <summary>How many posted inputs the ring holds before the OLDEST is dropped. A controller holding NEXT down, an
    /// audio pump ticking at 200 ms and a dealer push can all arrive between two frames; the only ones that matter
    /// then are the newest. The drop is counted and logged — bounded, never silent.</summary>
    public const int InboxDepth = 256;

    static readonly Input[] s_inbox = new Input[InboxDepth];
    static readonly Lock s_gate = new();
    static int s_head, s_count, s_dropped;
    static bool s_wakeQueued;
    static bool s_draining, s_redrain;
    static uint s_queueVersion;

    /// <summary>Cached so <see cref="Post(in Input)"/> allocates NOTHING, however hard a button is clicked.</summary>
    static readonly Action s_drain = Drain;

    /// <summary>How many posted inputs were dropped because the ring filled between two frames. Non-zero is a stalled
    /// UI thread, not a busy network; the diagnostics page shows it.</summary>
    public static int DroppedInputs => Volatile.Read(ref s_dropped);

    // ── 3. the signals (C1: written on the UI thread, in one Batch, once per drain) ──────────────────────────────────

    /// <summary>The current playable, cross-kind. <c>IsNone</c> when idle, and — while a remote device owns playback —
    /// a row this catalog has only just allocated for the identity the cluster named.</summary>
    public static readonly Signal<EntityRef> Current = new(default);
    /// <summary>Its identity. Non-empty before <see cref="Current"/> resolves to a row, so the bar can paint a
    /// transfer immediately.</summary>
    public static readonly Signal<EntityId> CurrentId = new(default);
    /// <summary>The context it is playing from — the art tile's "Playing from…" route.</summary>
    public static readonly Signal<EntityId> ContextUri = new(default);
    public static readonly Signal<Phase> PhaseSignal = new(Phase.Idle);
    /// <summary>The play/pause glyph. Derived once here rather than re-derived at each of its six readers.</summary>
    public static readonly Signal<bool> IsPlaying = new(false);
    /// <summary>Refilling — distinct from <see cref="Phase.Loading"/>, which is "opening".</summary>
    public static readonly Signal<bool> Buffering = new(false);
    public static readonly Signal<Fault> Error = new(Fault.None);
    /// <summary>Drives the "Reconnecting" band and the top-edge sweep.</summary>
    public static readonly Signal<RecoveryKind> Recovery = new(RecoveryKind.None);
    public static readonly Signal<int> PositionMs = new(0);
    /// <summary>0 = unknown, and the seek bar is DISABLED rather than drawn as a full grey rail.</summary>
    public static readonly Signal<int> DurationMs = new(0);
    /// <summary>0..1, LINEAR — the SLIDER's value (<see cref="State.SliderVolume"/>): the foreign owner's volume while
    /// one owns playback, ours otherwise. The cubic taper is the audio host's.</summary>
    public static readonly Signal<float> Volume = new(1f);
    public static readonly Signal<bool> Shuffle = new(false);
    public static readonly Signal<RepeatMode> Repeat = new(RepeatMode.Off);
    /// <summary>The FOLDED enablement — restrictions, phase, error and (while live) the DVR window, decided once.</summary>
    public static readonly Signal<bool> CanSeek = new(false);
    public static readonly Signal<bool> CanSkipNext = new(false);
    public static readonly Signal<bool> CanSkipPrev = new(false);
    /// <summary>The context's own Next / Previous permission, NOT folded with the error state — what the player bar
    /// needs to keep skipping armed while a dead row sits on the deck (<c>State.NextAllowedByContext</c>).</summary>
    public static readonly Signal<bool> NextAllowedByContext = new(false);
    public static readonly Signal<bool> PrevAllowedByContext = new(false);
    /// <summary>Us / Foreign / Nobody — the ONE authority every router, badge and publisher reads.</summary>
    public static readonly Signal<Owner> OwnerSignal = new(Owner.Nobody);
    /// <summary>The active Connect device's row in <see cref="Devices"/>, or -1. Owner-DERIVED: there is no
    /// <c>is_active</c> fallback anywhere in the app.</summary>
    public static readonly Signal<int> ActiveDeviceSlot = new(-1);
    /// <summary>The whole live timeline as ONE value, so the bar can never render a half-updated mix.</summary>
    public static readonly Signal<LiveWindow> Live = new(LiveWindow.None);
    /// <summary>The DECIDED edge state (hysteresis). The UI may never re-derive it — it did once, and shipped a
    /// flicker bug.</summary>
    public static readonly Signal<bool> IsBehindLive = new(false);
    /// <summary>UNIX ms this device tuned in at; <c>&lt;= 0</c> means "not known" and the label falls back to the
    /// reported position, never to 0:00.</summary>
    public static readonly Signal<long> TunedInAtMs = new(0L);
    /// <summary>The stream's quality badge, cleared the moment a remote device owns playback.</summary>
    public static readonly Signal<StringId> StreamFormat = new(StringId.Empty);
    /// <summary>Is the video host what is actually playing? The lyrics peek suppresses itself — and stops its clock —
    /// on this.</summary>
    public static readonly Signal<bool> VideoActive = new(false);
    /// <summary>Bumped to ask the shell to open the device picker (a toast's action, a media-key fallback).</summary>
    public static readonly Signal<int> DevicePickerRequest = new(0);
    /// <summary>The identity of the last row the deck stepped past ON ITS OWN because its load failed for good
    /// (<see cref="AutoSkip"/>, <see cref="Effects.SkippedUnavailable"/>) — a natural end landing on a track the catalog
    /// no longer resolves. The shell's toast names it; <see cref="SkippedUnavailableCount"/> bumps with every skip, so a
    /// watcher fires per skip even when the same identity dies twice.</summary>
    public static readonly Signal<EntityId> LastSkippedUnavailable = new(default);
    /// <inheritdoc cref="LastSkippedUnavailable"/>
    public static readonly Signal<uint> SkippedUnavailableCount = new(0u);

    /// <summary>Per-effect in-flight bits (D21). A button that should feel instant IGNORES these (Next / Prev / Seek);
    /// a button that must not double-fire reads one and renders disabled + spinner (Transfer). The decision is a
    /// one-line bind in the UI; the mechanism underneath is identical for every button.</summary>
    public static class Pending
    {
        public static readonly Signal<bool> Load = new(false);
        public static readonly Signal<bool> Transfer = new(false);
    }

    // ── 4. the device roster (ch 20 §7 asks for its home; this is it) ───────────────────────────────────────────────

    /// <summary>The Connect roster, as the last cluster stated it — the rows the picker paints and the ONLY place a
    /// device-id STRING lives. The core folds identity as a <see cref="DeviceHash"/>; the outbound routes need the
    /// text, so the translation happens exactly here and nowhere else.</summary>
    public static class Devices
    {
        public struct Row
        {
            public ulong Hash;
            /// <summary>The device id as TEXT — the only place one exists outside the wire. Null on a slot the roster
            /// has grown into and not yet filled, which is why every reader goes through <see cref="IdOf"/>.</summary>
            public string? Id;
            public string? Name;
            public Spotify.Decode.DeviceKind Kind;
            /// <summary>0..65535, the wire scale.</summary>
            public int Volume;
            public bool IsActive;
        }

        static Row[] s_rows = new Row[8];
        static int s_count;
        static uint s_version;

        /// <summary>Bumps whenever the roster changed — the number the picker's bound list compares.</summary>
        public static readonly Signal<uint> Changed = new(0u);

        public static ReadOnlySpan<Row> Rows => s_rows.AsSpan(0, s_count);
        public static int Count => s_count;

        public static int SlotOf(ulong hash)
        {
            if (hash == 0) return -1;
            for (int i = 0; i < s_count; i++) if (s_rows[i].Hash == hash) return i;
            return -1;
        }

        public static string IdOf(ulong hash)
        {
            int slot = SlotOf(hash);
            return slot < 0 ? "" : s_rows[slot].Id ?? "";
        }

        public static string NameOf(ulong hash)
        {
            int slot = SlotOf(hash);
            return slot < 0 ? "" : s_rows[slot].Name ?? "";
        }

        /// <summary>Replace the roster from one cluster. Strings are only materialised for a hash the roster does not
        /// already hold, so a 1 Hz heartbeat over five devices allocates nothing after the first push.</summary>
        internal static void Update(Spotify.Decode.ClusterBuffer buffer, in Spotify.Decode.ClusterDelta delta, ulong active)
        {
            var devices = buffer.Devices(delta.DeviceStart, delta.DeviceCount);
            if (devices.Length > s_rows.Length) Array.Resize(ref s_rows, devices.Length);
            bool changed = devices.Length != s_count;
            for (int i = 0; i < devices.Length; i++)
            {
                ref readonly var d = ref devices[i];
                ulong hash = DeviceHash(buffer.Utf8(d.Id));
                ref var row = ref s_rows[i];
                if (row.Hash != hash || row.Id is null)
                {
                    row.Hash = hash;
                    row.Id = Text(buffer, d.Id);
                    row.Name = Text(buffer, d.Name);
                    changed = true;
                }
                else if (string.IsNullOrEmpty(row.Name)) row.Name = Text(buffer, d.Name);
                bool isActive = hash != 0 && hash == active;
                if (row.Kind != d.Kind || row.Volume != d.Volume || row.IsActive != isActive) changed = true;
                row.Kind = d.Kind;
                row.Volume = d.Volume;
                row.IsActive = isActive;
            }
            s_count = devices.Length;
            if (!changed) return;
            Changed.Value = ++s_version;
        }

        static string Text(Spotify.Decode.ClusterBuffer buffer, TextRef reference)
        {
            var utf8 = buffer.Utf8(reference);
            return utf8.IsEmpty ? "" : System.Text.Encoding.UTF8.GetString(utf8);
        }
    }

    // ── 5. boot ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Called once by <c>App.cs</c>, after <c>Platform.Boot</c> / <c>Entities.Boot</c> / <c>Spotify.Boot</c>.
    /// It creates nothing that can fail and opens nothing: the audio pump and the OS bridges arm themselves on their
    /// own first use, so a <c>--fake</c> run and a unit test both boot the reducer with no device anywhere.</summary>
    public static void Boot()
    {
        s_state = State.Initial;
        s_state.EpisodeRate = ValidEpisodeSpeed(Platform.Settings.Get(s_episodeSpeedKey));
        EpisodeSpeed.SetIfChanged(s_state.EpisodeRate);
        s_state.Us = DeviceHash(Platform.DeviceId);
        s_identity = new DeviceIdentity(
            Platform.DeviceId,
            Environment.MachineName + " - Wavee",                       // what every other client's picker shows for this box
            Spotify.Identity.ClientId,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, // actual operating system
            Spotify.Identity.ClientVersion,                // `DeviceInfo.device_software_version`
            SpircVersion, Spotify.Audio.CanDerive);

        if (Platform.Settings.Get(Platform.Keys.RememberVolume))
            s_state.Volume = Math.Clamp(Platform.Settings.Get(Platform.Keys.SavedVolume), 0f, 1f);

        // The dealer's mailbox wakes us; the drain reads it. One closure, made once.
        Spotify.Connect.Wake = static () => ToUi(s_drain);
        // The session reaching Online asks for the hello; the snapshot is ours, so it is captured on the UI thread.
        s_hellos = 0;
        Spotify.Connect.Hello = static () => ToUi(s_hello);
        s_boundScope = Entities.Current;                    // the scope the deck's slots belong to (G-241, Rebind)
        WatchSession();                                    // every transition into Online → Input.SessionOnline

        Publish();
        StartTicker();
        Log.Info("playback", "boot device=" + Platform.Redact(Platform.DeviceId) + " volume=" + s_state.Volume.ToString("0.00"));
    }

    /// <summary>The live state, for a diagnostics row or a shell that needs the whole value at once. A COPY — nobody
    /// outside this file writes playback state (C1).</summary>
    public static State Snap() => s_state;

    /// <summary>The value a connect-state PUT encodes, captured NOW: this device's identity, our volume, and — while we
    /// own playback — the player half (<see cref="Snapshot.Of"/>). UI THREAD (it reads the reducer's state, C1). The
    /// session's hello and a headless host's <c>--connect</c> announce both send it through
    /// <c>Spotify.Connect.PublishNow</c>; the message id is minted by the glue.</summary>
    public static Snapshot SnapshotForConnect(PublishReason reason = PublishReason.NewDevice)
    {
        bool hasVideo = CurrentHasVideo();
        long now = FrameNowMs();
        WireExtras wire = CaptureWire(now);                // the parity half (G-240), Playback.Host.Wire.cs
        return Snapshot.Of(in s_state, in s_identity, reason, 0, UnixNowMs(), now, CurrentUid(), hasVideo,
            hasVideo ? CurrentVideoGid() : "", in wire);
    }

    /// <summary>Which reason a hello carries: the first announce of this boot is <see cref="PublishReason.NewDevice"/>,
    /// every later one (a reconnect's fresh connection id) <see cref="PublishReason.NewConnection"/>. Both reach the
    /// glue as its NewDevice route (<see cref="WireReason"/>); the body's put_state_reason tells the service which. PURE.</summary>
    public static PublishReason HelloReason(int announcedBefore)
        => announcedBefore <= 0 ? PublishReason.NewDevice : PublishReason.NewConnection;

    static int s_hellos;
    static readonly Action s_hello = static () =>
    {
        Rebind();                                          // the welcome that preceded Online may have switched scope (G-241)
        Announce(HelloReason(s_hellos++));
    };

    // ── 6. in: Post (any thread) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Post one input. Thread-safe, allocation-free, and bounded. It does NOT run <see cref="Step"/> — the
    /// value goes into the ring and the UI thread folds it at the top of the next frame, inside one Batch (C1/C3).
    ///
    /// <para>Calling it from the UI thread is the same thing: the input still waits for the drain, so a reducer can
    /// never re-enter itself from an effect.</para></summary>
    public static void Post(in Input i)
    {
        bool wake;
        lock (s_gate)
        {
            if (s_count == InboxDepth)
            {
                // Drop the OLDEST: the newest input is the one that matters when a burst outruns a frame.
                s_head = (s_head + 1) % InboxDepth;
                s_count--;
                s_dropped++;
            }
            s_inbox[(s_head + s_count) % InboxDepth] = i;
            s_count++;
            wake = !s_wakeQueued;
            s_wakeQueued = true;
        }
        if (wake) ToUi(s_drain);
    }

    /// <summary>Ask for a drain without an input — a queue write the next drain's watch must see.</summary>
    static void RequestDrain()
    {
        bool wake;
        lock (s_gate) { wake = !s_wakeQueued; s_wakeQueued = true; }
        if (wake) ToUi(s_drain);
    }

    // ── 7. the drain (UI thread) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE DRAIN = ONE BATCH = ONE RENDER (C3). Connect's mailbox first — those results are older than
    /// anything posted since — then an inbound load, the queue watch and the ring, then the effects once, then the
    /// signals once. RE-ENTRANT-SAFE: a marshaller that runs a posted drain inline (a test's, a headless loop's) marks
    /// the running drain to go round once more instead of nesting inside its Execute.</summary>
    static void Drain()
    {
        if (s_draining) { s_redrain = true; return; }
        s_draining = true;
        try
        {
            do
            {
                s_redrain = false;
                lock (s_gate) s_wakeQueued = false;

                long now = FrameNowMs();
                Rebind();                                  // a scope switch re-slots the deck before anything reads it (G-241)
                DrainConnect(now);                         // the mailbox and the host's Connect intakes, in arrival order
                WatchQueue(now);

                while (true)
                {
                    Input i;
                    lock (s_gate)
                    {
                        if (s_count == 0) break;
                        i = s_inbox[s_head];
                        s_inbox[s_head] = default;         // release the references the value holds
                        s_head = (s_head + 1) % InboxDepth;
                        s_count--;
                    }
                    StepOne(in i);
                }

                FollowQueue(now);
                Execute();
                s_forward.Clear();                         // every staged forward was folded in this drain (G-248)
                Publish();
            }
            while (s_redrain);
        }
        finally { s_draining = false; }
    }

    /// <summary>One Step, and the play report it produced. The report is the one slot that is a SEQUENCE (a registration
    /// must close before the next opens), so it is drained here, per Step, and never coalesced (G-076).</summary>
    static void StepOne(in Input i)
    {
        Owner ownerBefore = s_state.Own.Kind;
        ulong deviceBefore = s_state.Own.Device;
        ClaimPhase claimBefore = s_state.Own.Claim;
        if (i.Kind == InputKind.Play && !i.Context.Equals(s_wireOriginContext)) s_wireOrigin = null;
        uint loadBefore = s_state.LoadEpoch;
        Step(ref s_state, in i, ref s_fx);
        if (s_state.LoadEpoch != loadBefore && (s_fx.Load || s_fx.Adopt))
            WireStarted(s_state.Context, s_state.StartReason);
        if (i.Kind == InputKind.SetSpeed || (i.Kind == InputKind.RemoteCommand && i.Command.Kind == RemoteCmd.SetPlaybackSpeed))
        {
            EpisodeSpeed.SetIfChanged(s_state.EpisodeRate);
            if (s_registration.Open) Spotify.Telemetry.RateChanged(ref s_registration, s_state.PosMs, s_state.ContentRate);
            Audio.SetRate(s_state.ContentRate);
            Video.SetRate(s_state.ContentRate);
        }
        LogOwnerTransition(ownerBefore, deviceBefore, claimBefore);
        if (s_fx.Play.Events == PlayEvents.None) return;
        PlayReport report = s_fx.Play;
        s_fx.Play = default;
        RunPlayReport(in report);
    }

    /// <summary>B5: the ONE always-on line that answers "did we just gain, lose or hand off Connect ownership, and
    /// why" — the diagnostics page has the verdict; the log the user actually sends did not (2026-09-16/18's
    /// invisibility reports both traced back to state no log line carried). Compares <c>s_state.Own</c> before and
    /// after the Step that just ran — the pure fold (<c>Playback.Ownership.Fold</c>, owner D's file) stays free of
    /// logging, so this is the ONLY place a transition is noted. A Kind change or a Device change is exactly what the
    /// fold's own StopHost / ClaimRejected rows produce (`ToForeign` sets one or the other every time), so diffing the
    /// public state is a complete, precise proxy for those internal flags without threading them out of the fold.</summary>
    static void LogOwnerTransition(Owner ownerBefore, ulong deviceBefore, ClaimPhase claimBefore)
    {
        Owner ownerAfter = s_state.Own.Kind;
        ulong deviceAfter = s_state.Own.Device;
        if (ownerAfter == ownerBefore && deviceAfter == deviceBefore) return;

        string cause = claimBefore == ClaimPhase.Protected && ownerAfter == Owner.Foreign ? "claim-rejected"
            : ownerAfter == Owner.Foreign ? "takeover"
            : ownerAfter == Owner.Nobody ? s_state.Own.Cause.ToString()
            : ownerAfter == Owner.Us ? "claimed"
            : "cluster";
        Log.Info("playback", "connect.owner " + ownerBefore + " → " + ownerAfter + " (" + cause + ")"
            + " fx=" + (s_fx.Stop ? "Stop" : "-") + " claim=" + s_state.Own.Claim + " claimMsg=" + s_state.Own.ClaimMsgId);
    }

    /// <summary>Fold an input made on the UI thread: inline when a drain is running (so it lands in THIS drain's
    /// effects), posted otherwise.</summary>
    static void Apply(in Input i)
    {
        if (s_draining) StepOne(in i);
        else Post(in i);
    }

    /// <summary>One mailbox item → inputs, here and nowhere else. An item minted under a session that has since closed is
    /// dropped (G-036) — it describes a connection that no longer exists. The buffer a cluster item carries is handed back
    /// the moment its fold has read it — hold a <see cref="TextRef"/> past that and the pool starves. The ORDER the items
    /// and the host's own intakes are taken in is `Playback.Host.Remote.cs`'s `DrainConnect`.</summary>
    static void FoldItem(in Spotify.Connect.Item item, long now)
    {
        try
        {
            if (item.Epoch != Spotify.Current.Epoch) return;
            switch (item.Kind)
            {
                case Spotify.Connect.ItemKind.Cluster:
                    FoldCluster(in item, now);
                    break;
                case Spotify.Connect.ItemKind.RemoteCommand:
                    StepOne(Input.Controller(in item.Command, now));
                    break;
                case Spotify.Connect.ItemKind.Volume:
                    // THIS device's volume, set by a controller: applied and announced, never forwarded (G-073).
                    StepOne(Input.RemoteVolume(item.Volume, item.VolumeMessageId, now));
                    break;
            }
        }
        finally { Spotify.Connect.Release(in item); }
    }

    /// <summary>One cluster → the two PURE values the core folds: the ownership <see cref="ClusterFrame"/> and the
    /// mirrored <see cref="RemoteState"/>. Every id becomes a hash or an <see cref="EntityId"/> HERE, so the reducer
    /// never sees a <see cref="TextRef"/>, never resolves a string and never routes off a raw cluster id.</summary>
    static void FoldCluster(in Spotify.Connect.Item item, long now)
    {
        Spotify.Decode.ClusterBuffer? buffer = item.Buffer;
        if (buffer is null) return;
        ref readonly Spotify.Decode.ClusterDelta d = ref item.Delta;

        ulong active = DeviceHash(buffer.Utf8(d.ActiveDeviceId));
        Devices.Update(buffer, in d, active);

        var frame = new ClusterFrame(d.Origin, d.PutMsgId, active, d.ServerTimestampMs, d.UpdateReason,
            d.ActiveStartedPlayingAt);
        var remote = new RemoteState(
            d.HasTrack,
            d.HasTrack ? Identify(buffer.Utf8(d.Track.Uri)) : default,
            d.IsPlaying, d.IsPaused, d.IsBuffering,
            d.PositionAsOfMs, d.TimestampMs, d.DurationMs,
            d.Shuffling, d.Repeat, d.ActiveVolume,
            d.NoPrev, d.NoNext, d.NoSeek,
            Identify(buffer.Utf8(d.ContextUri)));   // the owner's context: what a takeover adopts (A4)

        StepOne(Input.Cluster(in frame, in remote, now));
        s_state.ActiveDeviceSlot = Devices.SlotOf(s_state.ActiveDevice);
        // The owner's own queue, kept for a forwarded set_queue to echo back (G-248). A cluster the fold dropped is not it.
        if (s_state.Own.Kind == Owner.Foreign && active == s_state.Own.Device) s_foreign.Take(buffer, in d);
        else if (s_state.Own.Kind != Owner.Foreign) s_foreign.Clear();
    }

    /// <summary>A wire uri → a packed identity. UI thread, so the text form may intern (C1).</summary>
    static EntityId Identify(ReadOnlySpan<byte> utf8) => utf8.IsEmpty ? default : EntityId.Parse(utf8);

    /// <summary>Anything that edits the queue — a page's "Add to queue", a controller, an autoplay append, a reorder —
    /// bumps its version; the reducer re-arms the next row on the change (G-112).</summary>
    static void WatchQueue(long now)
    {
        if (Entities.Current is null) return;
        uint version = Queue.Version;
        if (version == s_queueVersion) return;
        s_queueVersion = version;
        StepOne(Input.QueueChanged(now));
    }

    static Scope? s_followScope;
    static int s_followIndex = -1;
    static uint s_followVersion;

    /// <summary>Put the queue's buckets back under the deck once the ring has folded (<see cref="FollowsQueue"/> says
    /// when; <c>Queue.Follow</c> rewrites nothing when they already match). A follow that did rewrite bumps the queue's
    /// version, and the watch re-arms the next row inside THIS drain, so the pump is told the row that really follows —
    /// after a Previous into history that is the history row, not the one the stale buckets skipped to. Skipped outright
    /// while neither the cursor nor the queue has moved since the last follow, so a position tick copies nothing.</summary>
    static void FollowQueue(long now)
    {
        Scope? scope = Entities.Current;
        if (scope is null || s_state.Cursor.IsNone) return;
        int index = s_state.Cursor.Index;
        if (ReferenceEquals(scope, s_followScope) && index == s_followIndex && Queue.Version == s_followVersion) return;
        if (!FollowsQueue(in s_state, Queue.RefAt(index))) return;
        if (Queue.Follow(in s_state.Cursor)) WatchQueue(now);
        s_followScope = scope;
        s_followIndex = index;
        s_followVersion = Queue.Version;
    }

    // ── 8. out: Execute (the effect slots, read ONCE) ───────────────────────────────────────────────────────────────

    static void Execute()
    {
        if (!s_fx.Any) { s_fx.Clear(); return; }

        // Order matters exactly once: a host that must STOP before the next stream opens has to stop first, and an
        // adoption only means something when no load replaced the voice it names.
        // A takeover re-seeds the queue and the cursor from the owner's last cluster BEFORE the load it rides with: the
        // seed bumps the queue version, and the watch re-arms the next row inside this drain (A4).
        if (s_fx.TakeoverSeed) SeedQueueFromCluster(takeover: true);
        if (s_fx.Stop) StopHost();
        if (s_fx.Load) LoadHost(s_fx.LoadRow, s_fx.LoadId, s_fx.LoadKind, s_fx.LoadEpoch, s_fx.LoadFromMs, s_fx.LoadPaused);
        else if (s_fx.Adopt) PumpAdopt(s_fx.AdoptFrom, s_fx.AdoptTo);
        if (s_fx.CancelPrepared) PumpCancelPrepared();
        if (s_fx.PauseHost) { if (s_hostKind == PlayableKind.Video) Video.Pause(); else Audio.Pause(); }
        if (s_fx.ResumeHost) { if (s_hostKind == PlayableKind.Video) Video.Play(); else Audio.Resume(); }
        if (s_fx.Seek) { if (s_hostKind == PlayableKind.Video) Video.Seek(s_fx.SeekMs); else Audio.Seek(s_fx.SeekMs, s_fx.SeekEpoch); }
        if (s_fx.Volume)
        {
            Audio.SetVolume(s_fx.VolumeValue);
            Video.SetVolume(Audio.VolumeTaper.Amplitude(s_fx.VolumeValue));
            if (Platform.Settings.Get(Platform.Keys.RememberVolume))
                Platform.Settings.Set(Platform.Keys.SavedVolume, s_fx.VolumeValue);
        }
        if (s_fx.PrepareNext) Audio.Prepare(s_fx.NextRow, s_fx.NextId);
        if (s_fx.Prefetch2) PumpPrefetch(s_fx.Prefetch2Row, s_fx.Prefetch2Id);   // the farther row first: the nearer one's caches stay newest
        if (s_fx.Prefetch) PumpPrefetch(s_fx.PrefetchRow, s_fx.PrefetchId);
        if (s_fx.Fetch) EnsureRow(s_fx.FetchId);
        if (s_fx.QueueAdd) EnqueueRemote(s_fx.QueueAddId);
        if (s_fx.Reorder) ReorderQueue(s_fx.ReorderShuffle);
        if (s_fx.Autoplay) RequestAutoplay(s_fx.AutoplayContext);
        if (s_fx.AutoplayPage) RequestAutoplayPage(s_fx.AutoplayPageContext);
        if (s_fx.Page) RequestPage(s_fx.PageContext);
        if (s_fx.VideoDemoted) OnVideoDemoted?.Invoke(s_fx.VideoDemotedWhy);
        if (s_fx.SkippedUnavailable)
        {
            LastSkippedUnavailable.Value = s_fx.SkippedId;
            SkippedUnavailableCount.Value = SkippedUnavailableCount.Peek() + 1;
            Log.Info("playback", $"auto-skip: dead row {s_fx.SkippedId} stepped past (run {s_state.AutoSkips}/{AutoSkip.MaxConsecutive})");
        }
        if (s_fx.PublishState) Announce(s_fx.PublishWhy);
        if (s_fx.SendRemote) SendRemote();
        if (s_fx.Transfer) SendTransfer();
        if (s_fx.Smtc || s_fx.SmtcTimeline) Os.Publish(in s_state);
        if (s_fx.Snapshot) PersistDeck?.Invoke(RestorePoint.Of(in s_state, FrameNowMs()));
        s_fx.Clear();
    }

    /// <summary>OWNER H, the pump (`Playback.Audio.cs`): a hand-off advanced the deck without a Load — if the pump's
    /// load epoch is still <paramref name="fromEpoch"/>, it becomes <paramref name="toEpoch"/>, and the joined row's
    /// Duration and Format are re-posted under it (G-100).</summary>
    static partial void PumpAdopt(uint fromEpoch, uint toEpoch);

    /// <summary>OWNER H: warm the next row only — head, mirrors, key; no ring, no decoder (G-112).</summary>
    static partial void PumpPrefetch(EntityRef row, EntityId id);

    /// <summary>OWNER H: the prepared row stopped being next — dispose the slot and any join not yet live (G-112).</summary>
    static partial void PumpCancelPrepared();

    static void StopHost()
    {
        if (s_hostKind == PlayableKind.Video) Video.Stop();
        else Audio.Stop();
    }

    /// <summary>Route a load to the host its kind names, stopping the other host first when the kind crosses the video
    /// line (G-140). A paused load is followed by the host's own pause in the same pass. <paramref name="fromMs"/> is
    /// already the episode's resume point when a Claim or an Advance started one "from the top": the reducer's load tail
    /// resolves it on the deck (<c>EpisodeStartOf</c> in <c>EmitLoad</c>), so a paused load shows it from the first frame.</summary>
    static void LoadHost(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs, bool paused)
    {
        Pending.Load.Value = true;
        if (MediaSwitch.HostChanges(s_hostKind, kind)) StopHost();
        s_hostKind = kind;
        EnsureRow(id);                                     // an inbound row may have a slot and no identity yet
        if (kind == PlayableKind.Video) { LoadVideo(row, id, epoch, fromMs, paused); return; }
        Audio.Load(row, id, kind, epoch, fromMs);
        if (paused) Audio.Pause();
    }

    /// <summary>The video resolver: a row's manifest id (32 hex) → a playable source, or null for "no video". Blocks —
    /// api threads only. The default is the row's own gid; owner H's resolver tiers (the counterpart's gid, a TrackV4
    /// read, a local override) replace it.</summary>
    public static Func<EntityId, string, CancellationToken, Video.VideoSource?> VideoResolver { get; set; }
        = static (_, gid, ct) => gid.Length == 0 ? null : Video.Manifest.Resolve(gid, ct);

    /// <summary>Resolve on an api thread, load on the UI thread — unless a newer load superseded this one meanwhile. A
    /// row with no source demotes to audio through the reducer (<see cref="AudioSignal.VideoUnavailable"/>).</summary>
    static void LoadVideo(EntityRef row, EntityId id, uint epoch, int fromMs, bool paused)
    {
        string gid = row.Kind == EntityKind.Track && !row.IsNone && Entities.Current is not null
                     && (uint)row.Slot < (uint)Entities.Current.Tracks.Count
            ? ManifestIdOfRow(new Track(row.Slot))
            : "";
        Func<EntityId, string, CancellationToken, Video.VideoSource?> resolver = VideoResolver;
        bool queued = Spotify.Api.Run(() =>
        {
            Video.VideoSource? source = null;
            try { source = resolver(id, gid, CancellationToken.None); }
            catch (Exception ex) { Log.Warn("playback", "video resolve failed", ex); }
            if (source is null) { Post(Input.Audio(AudioSignal.VideoUnavailable, epoch, FrameNowMs())); return; }
            Video.VideoSource resolved = source;
            ToUi(() =>
            {
                if (s_state.LoadEpoch != epoch || s_hostKind != PlayableKind.Video) return;   // superseded while resolving
                Video.Load(resolved, epoch, fromMs, paused);
            });
        });
        if (!queued) Post(Input.Audio(AudioSignal.VideoUnavailable, epoch, FrameNowMs()));
    }

    /// <summary>The row's manifest id: its own video gid, else its linked counterpart's — one metadata POST fewer (B7).</summary>
    static string ManifestIdOfRow(Track t)
    {
        if (!t.VideoGidId.IsEmpty) return Entities.Strings.Resolve(t.VideoGidId);
        Track counterpart = t.VideoCounterpart;
        return counterpart.IsValid ? Entities.Strings.Resolve(counterpart.VideoGidId) : "";
    }

    /// <summary>The video placement was demoted to audio after its retry (G-142). Owner K's `Video.Host.cs` installs it:
    /// turn the surface off and say why.</summary>
    public static Action<Fault>? OnVideoDemoted { get; set; }

    // D4 (F2): Playback priority bypasses F1's per-tick drain (it pumps INLINE, immediately) — so unlike a Visible/
    // Prefetch ask, calling `Entities.Ensure` once per `EnsureRow` invocation is not coalesced for free downstream.
    // One drain (`Drain()`'s single `Execute()` → `Publish()` pass, C3) can call `EnsureRow` more than once for the
    // SAME or a different id — the Fetch effect, a Load's inbound-row correction, and Publish's own late-identity
    // fix-up can all fire in one pass — so the ask is collected here and flushed exactly once, by `Publish()`.
    static readonly List<int> s_ensureRowTrackSlots = new(4);
    static readonly List<int> s_ensureRowEpisodeSlots = new(4);

    /// <summary>Make sure the identity the core is about to paint HAS a row, and collect it for the catalog's hot
    /// group at playback priority — <see cref="FlushRowEnsures"/> issues the one span ask per kind this drain owes.
    /// Allocating the slot is what turns a foreign device's uri into a handle the bar can bind.</summary>
    static void EnsureRow(EntityId id)
    {
        if (id.IsEmpty || Entities.Current is null) return;
        Table? table = Entities.TableFor(id.Kind);
        if (table is null) return;
        int slot = table.Slot(id);
        if (slot <= Table.None) return;
        // Re-point the deck slot whenever it does not yet name the identity that is on it — not only from IsNone: a
        // slot from a retired scope (a restore before the welcome switch, a page ref captured before Entities.Switch)
        // reads some OTHER row and must be corrected too, or the bar keeps painting that other row's title forever.
        if (id.Equals(s_state.CurrentId) && !RowNamesId(s_state.Current, id))
        {
            if (!s_state.Current.IsNone)
                Log.Info("playback", "deck row re-pointed to its identity (slot " + s_state.Current.Slot + " → " + slot + ")");
            s_state.Current = new EntityRef(id.Kind, slot);
        }
        if (id.Kind == EntityKind.Track) { if (!s_ensureRowTrackSlots.Contains(slot)) s_ensureRowTrackSlots.Add(slot); }
        else if (id.Kind == EntityKind.Episode) { if (!s_ensureRowEpisodeSlots.Contains(slot)) s_ensureRowEpisodeSlots.Add(slot); }
    }

    /// <summary>The one span ask per kind this drain's <see cref="EnsureRow"/> calls owe, at Playback priority — called
    /// once, at the end of <see cref="Publish"/>, after every `EnsureRow` this drain could reach (including Publish's
    /// own late-identity correction, above) has had its say.</summary>
    static void FlushRowEnsures()
    {
        if (Entities.Current is null) { s_ensureRowTrackSlots.Clear(); s_ensureRowEpisodeSlots.Clear(); return; }
        if (s_ensureRowTrackSlots.Count > 0)
        {
            Entities.Ensure(Entities.Current.Tracks,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(s_ensureRowTrackSlots),
                (uint)TrackFields.Identity, FetchPriority.Playback);
            s_ensureRowTrackSlots.Clear();
        }
        if (s_ensureRowEpisodeSlots.Count > 0)
        {
            Entities.Ensure(Entities.Current.Episodes,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(s_ensureRowEpisodeSlots),
                (uint)EpisodeFields.Identity, FetchPriority.Playback);
            s_ensureRowEpisodeSlots.Clear();
        }
    }

    /// <summary>Announce our state to the connect-state service. The SNAPSHOT is captured here, on the UI thread,
    /// inside the drain: the glue's debounce and its PUT both run on an api thread and may not read
    /// <see cref="State"/> (C1/C9).</summary>
    static void Announce(PublishReason why)
    {
        Snapshot snapshot = SnapshotForConnect(why);
        Spotify.Connect.PutReason reason = WireReason(why);
        if (why is PublishReason.NewDevice or PublishReason.NewConnection) Spotify.Connect.PublishNow(in snapshot, reason);
        else Spotify.Connect.PublishState(in snapshot, reason);
    }

    /// <summary>An explicit map BY NAME and never a cast. The two enums now agree numerically — `Spotify.Connect
    /// .PutReason` spelled <c>BecameInactive = 6</c> when this landed, 6 is <c>PICKER_OPENED</c>, and owner F has
    /// corrected it — but members of two enums in two files are free to drift again, and this map is what makes that
    /// drift a compile error instead of a wrong announce. The BODY's reason comes from
    /// <see cref="Snapshot.Reason"/>, which is the proto's own number; this one only picks the glue's log/422
    /// branch.</summary>
    static Spotify.Connect.PutReason WireReason(PublishReason why) => why switch
    {
        PublishReason.SpircHello => Spotify.Connect.PutReason.SpircHello,
        PublishReason.SpircNotify => Spotify.Connect.PutReason.SpircNotify,
        PublishReason.NewDevice or PublishReason.NewConnection => Spotify.Connect.PutReason.NewDevice,
        PublishReason.PlayerStateChanged => Spotify.Connect.PutReason.PlayerStateChanged,
        PublishReason.VolumeChanged => Spotify.Connect.PutReason.VolumeChanged,
        PublishReason.PickerOpened => Spotify.Connect.PutReason.PickerOpened,
        PublishReason.BecameInactive => Spotify.Connect.PutReason.BecameInactive,
        _ => Spotify.Connect.PutReason.Unknown,
    };

    static readonly char[] s_uidChars = new char[UidBook.MaxChars];
    static ulong s_uidItem;
    static string s_uid = "";

    /// <summary>Every queue row's server uid, whatever its shape (G-075): the item ids the context build, a transfer, an
    /// autoplay append and a controller's queue are stored under, and the text that echoes back.</summary>
    static readonly UidBook s_uids = new();

    /// <summary>The server-minted queue uid for the row on the deck, as text (G-075): the uid a context resolve, a
    /// transfer or a controller stamped on its queue edge — 16 hex, 20 hex or <c>q2</c>, exactly as sent (<see cref="UidBook"/>).
    /// "" when this session minted the queue itself, which is what the wire expects for a locally-started context. Cached
    /// per item, so an announce storm formats it once.</summary>
    static string CurrentUid()
    {
        if (s_state.Cursor.IsNone || s_state.Current.IsNone || Entities.Current is null) return "";
        int index = s_state.Cursor.Index;
        ReadOnlySpan<QueueEdge> rows = Queue.Rows;
        if ((uint)index >= (uint)rows.Length || Queue.RefAt(index) != s_state.Current) return "";
        ulong item = rows[index].ItemId;
        if (item == 0) return "";
        if (item != s_uidItem)
        {
            s_uidItem = item;
            s_uid = new string(s_uidChars, 0, s_uids.Format(item, s_uidChars));
        }
        return s_uid;
    }

    /// <summary>Does the row on the deck carry a music video (the snapshot's offer, G-143)?</summary>
    static bool CurrentHasVideo()
    {
        EntityRef row = s_state.Current;
        if (row.IsNone || Entities.Current is null) return false;
        if (row.Kind == EntityKind.Episode) return (new Episode(row.Slot).Flags & EpisodeFlags.Video) != 0;
        if (row.Kind != EntityKind.Track) return false;
        return (uint)row.Slot < (uint)Entities.Current.Tracks.Count && new Track(row.Slot).HasVideo;
    }

    /// <summary>The deck row's 32-hex video manifest gid (the snapshot's <see cref="Snapshot.VideoGid"/>) — the interned
    /// string the track table already holds, so an announce resolves it and allocates nothing.</summary>
    static string CurrentVideoGid()
    {
        EntityRef row = s_state.Current;
        if (row.Kind == EntityKind.Episode && !row.IsNone) return Video.KnownManifestId(s_state.CurrentId);
        if (row.IsNone || row.Kind != EntityKind.Track || Entities.Current is null
            || (uint)row.Slot >= (uint)Entities.Current.Tracks.Count) return "";
        StringId gid = new Track(row.Slot).VideoGidId;
        return gid.IsEmpty ? "" : Entities.Strings.Resolve(gid);
    }

    /// <summary>A transport verb for the device that owns playback. Three ROUTES, not one — a player command, and
    /// volume's own PUT — and the glue blocks, so both run on an api thread (C9).</summary>
    static void SendRemote()
    {
        string target = Devices.IdOf(s_fx.RemoteDevice);
        if (target.Length == 0) { Log.Warn("playback", "remote verb dropped: the owner is not in the roster"); return; }
        if (s_fx.RemoteIsVolume)
        {
            int volume = (int)Math.Clamp(s_fx.RemoteArg, 0, MaxWireVolume);
            Spotify.Api.Run(() => Spotify.Connect.Volume(target, volume, CancellationToken.None));
            return;
        }
        if (s_fx.RemoteCmd is RemoteCmd.AddToQueue or RemoteCmd.SetQueue)
        {
            ForwardQueue(target, s_fx.RemoteCmd, s_fx.RemoteArg, s_fx.RemoteFlag);   // G-248, Playback.Host.Wire.cs
            return;
        }
        if (s_fx.RemoteCmd == RemoteCmd.PlayContext)
        {
            // The texts are formatted HERE (UI thread; an EntityId's text is the interner's) and captured as strings.
            string contextUri = s_fx.RemoteContext.Text, trackUri = s_fx.RemoteTrack.Text;
            bool shuffle = s_fx.RemoteFlag;
            Log.Info("playback", "play forwarded to the owner: context=" + contextUri + " track=" + trackUri);
            Spotify.Api.Run(() => Spotify.Connect.PlayContext(target, contextUri, trackUri, shuffle, CancellationToken.None));
            return;
        }
        string endpoint = Endpoint(s_fx.RemoteCmd);
        if (endpoint.Length == 0) return;
        string valueName = s_fx.RemoteCmd switch
        {
            RemoteCmd.SeekTo => "value",
            RemoteCmd.SetShufflingContext or RemoteCmd.SetRepeatingContext or RemoteCmd.SetRepeatingTrack => "value",
            _ => "",
        };
        long arg = s_fx.RemoteArg;
        bool flag = s_fx.RemoteFlag;
        Spotify.Api.Run(() => Spotify.Connect.Command(target, endpoint, valueName, arg, flag, CancellationToken.None));
    }

    /// <summary>The wire spelling of a verb. The ONE place an endpoint string exists on the outbound side — the
    /// decode half folded the inbound ones to ordinals for the same reason.</summary>
    static string Endpoint(RemoteCmd cmd) => cmd switch
    {
        RemoteCmd.Play => "play",
        RemoteCmd.Pause => "pause",
        RemoteCmd.Resume => "resume",
        RemoteCmd.SeekTo => "seek_to",
        RemoteCmd.SkipNext => "skip_next",
        RemoteCmd.SkipPrev => "skip_prev",
        RemoteCmd.SetShufflingContext => "set_shuffling_context",
        RemoteCmd.SetRepeatingContext => "set_repeating_context",
        RemoteCmd.SetRepeatingTrack => "set_repeating_track",
        _ => "",
    };

    /// <summary>Move playback to another device. The reply is posted back with the epoch it was started for, so a
    /// second transfer while this one is in flight discards the first answer (C4).</summary>
    static void SendTransfer()
    {
        string target = Devices.IdOf(s_fx.TransferTo);
        if (target.Length == 0) { Log.Warn("playback", "transfer dropped: the target is not in the roster"); return; }
        string from = Devices.IdOf(s_state.ActiveDevice);
        if (from.Length == 0) from = Platform.DeviceId;
        uint epoch = s_fx.TransferEpoch;
        Pending.Transfer.Value = true;
        Spotify.Api.Run(() =>
        {
            bool ok = Spotify.Connect.Transfer(from, target, CancellationToken.None);
            Post(Input.TransferDone(epoch, ok));
        });
    }

    // ── 9. publish (one write per signal, once per drain) ───────────────────────────────────────────────────────────

    static void Publish()
    {
        // The drain-time resolve: whatever path put an identity on deck without a matching row (a mirrored cluster, a
        // restore, a slot from a retired scope) gets one more chance here, once per drain, before the bar paints it.
        if (!s_state.CurrentId.IsEmpty && !RowNamesId(s_state.Current, s_state.CurrentId)) EnsureRow(s_state.CurrentId);
        long now = FrameNowMs();
        Current.Value = s_state.Current;
        CurrentId.Value = s_state.CurrentId;
        ContextUri.Value = s_state.Context;
        PhaseSignal.Value = s_state.Phase;
        IsPlaying.Value = s_state.IsPlaying;
        Buffering.Value = s_state.Buffering;
        Error.Value = s_state.Error;
        Recovery.Value = s_state.Recovery;
        PositionMs.Value = s_state.Position(now);
        DurationMs.Value = s_state.DurationMs;
        Volume.Value = s_state.SliderVolume;
        Shuffle.Value = s_state.Shuffle;
        Repeat.Value = s_state.Repeat;
        CanSeek.Value = s_state.CanSeek;
        CanSkipNext.Value = s_state.CanSkipNext;
        CanSkipPrev.Value = s_state.CanSkipPrev;
        NextAllowedByContext.Value = s_state.NextAllowedByContext;
        PrevAllowedByContext.Value = s_state.PrevAllowedByContext;
        OwnerSignal.Value = s_state.Owner;
        ActiveDeviceSlot.Value = s_state.ActiveDeviceSlot;
        Live.Value = s_state.Live;
        IsBehindLive.Value = s_state.Edge.IsBehind;
        TunedInAtMs.Value = s_state.TunedInAtMs;
        StreamFormat.Value = s_state.StreamFormat;
        VideoActive.Value = s_state.Kind == PlayableKind.Video && s_state.Phase is Phase.Playing or Phase.Paused;

        // The in-flight bits retire on the state that ends the flight, not on a timer: the deck is audible, so the
        // load is over; someone else owns playback, so the transfer is over.
        if (s_state.Phase is Phase.Playing or Phase.Paused or Phase.Idle or Phase.Ended) Pending.Load.Value = false;
        if (s_state.Owner != Owner.Us) Pending.Transfer.Value = false;

        WatchRow();
        Ticker(s_state.Phase == Phase.Playing || s_state.Own.Claim == ClaimPhase.Protected || AwaitsIdentity());
        FlushRowEnsures();       // D4: the one span ask per kind every EnsureRow call this drain made owes
    }

    // ── 9a. the row on the deck, watched (G-084 late hydration, G-143 the video offer) ──────────────────────────────

    /// <summary>How many drains a row with no identity keeps the ticker alive for, so a late metadata commit reaches
    /// the OS card while the deck is paused. ~30 s at the ticker's 1 s; a fetch that never lands stops asking.</summary>
    public const int IdentityWaitTicks = 30;

    static EntityId s_watchId;
    static uint s_watchVersion;
    static int s_identityWaits;
    static EntityId s_videoId;
    static bool s_videoHas;
    static StringId s_videoGid;
    static global::Wavee.Video.ConnectVideoFacts s_videoFacts = new();

    /// <summary>The row's version moved under the same identity: the catalog committed something for it. The OS card
    /// is pushed again (the bridge dedupes on id, identity and art, so only a real change costs a COM call), and the
    /// video facts are observed for a gain the service must hear about.</summary>
    static void WatchRow()
    {
        EntityRef row = s_state.Current;
        if (row.IsNone || Entities.Current is null) { s_watchId = default; s_identityWaits = 0; return; }
        Table? table = Entities.TableFor(row.Kind);
        if (table is null || (uint)row.Slot >= (uint)table.Count) return;
        uint version = table.Version[row.Slot];
        bool sameRow = s_state.CurrentId.Equals(s_watchId);
        if (sameRow && version == s_watchVersion) return;
        if (!sameRow) s_identityWaits = 0;
        s_watchId = s_state.CurrentId;
        s_watchVersion = version;
        if (sameRow) Os.Publish(in s_state);
        if (row.Kind == EntityKind.Track) ObserveVideo(new Track(row.Slot));
    }

    static void ObserveVideo(Track track)
    {
        bool has = track.HasVideo;
        StringId gid = track.VideoGidId;
        if (s_state.CurrentId.Equals(s_videoId) && has == s_videoHas && gid.Value == s_videoGid.Value) return;
        s_videoId = s_state.CurrentId;
        s_videoHas = has;
        s_videoGid = gid;
        // Owner K's tracker holds the rule (baseline on first sight, then only a real gain); strings only on a change.
        if (s_videoFacts.Observe(s_videoId.Text, has, gid.IsEmpty ? null : Entities.Strings.Resolve(gid)))
            Post(Input.VideoOffer(s_videoId));
    }

    static bool AwaitsIdentity()
    {
        EntityRef row = s_state.Current;
        if (row.IsNone || Entities.Current is null || s_identityWaits >= IdentityWaitTicks) return false;
        bool known = row.Kind switch
        {
            EntityKind.Track => (uint)row.Slot >= (uint)Entities.Current.Tracks.Count || new Track(row.Slot).Knows(TrackFields.Identity),
            EntityKind.Episode => (uint)row.Slot >= (uint)Entities.Current.Episodes.Count || new Episode(row.Slot).Knows(EpisodeFields.Identity),
            _ => true,
        };
        if (known) return false;
        s_identityWaits++;
        return true;
    }

    /// <summary>Ask the shell to open the device picker (a toast action, a media-key fallback).</summary>
    public static void RequestDevicePicker() => DevicePickerRequest.Value++;

    // ── 10. the position ticker (P10 — named, one second, only while it can do something) ───────────────────────────

    /// <summary>The transport's own cadence. One second: the labels read whole seconds, the OS timeline is gated to
    /// whole seconds by <see cref="SmtcTimelineCoalescer"/>, and the pump's 200 ms reports already carry the truth
    /// between ticks. Faster would buy nothing and wake the GPU for it (memory rule <c>gpu-load-read-wake-first</c>).
    /// </summary>
    public const int TickerPeriodMs = 1000;

    static Timer? s_ticker;
    static bool s_tickerRunning;

    static void StartTicker()
        => s_ticker ??= new Timer(static _ => Post(Input.Tick(FrameNowMs())), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Run the ticker only while it has something to do: a playing deck to extrapolate, a protected claim
    /// whose 5 s window has to be able to expire, or a row whose identity has not landed yet. An idle Wavee posts
    /// nothing and renders nothing.</summary>
    static void Ticker(bool wanted)
    {
        if (wanted == s_tickerRunning) return;
        s_tickerRunning = wanted;
        StartTicker();
        s_ticker?.Change(wanted ? TickerPeriodMs : Timeout.Infinite, wanted ? TickerPeriodMs : Timeout.Infinite);
    }

    // ── 11. the transport verbs the UI binds to ─────────────────────────────────────────────────────────────────────
    //
    // Each is one `Post`. They exist so a button binds to a verb rather than composing an `Input` at the call site —
    // and so the frame stamp is put on in exactly one place.

    public static void PlayNow(EntityRef row, EntityId context, QueueCursor cursor, PlayableKind kind = PlayableKind.Audio, int fromMs = 0)
        => Post(Input.Play(row, row.Id, context, cursor, kind, fromMs, FrameNowMs()));

    public enum EpisodeStartKind : byte { Resume, Beginning, Position }
    public readonly record struct EpisodeStart(EpisodeStartKind Kind, int PositionMs = 0);

    /// <summary>Resolve episode intent before loading, including an explicit zero position.</summary>
    public static void PlayEpisode(EntityId id, EntityId context, EpisodeStart start)
    {
        if (id.Kind != EntityKind.Episode || Entities.Current is null) return;
        EntityRef row = Entities.Ref(id);
        if ((new Episode(row.Slot).Flags & EpisodeFlags.Unplayable) != 0) return;
        int ms = start.Kind == EpisodeStartKind.Resume ? EpisodeStartOf(id) : Math.Max(0, start.PositionMs);
        if (start.Kind == EpisodeStartKind.Beginning) ms = 0;
        if (!context.IsEmpty) { PlayContext(context, id, ms); return; }
        Post(new Input(InputKind.Play, row, id, context, QueueCursor.None, ms,
            Input.ExplicitPositionBit, nowMs: FrameNowMs()));
    }

    public static void Next() => Post(Input.Next(FrameNowMs()));
    public static void Previous() => Post(Input.Prev(FrameNowMs()));
    public static void Pause() => Post(Input.Pause(FrameNowMs()));
    public static void Resume() => Post(Input.Resume(FrameNowMs()));
    public static void TogglePlay() => Post(s_state.IsPlaying ? Input.Pause(FrameNowMs()) : Input.Resume(FrameNowMs()));
    public static void SeekTo(int ms) => Post(Input.Seek(ms, FrameNowMs()));
    public static void GoLive() => Post(Input.GoLive(FrameNowMs()));
    public static void SetVolume(float linear01) => Post(Input.Volume(linear01));
    public static void SetShuffle(bool on) => Post(Input.Shuffle(on));
    public static void SetRepeat(RepeatMode mode) => Post(Input.Repeat(mode));
    public static void Stop(StopReason why = StopReason.None) => Post(Input.Stop(why));
    public static void Release(ReleaseCause cause) => Post(Input.Release(cause));

    // ── the per-track format override (ch 01 DATA GAP 14): a persisted `uri=formatId;…` map, newest last, capped
    // (`Track.DrawerRules.FormatOverrides`). Read on the audio open thread, written from the UI: one lock.
    static readonly SettingKey<string> s_formatOverridesKey = new("playback.formatOverrides", "");
    static readonly object s_formatOverridesGate = new();
    static List<KeyValuePair<string, byte>>? s_formatOverrides;

    /// <summary>The wire format pinned for <paramref name="track"/>, or null (the user's default quality).</summary>
    public static byte? FormatOverrideFor(EntityId track)
    {
        if (track.IsEmpty) return null;
        lock (s_formatOverridesGate)
            return Track.DrawerRules.FormatOverrides.Find(
                s_formatOverrides ??= Track.DrawerRules.FormatOverrides.Parse(Platform.Settings.Get(s_formatOverridesKey)), track.Text);
    }

    /// <summary>Pin a wire format for <paramref name="track"/> (null clears it); applies from the track's next open.</summary>
    public static void SetFormatOverride(EntityId track, byte? formatId)
    {
        if (track.IsEmpty) return;
        string text;
        lock (s_formatOverridesGate)
        {
            var map = s_formatOverrides ??= Track.DrawerRules.FormatOverrides.Parse(Platform.Settings.Get(s_formatOverridesKey));
            if (!Track.DrawerRules.FormatOverrides.Put(map, track.Text, formatId)) return;
            text = Track.DrawerRules.FormatOverrides.Serialize(map);
        }
        Platform.Settings.Set(s_formatOverridesKey, text);
    }

    /// <summary>The rung an open asks the ladder for: the pinned format's, else the preferred quality.</summary>
    public static Spotify.Audio.Quality QualityFor(EntityId track)
        => FormatOverrideFor(track) is { } formatId ? Track.DrawerRules.QualityOf(formatId) : Spotify.Audio.PreferredQuality();

    /// <summary>The video placement turned on or off. Owner K's `Video.Host.cs` posts it on the surface's
    /// <c>IsActive</c> edge; the reducer switches hosts at the carried position when the row has a video (G-141).</summary>
    public static void SetVideoPlacement(bool active) => Post(Input.VideoPlacement(active, FrameNowMs()));

    /// <summary>Transfer to a roster row. The core refuses self-to-self (the service answers 400); this refuses an
    /// index that is not a row, so the two together mean the picker cannot send a request that cannot be honoured.</summary>
    public static void TransferTo(int rosterSlot)
    {
        var rows = Devices.Rows;
        if ((uint)rosterSlot >= (uint)rows.Length) return;
        Post(Input.Transfer(rows[rosterSlot].Hash, rosterSlot, FrameNowMs()));
    }

    // ── 12. the reports owner H's hosts post back ───────────────────────────────────────────────────────────────────
    //
    // Named so the audio/video hosts never compose an `Input` either, and so every one of them carries the load epoch
    // it was started for — the drop test in `Step` is the whole of C4 and it cannot work if a caller forgets.

    public static void ReportStarted(uint epoch, int positionMs) => Post(Input.Audio(AudioSignal.Started, epoch, FrameNowMs(), positionMs));
    public static void ReportPosition(uint epoch, int positionMs) => Post(Input.Audio(AudioSignal.Position, epoch, FrameNowMs(), positionMs));
    public static void ReportBuffering(uint epoch, bool refilling)
        => Post(Input.Audio(refilling ? AudioSignal.Buffering : AudioSignal.Buffered, epoch, FrameNowMs()));
    public static void ReportSeeked(uint epoch, int positionMs) => Post(Input.Audio(AudioSignal.Seeked, epoch, FrameNowMs(), positionMs));
    public static void ReportPaused(uint epoch) => Post(Input.Audio(AudioSignal.Paused, epoch, FrameNowMs()));
    public static void ReportResumed(uint epoch) => Post(Input.Audio(AudioSignal.Resumed, epoch, FrameNowMs()));
    public static void ReportStopped(uint epoch) => Post(Input.Audio(AudioSignal.Stopped, epoch, FrameNowMs()));
    public static void ReportFailed(uint epoch, Fault fault) => Post(Input.Audio(AudioSignal.Failed, epoch, FrameNowMs(), (long)fault));
    public static void ReportEnded(uint epoch) => Post(Input.Ended(epoch, FrameNowMs()));
    public static void ReportDuration(uint epoch, int ms) => Post(Input.Duration(ms, epoch));
    public static void ReportFormat(uint epoch, StringId badge) => Post(Input.Format(badge, epoch));
    public static void ReportRecovery(uint epoch, RecoveryKind kind) => Post(Input.Recovering(kind, epoch));
    public static void ReportLiveWindow(in LiveWindow window) => Post(Input.LiveReport(in window, UnixNowMs()));
    public static void ReportSuspend() => Post(Input.Suspend(FrameNowMs()));
    public static void ReportWake() => Post(Input.Wake(FrameNowMs()));
    public static void ReportDeviceLost() => Post(Input.DeviceLost());

    /// <summary>The pump JOINED the prepared row (<paramref name="epoch"/> = the outgoing load's): the reducer advances
    /// without a Load and answers with an adoption (G-100, D4). Replaces the join's <c>Started</c> post.</summary>
    public static void ReportHandedOff(uint epoch, EntityId preparedId, int positionMs)
        => Post(Input.HandedOff(epoch, preparedId, positionMs, FrameNowMs()));

    /// <summary>This load's endgame window opened (fade + 8 s), once per load: time to prepare the next row (G-112).</summary>
    public static void ReportEndingSoon(uint epoch) => Post(Input.Audio(AudioSignal.EndingSoon, epoch, FrameNowMs()));

    /// <summary>The output device changed format under a graph that cannot render on it: reload at the position (G-108).</summary>
    public static void ReportDeviceReload(uint epoch) => Post(Input.Audio(AudioSignal.DeviceReload, epoch, FrameNowMs()));

    /// <summary>The video host found no source for this load's row: demote to audio (G-141).</summary>
    public static void ReportVideoUnavailable(uint epoch) => Post(Input.Audio(AudioSignal.VideoUnavailable, epoch, FrameNowMs()));

    /// <summary>A put-state left the building. Binds the claim to its message id, so the RESPONSE to that put — and
    /// only that put — is the verdict the fence reads (C5).</summary>
    public static void ReportPutSent(uint messageId, bool isActive) => Post(Input.PutSent(messageId, isActive));

    /// <summary>A put-state round trip ended in FAILURE (the accepted case arrives as its response cluster). Without
    /// this a lost response would sit the claim Protected — and audible — until the 5 s window expired.</summary>
    public static void ReportPutFailed(uint messageId) => Post(Input.PutVerdict(messageId, accepted: false));

    // ── 13. test seam ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Put the loop back to a known, empty state. TESTS ONLY — it exists because the whole of this file is
    /// process statics by design (there is one playback session per process), and a fact that ran before must not be
    /// able to reach the one running now.</summary>
    public static void ResetForTests()
    {
        lock (s_gate) { s_head = 0; s_count = 0; s_dropped = 0; s_wakeQueued = false; Array.Clear(s_inbox); }
        s_state = State.Initial;
        s_fx.Clear();
        s_identity = default;
        s_hellos = 0;
        s_hostKind = PlayableKind.Audio;
        s_draining = false;
        s_redrain = false;
        s_queueVersion = 0;
        s_followScope = null;
        s_followIndex = -1;
        s_followVersion = 0;
        s_uidItem = 0;
        s_uid = "";
        s_uids.Clear();
        s_watchId = default;
        s_watchVersion = 0;
        s_identityWaits = 0;
        s_videoId = default;
        s_videoHas = false;
        s_videoGid = StringId.Empty;
        s_videoFacts = new global::Wavee.Video.ConnectVideoFacts();
        s_ensureRowTrackSlots.Clear();
        s_ensureRowEpisodeSlots.Clear();
        ResetContextForTests();
        ResetRemoteForTests();
        ResetWireForTests();
        s_boundScope = Entities.Current;
        Ticker(false);
        Publish();
    }
}
