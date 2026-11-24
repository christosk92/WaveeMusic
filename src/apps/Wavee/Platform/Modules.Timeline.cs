// ── Platform/Modules.Timeline.cs ───────────────────────────────────────────────────────────────────────────────────────────
// The module observer hook (#154 G2c): the app's playback timeline, sent to every ENABLED module that declares the `timeline`
// capability as `playback/timeline` notifications (Wavee.Sdk TimelineState).
//
// WHAT GOES ON THE WIRE. An anchor, never a stream of positions: "at Stopwatch tick T the playhead was at P µs, moving at rate R".
// QPC is one counter for the whole machine, so a module extrapolates locally and the stdio transport's delay (milliseconds, no
// bound) drops out. One notification per `Playback.Timeline.Changed` (a track, a seek, a pause, a resume, a transition, a new
// duration) and a 1 s keepalive while something plays; none while nothing does.
//
// ENABLED. 0.4 has no general per-module switch: `TimelineObserverEnabled` answers per module id (the timecode settings map
// `wavee.clx` to their CLX switch). An enabled observer holds a lease, so the idle sweep leaves it running, and it is started by
// the first state it is sent; turning it off releases the lease and stops the process.
//
// THREADS. `Changed` arrives on the pump chain and the keepalive on a timer; the state is BUILT on the UI thread (the title,
// artists and album come from the entity tables, which have one writer) and SENT from the pool. Allocates a JSON message per
// send: a change or a second, never a frame.

using System.Diagnostics;
using System.Globalization;
using System.Threading;

using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee;

public static partial class Modules
{
    static TimelineFanOut? s_timeline;

    /// <summary>Which observer modules are enabled, by module id (0.4: the timecode settings answer for <c>wavee.clx</c>). Null
    /// enables none. Call <see cref="RefreshTimelineObservers"/> after the answer changes.</summary>
    public static Func<string, bool>? TimelineObserverEnabled { get; set; }

    /// <summary>Re-read <see cref="TimelineObserverEnabled"/>: start sending to newly enabled observers, stop the disabled ones.</summary>
    public static void RefreshTimelineObservers() => s_timeline?.Refresh();

    /// <summary>The fan-out's counters for the diagnostics page: notifications sent, and the observers enabled now.</summary>
    public static (long Sent, int Observers) TimelineFanOutStats => s_timeline is { } f ? (f.Sent, f.ObserverCount) : (0, 0);

    /// <summary>The bundled CLX module's id: the one observer 0.4 enables, by the timecode settings' CLX switch.</summary>
    public const string ClxModuleId = "wavee.clx";

    static void StartTimelineFanOut(ModuleHost host)
    {
        TimelineObserverEnabled ??= static id => string.Equals(id, ClxModuleId, StringComparison.OrdinalIgnoreCase)
                                                 && Platform.Settings.Get(Platform.Keys.TimecodeClxEnabled);
        for (int i = 0; i < host.Installed.Count; i++)
            if (ModuleCapabilities.Declares(host.Installed[i].Manifest, ModuleCapabilities.Timeline))
            {
                s_timeline = new TimelineFanOut(host);
                return;
            }
    }

    static void StopTimelineFanOut()
    {
        s_timeline?.Dispose();
        s_timeline = null;
    }

    /// <summary>The fan-out itself (see the file header).</summary>
    public sealed class TimelineFanOut : IDisposable
    {
        readonly ModuleHost _host;
        readonly Lock _gate = new();
        readonly Dictionary<string, IDisposable> _leases = new(StringComparer.OrdinalIgnoreCase);   // enabled observers, by id
        readonly Timer _keepalive;
        readonly Action _onChanged;
        bool _timerOn, _sentNothing, _disposed;
        long _sent;

        public TimelineFanOut(ModuleHost host)
        {
            _host = host;
            _keepalive = new Timer(static s => ((TimelineFanOut)s!).OnKeepalive(), this, Timeout.Infinite, Timeout.Infinite);
            _onChanged = () => Send("change");
            Playback.Timeline.Changed += _onChanged;
            Refresh();
        }

        public long Sent => Interlocked.Read(ref _sent);
        public int ObserverCount { get { lock (_gate) return _leases.Count; } }

        /// <summary>Re-read which observers are enabled: lease and start the new ones (with the current state), release and stop
        /// the ones turned off, and run the keepalive only while any is enabled.</summary>
        public void Refresh()
        {
            List<ModuleProcess>? stop = null;
            bool added = false;
            lock (_gate)
            {
                if (_disposed) return;
                Func<string, bool>? enabled = TimelineObserverEnabled;
                foreach (InstalledModule m in _host.Installed)
                {
                    if (!ModuleCapabilities.Declares(m.Manifest, ModuleCapabilities.Timeline)) continue;
                    bool on = enabled?.Invoke(m.Id) == true;
                    bool had = _leases.ContainsKey(m.Id);
                    if (on && !had)
                    {
                        _leases[m.Id] = _host.Process(m).AcquireLease("observer");
                        added = true;
                        Log.Info("module." + m.Id, "timeline observer on");
                    }
                    else if (!on && had)
                    {
                        _leases[m.Id].Dispose();
                        _leases.Remove(m.Id);
                        (stop ??= []).Add(_host.Process(m));
                        Log.Info("module." + m.Id, "timeline observer off");
                    }
                }
                bool any = _leases.Count > 0;
                if (any != _timerOn)
                {
                    _timerOn = any;
                    _keepalive.Change(any ? 1000 : Timeout.Infinite, any ? 1000 : Timeout.Infinite);
                }
            }
            if (stop is not null)
                foreach (ModuleProcess p in stop) _ = StopQuietlyAsync(p);
            if (added) Send("enable", force: true);
        }

