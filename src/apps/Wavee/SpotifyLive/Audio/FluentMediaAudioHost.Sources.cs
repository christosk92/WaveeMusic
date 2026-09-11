using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Modules;
using Wavee.Sdk;

namespace Wavee.SpotifyLive.Audio;

public sealed partial class FluentMediaAudioHost
{
    async Task<SpotifyMediaByteSource> ReopenSourceAsync(SpotifyMediaByteSource original, CancellationToken ct,
        bool forDeviceRecovery = false)
    {
        if (!original.ReadStream.AsStream().CanSeek
            && !(forDeviceRecovery && original.ReopenBody?.SourceKind == AudioSourceKind.LiveStream))
            throw new NotSupportedException("This source has no seekable timeline.");
        if (original.ReopenBody is not { } body)
            throw new InvalidOperationException("The audio body is not ready for an independent seek.");
        return await OpenBodySourceAsync(body, original.SkipOffset, original.Kind, original.DurationMs, original.GainLinear, ct);
    }

    async Task<SpotifyMediaByteSource> OpenBodySourceAsync(AudioStreamHandle body, int skip,
        WaveeDecoderKind kind, long durationMs, float gainLinear, CancellationToken ct)
    {
        IAudioReadStream stream;
        switch (body.SourceKind)
        {
            case AudioSourceKind.LocalFile:
                stream = await Task.Run(() => LocalFileAudioStream.Open(body.CdnUrl), ct);
                break;
            case AudioSourceKind.ExternalPlain:
                stream = await PlainHttpAudioStream.OpenAsync(_http, body.CdnUrl, _log, ct);
                break;
            case AudioSourceKind.LiveStream:
                var live = await _openLiveSource(body.CdnUrl, ct);
                kind = await IdentifyLiveSourceAsync(live, ct);
                stream = live;
                break;
            case AudioSourceKind.ModuleStream:
                if (!ModuleUri.TryDecode(body.TrackUri, out string moduleId, out _))
                    throw new InvalidDataException("Module source identity is invalid.");
                var process = ModuleHost.Current?.ProcessFor(moduleId)
                    ?? throw new InvalidOperationException("The module that owns this stream is unavailable.");
                stream = await ModuleByteStream.OpenAsync(process, body.CdnUrl, ct);
                break;
            case AudioSourceKind.SpotifyEncrypted:
                var spotify = SpotifyAudioStream.CreateHeadOnly(_http, ReadOnlyMemory<byte>.Empty, 0,
                    body.FileIdHex, _log, _bodyDisk);
                try
                {
                    var urls = body.CdnUrls ?? (string.IsNullOrEmpty(body.CdnUrl) ? [] : new[] { body.CdnUrl });
                    await spotify.AttachBodyWithNativeDecryptorAsync(BuildDecryptor(body), urls, null, ct);
                    spotify.ConfigureReadAhead(AudioBitratePolicy.BitsPerSecond(body.Format), Wavee.NetworkPolicy.IsMetered);
                    stream = spotify;
                }
                catch { spotify.Dispose(); throw; }
                break;
            default:
                throw new NotSupportedException("This audio source cannot be reopened.");
        }
        if (ct.IsCancellationRequested) { stream.Dispose(); ct.ThrowIfCancellationRequested(); }
        return new SpotifyMediaByteSource(stream, skip, kind, durationMs, gainLinear)
        { ReopenBody = body };
    }

    static async Task<WaveeDecoderKind> IdentifyLiveSourceAsync(LiveHttpAudioStream live, CancellationToken ct)
    {
        try
        {
            // Cancellation must wake the sniff's actual ring wait, including before a decoder exists.
            using var registration = ct.Register(live.Dispose);
            var head = new byte[512];
            int length = await Task.Run(() => live.PeekHead(head), ct);
            ct.ThrowIfCancellationRequested();
            return SniffExternalKind(live.ContentType) ?? SniffLiveKind(head.AsSpan(0, length)) ?? WaveeDecoderKind.Mp3;
        }
        catch
        {
            await live.DisposeAsync();
            throw;
        }
    }
}
