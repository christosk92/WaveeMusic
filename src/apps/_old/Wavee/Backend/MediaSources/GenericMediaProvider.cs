using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.Backend.MediaSources;

// ── The GENERIC "play this file / URL" media source (Part A validation case (c)) ─────────────────────────────────────
// The smoke test for the seam's whole point: a playable that belongs to NO catalog. It owns one namespace and resolves
// its payload into one of the TWO body shapes that already exist — the local-file handle (case (b)) or the plain-HTTP
// handle external podcast episodes use. That is why this class adds ZERO host code: everything it produces is a shape
// FluentMediaAudioHost already knows how to open.

/// <summary>Owns <c>wavee:media:&lt;b64url(path-or-url)&gt;</c>. <see cref="MediaProviderCaps.None"/> — the same
/// simple-path selection as the local-file source (hard-cut boundaries, masked Connect uri, no wire meta).</summary>
public sealed class GenericMediaProvider : IPlayableMediaProvider
{
    public const string UriPrefix = PlayableUri.MediaPrefix;

    readonly Func<string, bool> _fileExists;
    readonly Func<string, long>? _probeDurationMs;

    public GenericMediaProvider(Func<string, bool>? fileExists = null, Func<string, long>? probeDurationMs = null)
    {
        _fileExists = fileExists ?? System.IO.File.Exists;
        _probeDurationMs = probeDurationMs;
    }

    public string Id => "generic";

    public MediaProviderCaps Caps => MediaProviderCaps.None;

    public bool Owns(string playableUri) => playableUri.StartsWith(UriPrefix, StringComparison.Ordinal);

    public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
    {
        if (!PlayableUri.TryDecodeMedia(track.Uri, out var payload))
            throw new AudioPlaybackException(AudioKeyFailureReason.Restricted,
                "malformed generic playable uri: " + track.Uri);

        // http(s) → the EXACT external-episode shape: an empty head plus a completed ExternalPlain body whose CdnUrl is
        // the URL. The host's existing ExternalPlain branch opens a PlainHttpAudioStream and sniffs the codec from the
        // Content-Type, so no new host code exists on this path at all.
        if (PlayableUri.IsHttpUrl(payload))
        {
            var body = new AudioStreamHandle(track.Uri, "", payload, default, AudioFormat.Mp3, track.DurationMs, 0f,
                SourceKind: AudioSourceKind.ExternalPlain);
            var start = new AudioFastStart(track.Uri, "", AudioFormat.Mp3, track.DurationMs, 0f, default);
            return Task.FromResult(new FastStartPlan(start, Task.FromResult(body)));
        }

        // Everything else is a path on disk → the local-file handle, built by the ONE shared builder (so the existence
        // check, the format map and the typed failures can never drift between the two providers).
        return Task.FromResult(LocalFileMediaProvider.PlanForPath(
            track.Uri, payload, track.DurationMs, _fileExists, _probeDurationMs));
    }
}