        void OnKeepalive()
        {
            // A second passed: only while something plays (a held or empty timeline already said so once, on its change).
            if (Playback.Timeline.TryRead(out Playback.Timeline.Snapshot s) && s.Playing) Send("keepalive");
        }

        void Send(string why, bool force = false)
        {
            lock (_gate) if (_disposed || _leases.Count == 0) return;
            Playback.ToUi(() =>
            {
                TimelineState state = Build();
                List<ModuleProcess> targets;
                lock (_gate)
                {
                    // "Nothing plays" is said once, not every change that leaves it nothing.
                    bool nothing = state.Uri is null;
                    if (nothing && _sentNothing && !force) return;
                    _sentNothing = nothing;
                    targets = new List<ModuleProcess>(_leases.Count);
                    foreach (InstalledModule m in _host.Installed)
                        if (_leases.ContainsKey(m.Id)) targets.Add(_host.Process(m));
                }
                foreach (ModuleProcess p in targets) _ = NotifyQuietlyAsync(p, state, why);
            });
        }

        async Task NotifyQuietlyAsync(ModuleProcess process, TimelineState state, string why)
        {
            try
            {
                await process.NotifyAsync(ModuleMethods.Timeline, state, SdkJsonContext.Default.TimelineState, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
                if (why != "keepalive")
                    Log.Info("module." + process.Module.Id, "timeline " + why + " uri=" + (state.Uri ?? "-") + " posMs="
                        + (state.PositionMicros / 1000).ToString(CultureInfo.InvariantCulture) + " playing=" + (state.Playing ? 1 : 0)
                        + " epoch=" + state.Epoch.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Log.Info("module." + process.Module.Id, "timeline notify failed: " + ex.Message); }
        }

        static async Task StopQuietlyAsync(ModuleProcess p)
        {
            try { await p.StopAsync("observer disabled").ConfigureAwait(false); }
            catch (Exception ex) { Log.Info("module." + p.Module.Id, "observer stop failed: " + ex.Message); }
        }

        /// <summary>UI thread: the composed timeline plus the entity tables' text for its track.</summary>
        static TimelineState Build()
        {
            long freq = Stopwatch.Frequency;
            if (!Playback.Timeline.TryRead(out Playback.Timeline.Snapshot s))
                return new TimelineState(null, null, null, null, 0, Stopwatch.GetTimestamp(), freq, 0, 1.0, false, 0, null, null);
            (string? title, string? artist, string? album) = TextFor(s.Track);
            TimelineTransition? transition = (s.State & Playback.Timeline.Flags.Transition) == 0 ? null
                : new TimelineTransition(s.Incoming.Text, Ticks(s.TransitionStartQpc100ns, freq), Ticks(s.TransitionEndQpc100ns, freq),
                    s.MasterVoice == Playback.Timeline.Master.Outgoing ? "outgoing" : "incoming");
            return new TimelineState(s.Track.Text, title, artist, album, s.PositionMicros, Ticks(s.AnchorQpc100ns, freq), freq,
                s.DurationMs, s.Rate, s.Playing, s.Epoch, null, transition);

            static long Ticks(long qpc100ns, long freq) => (long)(qpc100ns * (freq / 1e7));
        }

        /// <summary>UI thread: the title, the artists (joined) and the album or show of the playing item; nulls when the tables do
        /// not know it yet.</summary>
        static (string?, string?, string?) TextFor(EntityId id)
        {
            if (id.IsEmpty || Entities.Current is null) return (null, null, null);
            EntityRef row = Entities.Ref(id);
            if (row.IsNone) return (null, null, null);
            if (row.Kind == EntityKind.Track)
            {
                var t = new Track(row.Slot);
                if (!t.IsValid || !t.Knows(TrackFields.Identity)) return (null, null, null);
                ReadOnlySpan<int> artists = t.ArtistSlots;
                string artist = "";
                for (int i = 0; i < artists.Length; i++) artist += (i > 0 ? ", " : "") + new Artist(artists[i]).Name;
                return (NullIfEmpty(t.Title), NullIfEmpty(artist), t.AlbumSlot > 0 ? NullIfEmpty(t.Album.Title) : null);
            }
            if (row.Kind == EntityKind.Episode)
            {
                var e = new Episode(row.Slot);
                if (!e.IsValid) return (null, null, null);
                string show = e.ShowSlot > 0 && new Show(e.ShowSlot).IsValid ? new Show(e.ShowSlot).Title : "";
                return (e.Knows(EpisodeFields.Title) ? NullIfEmpty(e.Title) : null, NullIfEmpty(show), null);
            }
            return (null, null, null);

            static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
        }

        public void Dispose()
        {
            List<IDisposable> leases;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                leases = [.. _leases.Values];
                _leases.Clear();
            }
            Playback.Timeline.Changed -= _onChanged;
            _keepalive.Dispose();
            foreach (IDisposable l in leases) l.Dispose();
        }
    }
}
