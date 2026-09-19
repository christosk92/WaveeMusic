using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.MediaSources;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The Spotify media source: pure delegation onto the SAME <see cref="LiveTrackResolver"/> + fast-start
/// instances the session already owns, so routing playback through <see cref="MediaProviderRegistry"/> costs exactly one
/// <see cref="EntityUri"/> provider test and changes no resolve semantics. Owns the whole <c>spotify:</c> namespace —
/// tracks AND episodes ride the same seams (the episode branches live inside the resolver, where per-scheme knowledge
/// belongs).</summary>
public sealed class SpotifyMediaProvider : IPlayableMediaProvider
{
    readonly LiveTrackResolver _resolver;
    readonly IFastTrackResolver _fast;
    readonly IFastTrackWarmer? _warmer;

    public SpotifyMediaProvider(LiveTrackResolver resolver, IFastTrackResolver fast)
    {
        _resolver = resolver;
        _fast = fast;
        _warmer = fast as IFastTrackWarmer;
    }

    public string Id => "spotify";

    public MediaProviderCaps Caps =>
        MediaProviderCaps.PreparedNext | MediaProviderCaps.ConnectPublish | MediaProviderCaps.WireMeta;

    /// <summary>The whole <c>spotify:</c> namespace EXCEPT <c>spotify:local:*</c>. That namespace parses as Spotify (it
    /// is Spotify's own self-describing local-file shape) but there is nothing behind it on the server: no track id, no
    /// TRACK_V4 extension, no audio file — a file on somebody's disk. It is also what <c>ConnectUriMask</c> publishes
    /// every non-publishable row as, so a cluster echo of OUR OWN masked state hands one straight back. Owning it made
    /// that echo resolve as a Spotify track and fail with "Restricted: no TRACK_V4 extension" — a user-visible error for
    /// a uri we minted ourselves. Unowned is the honest answer: no local provider can play a stranger's file.</summary>
    public bool Owns(string playableUri) =>
        EntityUri.Parse(playableUri).Provider == EntityProviders.Spotify
        && !playableUri.StartsWith(ConnectUriMask.Prefix, StringComparison.Ordinal);

    public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
        => _fast.ResolveFastAsync(track, ct);

    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
        => _resolver.ResolveAsync(track, ct);

    public void Warm(Track track, string reason = "") => _warmer?.Warm(track, reason);

    public async Task<PlaybackTrackMeta?> ResolveWireMetaAsync(Track track, CancellationToken ct = default)
    {
        var m = await _resolver.ResolveMetaAsync(track, ct).ConfigureAwait(false);
        // EVERY arm is named, and the label is derived from the FORMAT rather than from the bitrate.
        //
        // Both halves used to be wrong in the same direction — they described lossless as lossy Vorbis. `Flac24` had no
        // kbps arm at all, so 24-bit lossless fell through `_ => 160`; and the label was
        // `m.Fmt == Mp3 ? "MP3" : $"Vorbis {kbps} kbps"`, which called a FLAC stream "Vorbis 1411 kbps" and a Flac24
        // stream "Vorbis 160 kbps". Nothing surfaced it, so nothing caught it — the stage's quality badge is the first
        // consumer that shows this string to a user, and a badge that misnames the stream is worse than no badge.
        (int kbps, string fmtLabel) = m.Fmt switch
        {
            AudioFormat.OggVorbis96 => (96, "Vorbis 96 kbps"),
            AudioFormat.OggVorbis160 => (160, "Vorbis 160 kbps"),
            AudioFormat.OggVorbis320 => (320, "Vorbis 320 kbps"),
            // 16-bit/44.1 stereo = 1411 kbps; 24-bit at the same rate is 2116.
            AudioFormat.Flac => (1411, "FLAC"),
            AudioFormat.Flac24 => (2116, "FLAC 24-bit"),
            AudioFormat.Mp3 => (160, "MP3"),
            _ => (160, "160 kbps"),
        };
        return new PlaybackTrackMeta(m.FileGid, m.FileId, kbps, fmtLabel, m.DurMs);
    }
}
