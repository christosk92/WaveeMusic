// ── Screens/Diagnostics.Inspect.Media.cs ───────────────────────────────────────────────────────────────────────────────
// `mount media=fake`: a fake video and audio source whose costs you set, so a hiccup from a canvas video or an audio preview starting can
// be reproduced headless. One backend (`FakeMedia`) stands for Media Foundation everywhere a player is built: the Spotify Canvas player
// (CanvasVideo.UseInspectBackend) and every MediaPlayer built without its own backend (MediaRouter's default registrar, PcmAudio and
// MfVideoOrFile: the shelf's 30 s audio preview and its tile videos when they build players that way).
//
// THE KNOBS (mount args): mediaOpenMs (the open's latency to its first frame, on the 16 ms frame clock; metadata at half of it),
// mediaStartCostMs (a synchronous UI-thread stall inside the open call, where a real engine's creation blocks: the start hiccup),
// mediaFrameCostMs (UI-thread work in every pump while it plays: a per-frame cost). The costs are real busy time on the calling thread, so
// `perf` measures them in whatever phase the app opened or pumped from. Each open logs `[inspect.media] open #n`: `status log=inspect.media`.
//
// Role: SHELL (UI thread; the inspect host's frame clock)
// Spec: docs/guide/agent-inspect.md "media=fake"; the skill's verify/debug-home.md.

using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Media;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        FakeMedia? _fakeMedia;

        /// <summary>The fake media backend, installed for one mount (ClearLeaks ends it).</summary>
        sealed class FakeMedia : IMediaBackend, IDisposable
        {
            readonly FluentGpu.Hosting.AppHost _host;
            readonly Action<FluentGpu.Hosting.FrameStats> _onFrame;
            readonly List<Session> _live = [];
            public readonly int OpenMs;
            public readonly float StartCostMs, FrameCostMs;
            public int Opens;
            long _nowMs;

            public MediaCapabilities Capabilities { get; } = new(true, false, false);

            public FakeMedia(FluentGpu.Hosting.AppHost host, int openMs, float startCostMs, float frameCostMs)
            {
                _host = host;
                OpenMs = Math.Clamp(openMs, 0, 60_000);
                StartCostMs = Math.Clamp(startCostMs, 0f, 2_000f);
                FrameCostMs = Math.Clamp(frameCostMs, 0f, 500f);
                _onFrame = OnFrame;
                host.FrameCompleted += _onFrame;
                CanvasVideo.UseInspectBackend(this);
                MediaRouter.SetDefaultRegistrar(r =>
                {
                    r.RegisterDefault(MediaKind.MfVideoOrFile, () => this);
                    r.RegisterDefault(MediaKind.PcmAudio, () => this);
                });
            }

            public void Dispose()
            {
                _host.FrameCompleted -= _onFrame;
                CanvasVideo.UseInspectBackend(null);
                MediaRouter.SetDefaultRegistrar(null);
                _live.Clear();
            }

            /// <summary>Busy-wait <paramref name="ms"/> on the calling thread: real time a frame's phases measure.</summary>
            static void Spin(float ms)
            {
                if (ms <= 0f) return;
                long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
                while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(32);
            }

            public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
            {
                Opens++;
                Spin(StartCostMs);   // the start hiccup: a synchronous engine creation on the thread that opened
                var session = new Session(this, (nuint)(0x6D00 + Opens));
                _live.Add(session);
                Log.Info("inspect.media", "[inspect.media] open #" + Opens + " startCostMs=" + StartCostMs + " openMs=" + OpenMs);
                return ValueTask.FromResult<IMediaSession>(session);
            }

            void OnFrame(FluentGpu.Hosting.FrameStats _)
            {
                _nowMs += 16;
                for (int i = _live.Count - 1; i >= 0; i--)
                    if (_live[i].Disposed) _live.RemoveAt(i);
                    else _live[i].Tick(_nowMs);
            }

            sealed class Session(FakeMedia owner, nuint handle) : IMediaSession, IVideoSurfaceSession, IVideoPumpSource
            {
                static readonly SizeI Natural = new(288, 512);   // the canvas's portrait loop
                MediaSignalSink? _sink;
                long _startMs = -1;
                bool _meta, _framed, _paused = true;
                public bool Disposed;
                public event Action? PumpRequested;

                public void ConnectSignals(MediaSignalSink sink)
                {
                    _sink = sink;
                    sink.State(PlaybackState.Opening);
                    sink.ExpectedNaturalSize(Natural);
                }

                public void Tick(long nowMs)
                {
                    if (_sink is not { } sink) return;
                    if (_startMs < 0) _startMs = nowMs;
                    long t = nowMs - _startMs;
                    if (!_meta && t >= owner.OpenMs / 2)
                    {
                        _meta = true;
                        sink.Duration(TimeSpan.FromSeconds(30));
                        sink.State(PlaybackState.Buffering);
                    }
                    if (!_framed && t >= owner.OpenMs)
                    {
                        _framed = true;
                        sink.NaturalSize(Natural);
                        sink.VideoSurface(new FluentGpu.Pal.VideoSurfaceId(1));
                        sink.State(_paused ? PlaybackState.Paused : PlaybackState.Playing);
                    }
                    if (_meta) PumpRequested?.Invoke();   // one pump per frame while it lives: the per-frame cost lands in it
                }

                public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
                {
                    if (_framed && !_paused) Spin(owner.FrameCostMs);   // the per-frame cost, inside the frame's video pump
                    if (!binding.IsValid || !_framed || videoRect.W <= 0f || videoRect.H <= 0f) return;
                    binding.Bind(handle);
                    binding.SetStreamSize(Natural, pinned: true);
                    binding.Place(videoRect);
                }

                public ValueTask PlayAsync() { _paused = false; if (_framed) _sink?.State(PlaybackState.Playing); return ValueTask.CompletedTask; }
                public ValueTask PauseAsync() { _paused = true; if (_framed) _sink?.State(PlaybackState.Paused); return ValueTask.CompletedTask; }
                public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
                public void SetRate(double rate) { }
                public void SetVolume(double volume) { }
                public void SetMuted(bool muted) { }
                public VideoDelivery Video => _meta ? new VideoDelivery.CompositedSurface(new FluentGpu.Pal.VideoSurfaceId(1), Natural, IsHdr: false) : VideoDelivery.None;
                public ValueTask DisposeAsync() { Disposed = true; _sink = null; return ValueTask.CompletedTask; }
            }
        }
    }
}
