// ── Shell/Canvas.cs — Spotify Canvas: when to ask, the one silent looping player, and which surface shows it ─────────────────────────────
//
// Role: SHELL. The record and its commit are Entities/Canvas.cs; the wire is Spotify/Spotify.Decode.Canvas.cs; the files on disk are
// Shell/Canvas.Files.cs; the layer a surface stacks over its art is Shell/Canvas.UI.cs.
//
// ASK ONLY WHEN NEEDED. The observer asks kind 16 for the PLAYING track and the next two of the queue (UserQueue + NextUp), in ONE edge
// batch (one POST), and nothing else: no list, page or card ever asks. A row past its cache TTL is asked again (the metadata cache then
// does the ETag round trip); a row the disk index holds is landed from it first (offline: up to its offline TTL). Only the playing
// track's video and the next one's are downloaded (the next only off a metered network).
//
// ONE PLAYER. Each MediaPlayer owns a Media Foundation engine (tens of MB), so Canvas has exactly one, muted and looping, and at most one
// surface presents it: the highest-priority surface that is mounted and active (Stage > Deck) is the OWNER; the others show the
// poster. A new owner mounts its element only after the old one let go (`Bound`), so two elements never present one player.
//
// WHEN IT DECODES. The source is open only while an owner exists and the setting is on; it PLAYS only while the owner's element can be
// seen (`InViewChanged`: not parked, not minimized, not scrolled out), the window is not occluded, audio is playing and motion is not
// reduced; otherwise it is paused on its frame. No owner, the setting off, a music video on the video host, or no canvas: the session is
// stopped (the decoder released; the engine itself is returned to the backend's warm slot and disposed by it after 30 s).

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace Wavee;

public static partial class CanvasVideo
{
    /// <summary>Settings ▸ Playback ▸ Video ▸ Show Canvas. Default ON (Spotify's own default).</summary>
    public static readonly SettingKey<bool> ShowKey = new("playback.canvas", true);

    /// <summary>Bumped by the one writer (<see cref="SetEnabled"/>) after the store has the value.</summary>
    public static readonly Signal<int> Epoch = new(0);

    /// <summary>Reactive read of the setting.</summary>
    public static bool Enabled()
    {
        _ = Epoch.Value;
        return Platform.Settings.Get(ShowKey);
    }

    /// <summary>The settings row's writer: persist, then bump so every surface re-reads on the same frame.</summary>
    public static bool SetEnabled(bool on)
    {
        Platform.Settings.Set(ShowKey, on);
        Epoch.Value = Epoch.Peek() + 1;
        return true;
    }

    /// <summary>Where a canvas can show, by priority: the higher value owns the one player when several are mounted. The now-playing
    /// cover is not one: "Cover" is the album art alone, and a canvas in the panel is the Canvas player style's: edge to edge behind the panel
    /// at rest (Bleed), in the picker's Canvas cell while picking (Deck).</summary>
    public enum Surface : byte { None = 0, Bleed = 1, Deck = 2, Stage = 3 }

    /// <summary>The playing track's canvas as the surfaces draw it. <see cref="Shows"/> false: draw nothing (the surface is exactly what it
    /// is without Canvas). <see cref="File"/> is empty until the mp4 is on disk (the poster shows meanwhile).</summary>
    public readonly record struct Now(int Track, string Uri, string Poster, string File, bool Shows);

    /// <summary>UI thread only (C1).</summary>
    public static readonly Signal<Now> Current = new(default);

    /// <summary>The surface that presents the player now (the highest claimed), and the one whose element is mounted.</summary>
    public static readonly Signal<Surface> Owner = new(Surface.None), Bound = new(Surface.None);

    /// <summary>Bumps when an open lands (the observer then applies play / pause to the new session).</summary>
    static readonly Signal<int> Opened = new(0);

    /// <summary>The surface whose element has PRESENTED a frame of the open canvas (its <c>SurfacePresented</c>), else None. From then on
    /// the video covers its box entirely, so what the surface drew under it (the poster, the stage's backdrop) is not drawn at all.</summary>
    public static readonly Signal<Surface> Presented = new(Surface.None);

    /// <summary>Reactive: is the stage's backdrop covered by a presented canvas? The stage templates bind their backdrop's visibility to
    /// its negation (<see cref="StageUncovered"/>), so a covered backdrop neither rasters nor composites.</summary>
    public static readonly Func<bool> StageUncovered = static () => Presented.Value != Surface.Stage;

    /// <summary>The owner's element can be seen (its <c>InViewChanged</c>).</summary>
    public static readonly Signal<bool> InView = new(false);

    /// <summary>The ONE canvas player (lazy: nothing is built until a canvas first shows).</summary>
    public static MediaPlayer Player => s_player ??= Build();

    static MediaPlayer? s_player;
    static readonly int[] s_claims = new int[4];

    /// <summary>The observer asked the player to play (true) or holds it paused / stopped (false). Diagnostics.</summary>
    public static bool Running { get; private set; }

