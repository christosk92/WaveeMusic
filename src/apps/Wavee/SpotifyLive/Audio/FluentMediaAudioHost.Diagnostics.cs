using FluentGpu.Media;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio;

public readonly record struct AudioPlaybackDiagnostics(
    string Phase,
    bool PlayWhenReady,
    int SampleRate,
    int Channels,
    int DevicePaddingMs,
    int CurrentPcmMs,
    int NextPcmMs,
    bool NextReady,
    long RenderEpoch,
    long RenderedFrames,
    long SubmittedFrames,
    long PlayedFrames,
    long UnexpectedUnderruns,
    PlaybackCommandId LastCommand,
    double LastCommandApplicationMs)
{
    public bool NextScheduled { get; init; }
}

public sealed partial class FluentMediaAudioHost
{
    double _lastCommandApplicationMs;

    public AudioPlaybackDiagnostics Diagnostics
    {
        get
        {
            var session = _session as PcmAudioSession;
            int rate = session?.Format.SampleRate ?? 0;
            int Milliseconds(int frames) => rate > 0 ? (int)((long)frames * 1000 / rate) : 0;
            var next = (_joinPending ? _joinVoice : _prepItem?.AudioVoice) as RingAudioSource;
            int nextFrames = next?.BufferedFrames ?? 0;
            bool nextReady = _prepItem?.IsReady == true
                || _joinPending && next is { ProducerFault: null, Exhausted: false } && nextFrames > 0;
            return new AudioPlaybackDiagnostics(_core.State.Peek().ToString(), _playIntent,
                rate, session?.Format.Channels ?? 0, Milliseconds(session?.DevicePaddingFrames ?? 0),
                Milliseconds(session?.BufferedFrames ?? 0), Milliseconds(nextFrames), nextReady,
                session?.RenderEpoch ?? 0, session?.SampleClock ?? 0, session?.SubmittedFrames ?? 0,
                session?.PlayedFrames ?? 0, session?.XrunCount ?? 0, _latestCommand, _lastCommandApplicationMs)
            { NextScheduled = _joinPending };
        }
    }
}
