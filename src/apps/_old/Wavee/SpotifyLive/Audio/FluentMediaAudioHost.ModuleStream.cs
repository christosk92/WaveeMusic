using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media.Windows;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Modules;
using Wavee.Sdk;

namespace Wavee.SpotifyLive.Audio;

/// <summary>The MODULE-SERVED bytes arm of <see cref="FluentMediaAudioHost"/>, split into its own file because it is the
/// one branch that depends on the module host (<c>ModuleByteStream</c> over the <c>stream/open|read|close</c> RPC)
/// rather than on anything in the audio stack. The call site is the last arm of <c>SupplyBodyAsync</c>'s branch chain.
///
/// <para>Shape-wise it is the ExternalPlain branch verbatim — open a ranged stream, decide the codec, wrap it in a
/// <c>SpotifyMediaByteSource</c>, open the session — with two differences: the bytes come from a child process instead
/// of an HttpClient, and the codec is decided from the module's <c>stream/open</c> content type first, then from the
/// first bytes (a module that serves an encrypted body cannot always name its container up front).</para></summary>
public sealed partial class FluentMediaAudioHost
{
    private partial Task SupplyModuleStreamAsync(AudioStreamHandle body, long epoch);

    private partial async Task SupplyModuleStreamAsync(AudioStreamHandle body, long epoch)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) return;

        // The handle carries the streamId in CdnUrl (the same field every other kind uses for "the one string that
        // locates the bytes"); the OWNING module comes out of the playable uri, so the host never has to be told.
        if (!ModuleUri.TryDecode(body.TrackUri, out string moduleId, out _))
        {
            _log.Info($"module stream body has a non-module uri: {body.TrackUri}");
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.Restricted,
                "this playable is not owned by a playback module"));
            return;
        }

        ModuleProcess? process = ModuleHost.Current?.ProcessFor(moduleId);
        if (process is null)
        {
            _log.Info($"no installed module named '{moduleId}' to serve stream {body.CdnUrl}");
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.Restricted,
                "the module that owns this track is not installed"));
            return;
        }

        ModuleByteStream stream;
        try
        {
            stream = await ModuleByteStream.OpenAsync(process, body.CdnUrl, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ONE open attempt, then a typed failure: the module has already done its own retrying upstream, and a
            // silent nothing here would leave the session in a permanent "loading" state with no way to tell why.
            _log.Info($"module stream open failed module={moduleId} stream={body.CdnUrl}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(0, ReasonFor(ex), ex.Message));
            return;
        }

        if (epoch != Volatile.Read(ref _loadEpoch)) { stream.Dispose(); return; }

        WaveeDecoderKind kind;
        try
        {
            kind = SniffExternalKind(stream.ContentType) ?? SniffModuleKind(stream) ?? KindOf(body.Format);
        }
        catch (Exception ex)
        {
            _log.Info($"module stream sniff failed module={moduleId}: {ex.GetType().Name}: {ex.Message}");
            stream.Dispose();
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.Network, ex.Message));
            return;
        }

        if (kind == WaveeDecoderKind.Aac && !MfAacDecoder.IsAvailable())
        {
            stream.Dispose();
            _signals.OnNext(AudioHostSignal.Fault(0, AudioKeyFailureReason.ArchUnsupported,
                "this Windows edition has no AAC decoder (install the Media Feature Pack)"));
            return;
        }

        var bytes = new SpotifyMediaByteSource(stream, 0, kind, body.DurationMs, DbToLinear(body.NormalizationGainDb));
        _activeStream = null;
        await OpenSessionAsync(bytes, epoch).ConfigureAwait(false);
    }

    /// <summary>Codec from the first bytes of a module stream, for the (common) case where the module named no content
    /// type: <c>OggS</c>, <c>fLaC</c>, then the ID3/MP3/ADTS sniff the live path already owns. Null = "no idea", which
    /// falls back to the format the resolve answer declared.</summary>
    static WaveeDecoderKind? SniffModuleKind(Stream stream)
    {
        if (!stream.CanSeek) return null;
        Span<byte> head = stackalloc byte[64];
        long start = stream.Position;
        int read = 0;
        while (read < head.Length)
        {
            int n = stream.Read(head[read..]);
            if (n <= 0) break;
            read += n;
        }

        stream.Position = start;   // the decoder must still see byte 0
        if (read <= 0) return null;
        ReadOnlySpan<byte> h = head[..read];
        if (h.Length >= 4 && h[0] == (byte)'O' && h[1] == (byte)'g' && h[2] == (byte)'g' && h[3] == (byte)'S')
            return WaveeDecoderKind.Vorbis;
        if (h.Length >= 4 && h[0] == (byte)'f' && h[1] == (byte)'L' && h[2] == (byte)'a' && h[3] == (byte)'C')
            return WaveeDecoderKind.Flac;
        return SniffLiveKind(h);
    }

    static AudioKeyFailureReason ReasonFor(Exception ex) => ex switch
    {
        ModuleException { Code: ModuleErrorCode.NeedsAuth } => AudioKeyFailureReason.Restricted,
        ModuleException { Code: ModuleErrorCode.GeoBlocked or ModuleErrorCode.Unavailable or ModuleErrorCode.Unsupported }
            => AudioKeyFailureReason.Restricted,
        ModuleException { Code: ModuleErrorCode.Offline or ModuleErrorCode.Transient } => AudioKeyFailureReason.Network,
        IOException => AudioKeyFailureReason.Network,
        _ => AudioKeyFailureReason.Restricted,
    };
}