    /// <summary>The one player's engine state, or "none" before it was built. Diagnostics (the inspector's <c>status</c>).</summary>
    public static string PlayerState() => s_player is { } p ? p.State.Peek().ToString() : "none";

    /// <summary>The one player's last error, or "". Diagnostics.</summary>
    public static string PlayerError() => s_player?.Error.Peek() is { } e ? e.ToString() : "";

    /// <summary>The playing canvas's natural size, 0x0 before its metadata. Diagnostics.</summary>
    public static SizeI PlayerNatural() => s_player is { } p ? p.NaturalSize.Peek() : default;

    // The canvas's OWN Media Foundation backend, process-lifetime (Playback.Video's ClearBackend reasoning, F190): a player rebuilt
    // over a fresh backend would pay MFStartup, a D3D11 video device and an engine cold each time, and a second player over the music
    // video's backend would get a throwaway engine. Explicit, so the canvas never depends on the host's default registrar (the
    // headless inspector has none).
    static FluentGpu.Media.Windows.MfMediaPlayer? s_backend;

    static MediaPlayer Build()
    {
        var p = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, s_backend ??= new FluentGpu.Media.Windows.MfMediaPlayer()).Build();
        p.SetMuted(true);
        return p;
    }

    /// <summary>A surface is mounted and active: it may own the player. The returned action withdraws the claim. UI thread.</summary>
    public static Action Claim(Surface surface)
    {
        s_claims[(int)surface]++;
        Reown();
        return () =>
        {
            s_claims[(int)surface] = Math.Max(0, s_claims[(int)surface] - 1);
            Reown();
        };
    }

    static void Reown()
    {
        var owner = Surface.None;
        for (int i = s_claims.Length - 1; i > 0; i--)
            if (s_claims[i] > 0) { owner = (Surface)i; break; }
        if (Owner.Peek() != owner) Owner.Value = owner;
    }

    /// <summary>The owner's element mounted (it presents now) / unmounted.</summary>
    internal static void BindSurface(Surface s) => Bound.Value = s;
    internal static void UnbindSurface(Surface s)
    {
        if (Bound.Peek() == s) Bound.Value = Surface.None;
        if (Presented.Peek() == s) Presented.Value = Surface.None;
        if (Owner.Peek() == s || Owner.Peek() == Surface.None) InView.Value = false;
    }

    /// <summary>The owner element's in-view report.</summary>
    internal static void NoteInView(bool inView) => InView.Value = inView;

    /// <summary>Does this surface mount the element now: it owns the player and the previous presenter has let go.</summary>
    public static bool Presents(Surface s)
    {
        var bound = Bound.Value;
        return Owner.Value == s && (bound == Surface.None || bound == s);
    }

    // ── the observer: the demand, the record, the player ────────────────────────────────────────────────────────────────

    /// <summary>How many queued tracks after the playing one are asked for their canvas.</summary>
    public const int Lookahead = 2;

    /// <summary>The leaf that runs the effects (mounted once, in <c>Video.PipLayer</c>).</summary>
    internal sealed class Observer : Component
    {
        readonly int[] _ask = new int[1 + Lookahead];
        readonly int[] _refresh = new int[1 + Lookahead];
        string _open = "";
        bool _playing;
        int _openedSeen;

        public override Element Render()
        {
            var post = UsePost();
            Files.Install(post);
            var hooks = UseContext(InputHooks.Current);

            // 1. THE DEMAND: the playing track and the next ones, one batch.
            UseSignalEffect(Demand);

            // 2. THE RECORD: what the surfaces draw for the playing track, and what goes to disk.
            UseSignalEffect(Publish);

            // 3. THE PLAYER: open / play / pause / stop.
            UseSignalEffect(() => Drive(hooks.WindowOccluded?.Value ?? false));

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        void Demand()
        {
            _ = Entities.ScopeEpoch.Value;
            var cur = Playback.Current.Value;
            if (Entities.Current is not { } scope) return;
            _ = scope.Edges.Queue.Changed.Value;
            _ = scope.Edges.TrackCanvas.Changed.Value;   // a stale row re-asks after it lands
            _ = Files.IndexEpoch.Value;                  // the disk index loaded
            if (!Enabled() || cur.Kind != EntityKind.Track || cur.IsNone) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int asks = 0, refreshes = 0;
            Consider(cur.Slot, now, ref asks, ref refreshes);
            if (Queue.UpNext(out int start, out int length))
                for (int i = start, n = 0; i < start + length && n < Lookahead; i++)
                {
                    var r = Queue.RefAt(i);
                    if (r.Kind != EntityKind.Track || r.IsNone) continue;
                    Consider(r.Slot, now, ref asks, ref refreshes);
                    n++;
                }
            if (asks > 0) Entities.EnsureEdge(FetchEdge.TrackCanvas, _ask.AsSpan(0, asks), priority: FetchPriority.Prefetch);
            for (int i = 0; i < refreshes; i++) Entities.RefreshEdge(FetchEdge.TrackCanvas, _refresh[i], FetchPriority.Prefetch);
        }

        void Consider(int slot, long now, ref int asks, ref int refreshes)
        {
            var t = new Track(slot);
            if (!t.IsValid || t.Uri.Provider != EntityProvider.Spotify) return;
            var rows = Entities.Current.Edges.TrackCanvas;
            if (rows.State(slot) == EdgeState.Unknown && !rows.WasAsked(slot, 0))
            {
                // The disk copy first: fresh, it answers; offline, it answers up to its offline TTL (and is re-asked once online).
                if (Files.Indexed(t.Uri.Text) is { } held && (!held.Stale(now) || !Spotify.Current.IsOnline) && held.OfflineFresh(now))
                {
                    Entities.LandCanvas(slot, held);
                    Entities.Current.PublishAll();
                    return;
                }
                if (Platform.Args.Fake) return;   // the fake catalog seeds every canvas it has; nothing goes to the wire
                _ask[asks++] = slot;
                return;
            }
            if (!Platform.Args.Fake && Spotify.Current.IsOnline && Entities.CanvasDue(slot, now) && refreshes < _refresh.Length)
                _refresh[refreshes++] = slot;
        }

        void Publish()
        {
            _ = Entities.ScopeEpoch.Value;
            var cur = Playback.Current.Value;
            bool on = Enabled();
            bool videoHost = Playback.VideoActive.Value;   // a music video plays on the video host: it, not the canvas, is the picture
            _ = Files.Epoch.Value;
            if (Entities.Current is not { } scope) { Set(default); return; }
            _ = scope.Edges.TrackCanvas.Changed.Value;
            _ = scope.Edges.Queue.Changed.Value;
            if (cur.Kind != EntityKind.Track || cur.IsNone) { Set(default); return; }
            var t = new Track(cur.Slot);
            if (Entities.TryCanvas(cur.Slot, out var c))
            {
                string url = Entities.Strings.Resolve(c.Url);
                Files.Remember(t.Uri.Text, c);
                string file = Files.Ensure(url, priority: true);
                Set(on && !videoHost
                    ? new Now(cur.Slot, t.Uri.Text, Entities.Strings.Resolve(c.Poster), file, true)
                    : default);
            }
            else Set(default);

            // The next one's file, ahead of its track change (never on a metered network).
            if (!on || Platform.Network.Metered.Value || !Queue.UpNext(out int start, out int length)) return;
            for (int i = start; i < start + length; i++)
            {
                var r = Queue.RefAt(i);
                if (r.Kind != EntityKind.Track || r.IsNone) continue;
                if (Entities.TryCanvas(r.Slot, out var next))
                {
                    Files.Remember(new Track(r.Slot).Uri.Text, next);
                    Files.Ensure(Entities.Strings.Resolve(next.Url), priority: false);
                }
                break;
            }
        }

        static void Set(in Now now)
        {
            if (!Current.Peek().Equals(now)) Current.Value = now;
        }

        void Drive(bool occluded)
        {
            var now = Current.Value;
            var owner = Owner.Value;
            bool inView = InView.Value;
            bool playing = Playback.IsPlaying.Value;
            bool reduced = Design.Reduced;
            if (!now.Shows || now.File.Length == 0 || owner == Surface.None)
            {
                if (_open.Length > 0 && s_player is { } idle)
                {
                    idle.Stop();
                    Log.Info("canvas", "canvas.player stop owner=" + owner + " shows=" + now.Shows);
                }
                _open = "";
                _playing = false;
                Running = false;
                return;
            }
            var player = Player;
            if (_open != now.File)
            {
                _open = now.File;
                _playing = false;
                if (Log.IsEnabled(WaveeLogLevel.Info))
                    Log.Info("canvas", "canvas.player open track=" + now.Uri + " owner=" + owner + " file=" + System.IO.Path.GetFileName(now.File));
                Open(player, now.File);
            }
            bool run = inView && playing && !occluded && !reduced;
            int opened = Opened.Value;                 // an open landed: apply the run state to the new session
            if (run == _playing && opened == _openedSeen) return;
            _openedSeen = opened;
            if (run != _playing && Log.IsEnabled(WaveeLogLevel.Info))
                Log.Info("canvas", "canvas.player " + (run ? "play" : "pause") + " inView=" + inView + " playing=" + playing + " occluded=" + occluded
                    + " reduced=" + reduced);
            _playing = run;
            Running = run;
            _ = run ? player.PlayAsync() : player.PauseAsync();
        }

        static async void Open(MediaPlayer player, string file)
        {
            try
            {
                player.SetMuted(true);
                await player.OpenAsync(MediaSource.FromFile(file).Loop(), new MediaOpenOptions { StartPaused = true });
                Files.Post(static () =>
                {
                    s_player?.SetMuted(true);
                    Opened.Value = Opened.Peek() + 1;
                });
            }
            catch (Exception ex)
            {
                Log.Warn("canvas", "canvas.open failed file=" + System.IO.Path.GetFileName(file) + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
