using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.Tests;

/// <summary>Explicit synchronous output simulation shared by recording hosts. No production fallback.</summary>
internal static class RecordingHostOperations
{
    sealed class State { public long Generation; public bool Intent; }
    static readonly ConditionalWeakTable<IMediaHost, State> States = new();

    public static PlaybackCommandReceipt Submit(IMediaHost host, AudioTransportRequest request, Action<AudioHostSignal> emit)
    {
        var state = States.GetOrCreateValue(host);
        if (request.Action is not AudioTransportAction.Seek) state.Intent = request.PlayWhenReady;
        switch (request.Action)
        {
            case AudioTransportAction.Play: host.Play(); break;
            case AudioTransportAction.Pause: host.Pause(); break;
            case AudioTransportAction.Stop: host.Stop(); break;
            case AudioTransportAction.Skip: host.Pause(); break;
            case AudioTransportAction.Seek: host.Seek(request.PositionMs, request.Mode); break;
            case AudioTransportAction.Adopt: break;
            default: throw new ArgumentOutOfRangeException(nameof(request));
        }
        bool advancing = state.Intent && host.IsPlaying;
        emit(new AudioHostSignal(advancing ? AudioHostSignalKind.Playing : AudioHostSignalKind.Paused,
            host.PositionMs, advancing, false, false)
        { Command = request.Command, OperationStatus = PlaybackOperationStatus.Applied, PlayWhenReady = state.Intent });
        return new(request.Command);
    }

    public static void Load(IAudioHost host, AudioLoadRequest request, Action<AudioHostSignal> emit)
    {
        var state = States.GetOrCreateValue(host);
        state.Generation = request.Item.Generation;
        state.Intent = request.PlayWhenReady;
        if (request.Source.Start.HeadBytes.IsEmpty && request.Source.Body.IsCompletedSuccessfully)
            host.Load(request.Source.Body.Result);
        else host.LoadFastStart(request.Source.Start);
        if (request.StartPositionMs > 0) host.Seek(request.StartPositionMs, SeekMode.Accurate);
        if (state.Intent) host.Play();
        emit(new AudioHostSignal(state.Intent ? AudioHostSignalKind.Playing : AudioHostSignalKind.Paused,
            request.StartPositionMs, state.Intent, false, false) { Command = request.Command, PlayWhenReady = state.Intent, OperationStatus = PlaybackOperationStatus.Applied });
        if (!request.Source.Start.HeadBytes.IsEmpty) _ = AttachAsync(host, request, state, emit);
    }

    static async Task AttachAsync(IAudioHost host, AudioLoadRequest request, State state, Action<AudioHostSignal> emit)
    {
        try
        {
            var body = await request.Source.Body.ConfigureAwait(false);
            if (state.Generation == request.Item.Generation) host.SupplyBody(body);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (state.Generation != request.Item.Generation) return;
            host.Stop();
            emit(AudioHostSignal.Fault(host.PositionMs, ex is AudioPlaybackException a ? a.Reason : AudioKeyFailureReason.None, ex.Message)
                with { Command = request.Command });
        }
    }
}
