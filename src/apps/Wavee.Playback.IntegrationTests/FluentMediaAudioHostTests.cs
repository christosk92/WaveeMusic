using FluentGpu.Media;
using Wavee.Backend;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Playback.IntegrationTests;

public sealed class FluentMediaAudioHostTests
{
    [Fact]
    public async Task PauseIsAcknowledgedWhileBackendFactoryIsBlocked()
    {
        using var http = new HttpClient();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        await using var host = new FluentMediaAudioHost(static () => null, http, effects =>
        {
            entered.TrySetResult();
            release.Wait();
            Interlocked.Increment(ref completed);
            return new PcmAudioPlayer(effects: effects);
        });
        var command = new PlaybackCommandId(1, 1);
        var applied = new TaskCompletionSource<AudioHostSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = host.Signals.Subscribe(new SignalObserver(signal =>
        {
            if (signal.Command == command && signal.OperationStatus == PlaybackOperationStatus.Applied)
                applied.TrySetResult(signal);
        }));
        try
        {
            host.WarmBackend();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var receipt = host.Submit(new(command, AudioTransportAction.Pause, false));
            Assert.Equal(command, receipt.Id);
            AudioHostSignal signal = await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(signal.IsPlaying);
            Assert.False(host.PlayIntent);
            Assert.Equal(0, Volatile.Read(ref completed));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task OldTransportGenerationCannotRestorePlayIntent()
    {
        using var http = new HttpClient();
        await using var host = new FluentMediaAudioHost(static () => null, http,
            static effects => new PcmAudioPlayer(effects: effects));
        var current = new PlaybackCommandId(2, 1);
        var stale = new PlaybackCommandId(1, 99);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var superseded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int playReports = 0;
        using var subscription = host.Signals.Subscribe(new SignalObserver(signal =>
        {
            if (signal.PlayWhenReady == true) Interlocked.Increment(ref playReports);
            if (signal.Command == current && signal.OperationStatus == PlaybackOperationStatus.Applied)
                acknowledged.TrySetResult();
            if (signal.Command == stale && signal.OperationStatus == PlaybackOperationStatus.Superseded)
                superseded.TrySetResult();
        }));
        host.Submit(new(current, AudioTransportAction.Pause, false));
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Submit(new(stale, AudioTransportAction.Play, true));
        await superseded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.PlayIntent);
        Assert.Equal(0, Volatile.Read(ref playReports));
    }

    private sealed class SignalObserver(Action<AudioHostSignal> onNext) : IObserver<AudioHostSignal>
    {
        public void OnNext(AudioHostSignal value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }
}
