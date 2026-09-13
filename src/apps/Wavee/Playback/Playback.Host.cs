// ── Playback/Playback.Host.cs ──────────────────────────────────────────────────────────────────────────────────────
// Post loop, signals, Pending, ticker
//
// Role: SHELL
// Owner: G
// Wave: 3
// Budget: 800 lines
// Spec: plan
//
// THE LOOP. `Playback.cs` decides; this file is the only thing that touches the world. Three jobs and no fourth:
//
//   IN     `Post(in Input)` — thread-safe, allocation-free, bounded. It writes the VALUE into a ring and wakes the UI
//          thread with ONE cached delegate; it never runs `Step`. Every other thread in the app (dealer, api, audio
//          pump, OS bridges, module rpc) reaches playback through it and through nothing else (C1/C2).
//   DRAIN  `Drain()` — UI thread, at the top of a frame, inside the engine's own Batch: `Spotify.Connect`'s mailbox
//          first (those results are older), then the ring. One `Step` per value, effects executed ONCE, signals
//          written ONCE. Ten `Next` clicks in one drain are ten `Step`s, one `Load` and one render (C3).
//   OUT    `Execute()` — the effect slots become calls on owner H's audio/OS seams and on `Spotify.Connect`'s glue,
//          each stamped with the epoch it was decided for so the shell can cancel what it has superseded (C4).
//
// THE ONE CLOCK. `Now()` converts the engine's frame stamp to milliseconds ONCE per drain and every input made here
// carries it (memory rule `animations-sample-frame-time`). The core reads no clock at all, which is why a whole
// playback session replays deterministically in a unit test.
//
// THE SEAMS TO OWNER H (Wave 3, `Playback.Audio.cs` / `Playback.Os.cs` — parts of THIS same partial class):
//
//   static class Audio {
//       void Load(EntityRef row, EntityId id, PlayableKind kind, uint epoch, int fromMs);
//       void Stop();  void Pause();  void Resume();
//       void Seek(int ms, uint epoch);
//       void SetVolume(float linear01);
//       void Prepare(EntityRef row, EntityId id);
//   }
//   static class Os { void Publish(in State s); }
//
// `Audio.Load` owns the cancellation: it cancels the previous epoch's token before it opens anything, which is what
// makes nine of ten clicks cost nothing. `Os.Publish` is ONE seam on purpose — the two `SmtcTimelineCoalescer`s live
// inside the bridges, so a card refresh and a timeline tick arrive the same way and the bridges decide the cost.
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

    /// <summary>Unix milliseconds — a DATE, for the two places that genuinely need one: the PUT body's
    /// <c>client_side_timestamp</c> and <see cref="State.TunedInAtMs"/>. Never used for motion or extrapolation.</summary>
    public static Func<long> UnixNowMs { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── 2. the state, the effects, the bounded inbox (C8) ───────────────────────────────────────────────────────────

    static State s_state = State.Initial;
    static Effects s_fx;
    static DeviceIdentity s_identity;

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
    /// <summary>0..1, LINEAR. The cubic taper is the audio host's.</summary>
    public static readonly Signal<float> Volume = new(1f);
    public static readonly Signal<bool> Shuffle = new(false);
    public static readonly Signal<RepeatMode> Repeat = new(RepeatMode.Off);
    /// <summary>The FOLDED enablement — restrictions, phase, error and (while live) the DVR window, decided once.</summary>
    public static readonly Signal<bool> CanSeek = new(false);
    public static readonly Signal<bool> CanSkipNext = new(false);
    public static readonly Signal<bool> CanSkipPrev = new(false);
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
        s_state.Us = DeviceHash(Platform.DeviceId);
        s_identity = new DeviceIdentity(
            Platform.DeviceId,
            Environment.MachineName,                       // what every other client's picker shows for this box
            Spotify.Identity.ClientId,
            Spotify.Identity.AppPlatform,                  // `PrivateDeviceInfo.platform`
            Spotify.Identity.ClientVersion,                // `DeviceInfo.device_software_version`
            SpircVersion);

        if (Platform.Settings.Get(Platform.Keys.RememberVolume))
            s_state.Volume = Math.Clamp(Platform.Settings.Get(Platform.Keys.SavedVolume), 0f, 1f);

        // The dealer's mailbox wakes us; the drain reads it. One closure, made once.
        Spotify.Connect.Wake = static () => ToUi(s_drain);

        Publish();
        StartTicker();
        Log.Info("playback", "boot device=" + Platform.Redact(Platform.DeviceId) + " volume=" + s_state.Volume.ToString("0.00"));
    }

    /// <summary>The live state, for a diagnostics row or a shell that needs the whole value at once. A COPY — nobody
    /// outside this file writes playback state (C1).</summary>
    public static State Snap() => s_state;

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

    // ── 7. the drain (UI thread) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE DRAIN = ONE BATCH = ONE RENDER (C3). Connect's mailbox first — those results are older than
    /// anything posted since — then the ring, then the effects once, then the signals once.</summary>
    static void Drain()
    {
        lock (s_gate) s_wakeQueued = false;

        long now = FrameNowMs();
        DrainConnect(now);

        while (true)
        {
            Input i;
            lock (s_gate)
            {
                if (s_count == 0) break;
                i = s_inbox[s_head];
                s_inbox[s_head] = default;                 // release the references the value holds
                s_head = (s_head + 1) % InboxDepth;
                s_count--;
            }
            Step(ref s_state, in i, ref s_fx);
        }

        Execute();
        Publish();
    }

    /// <summary>Drain `Spotify.Connect`'s bounded mailbox: decode results become INPUTS here and nowhere else. The
    /// buffer each cluster item carries is handed back the moment its fold has read it — hold a
    /// <see cref="TextRef"/> past that and the pool starves.</summary>
    static void DrainConnect(long now)
    {
        while (Spotify.Connect.TryDequeue(out Spotify.Connect.Item item))
        {
            try
            {
                if (item.Kind == Spotify.Connect.ItemKind.Cluster) FoldCluster(in item, now);
                else if (item.Kind == Spotify.Connect.ItemKind.RemoteCommand)
                {
                    var input = Input.Controller(in item.Command, now);
                    Step(ref s_state, in input, ref s_fx);
                }
            }
            finally { Spotify.Connect.Release(in item); }
        }
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
            d.NoPrev, d.NoNext, d.NoSeek);

        var input = Input.Cluster(in frame, in remote, now);
        Step(ref s_state, in input, ref s_fx);
        s_state.ActiveDeviceSlot = Devices.SlotOf(s_state.ActiveDevice);
    }

    /// <summary>A wire uri → a packed identity. UI thread, so the text form may intern (C1).</summary>
    static EntityId Identify(ReadOnlySpan<byte> utf8) => utf8.IsEmpty ? default : EntityId.Parse(utf8);

    // ── 8. out: Execute (the effect slots, read ONCE) ───────────────────────────────────────────────────────────────

    static void Execute()
    {
        if (!s_fx.Any) { s_fx.Clear(); return; }

        // Order matters exactly once: a host that must STOP before the next stream opens has to stop first.
        if (s_fx.Stop) Audio.Stop();
        if (s_fx.Load)
        {
            Pending.Load.Value = true;
            Audio.Load(s_fx.LoadRow, s_fx.LoadId, s_fx.LoadKind, s_fx.LoadEpoch, s_fx.LoadFromMs);
        }
        if (s_fx.PauseHost) Audio.Pause();
        if (s_fx.ResumeHost) Audio.Resume();
        if (s_fx.Seek) Audio.Seek(s_fx.SeekMs, s_fx.SeekEpoch);
        if (s_fx.Volume)
        {
            Audio.SetVolume(s_fx.VolumeValue);
            if (Platform.Settings.Get(Platform.Keys.RememberVolume))
                Platform.Settings.Set(Platform.Keys.SavedVolume, s_fx.VolumeValue);
        }
        if (s_fx.PrepareNext) Audio.Prepare(s_fx.NextRow, s_fx.NextId);
        if (s_fx.Fetch) EnsureRow(s_fx.FetchId);
        if (s_fx.PublishState) Announce(s_fx.PublishWhy);
        if (s_fx.SendRemote) SendRemote();
        if (s_fx.Transfer) SendTransfer();
        if (s_fx.Smtc || s_fx.SmtcTimeline) Os.Publish(in s_state);
        if (s_fx.Snapshot) SnapshotSession(in s_state);
        s_fx.Clear();
    }

    /// <summary>Make sure the identity the core is about to paint HAS a row, and ask the catalog for its hot group at
    /// playback priority. Allocating the slot is what turns a foreign device's uri into a handle the bar can bind.</summary>
    static void EnsureRow(EntityId id)
    {
        if (id.IsEmpty) return;
        Table? table = Entities.TableFor(id.Kind);
        if (table is null) return;
        int slot = table.Slot(id);
        if (slot <= Table.None) return;
        if (s_state.Current.IsNone && id.Equals(s_state.CurrentId)) s_state.Current = new EntityRef(id.Kind, slot);
        if (id.Kind == EntityKind.Track) Entities.Ensure(new Track(slot), TrackFields.Identity, FetchPriority.Playback);
        else if (id.Kind == EntityKind.Episode) Entities.Ensure(new Episode(slot), EpisodeFields.Identity, FetchPriority.Playback);
    }

    /// <summary>Announce our state to the connect-state service. The SNAPSHOT is captured here, on the UI thread,
    /// inside the drain: the glue's debounce and its PUT both run on an api thread and may not read
    /// <see cref="State"/> (C1/C9).</summary>
    static void Announce(PublishReason why)
    {
        var snapshot = Snapshot.Of(in s_state, in s_identity, why, 0, UnixNowMs(), FrameNowMs(), CurrentUid());
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

    /// <summary>The server-minted queue item id for the row on the deck, as text. "" when this session minted the
    /// queue itself, which is what the wire expects for a locally-started context.</summary>
    static string CurrentUid() => "";

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
        string endpoint = Endpoint(s_fx.RemoteCmd);
        if (endpoint.Length == 0) return;
        string valueName = s_fx.RemoteCmd switch
        {
            RemoteCmd.SeekTo => "position",
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

    /// <summary>Persist enough to restore the deck at launch. A partial seam because the session document is the
    /// shell's (`Shell.Host.cs`, Wave 4): the VOLUME is persisted here because it is a playback preference with a key
    /// of its own, and nothing else in this file is allowed to know where a json file lives.</summary>
    static partial void SnapshotSession(in State s);

    // ── 9. publish (one write per signal, once per drain) ───────────────────────────────────────────────────────────

    static void Publish()
    {
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
        Volume.Value = s_state.Volume;
        Shuffle.Value = s_state.Shuffle;
        Repeat.Value = s_state.Repeat;
        CanSeek.Value = s_state.CanSeek;
        CanSkipNext.Value = s_state.CanSkipNext;
        CanSkipPrev.Value = s_state.CanSkipPrev;
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

        Ticker(s_state.Phase == Phase.Playing || s_state.Own.Claim == ClaimPhase.Protected);
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

    /// <summary>Run the ticker only while it has something to do: a playing deck to extrapolate, or a protected claim
    /// whose 5 s window has to be able to expire. An idle Wavee posts nothing and renders nothing.</summary>
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
    public static void ReportSuspend() => Post(new Input(InputKind.Suspend, nowMs: FrameNowMs()));
    public static void ReportWake() => Post(new Input(InputKind.Resume_, nowMs: FrameNowMs()));
    public static void ReportDeviceLost() => Post(Input.DeviceLost());

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
        Ticker(false);
        Publish();
    }
}
