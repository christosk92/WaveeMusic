using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.Backend.MediaSources;

// ── The LOCAL-FILE media source (Part A validation case (b)) ─────────────────────────────────────────────────────────
// The second real provider, and the proof that the seam is source-agnostic: it resolves a playable with NO network, NO
// key, NO CDN and NO Spotify metadata, using the same fast-first contract the Spotify provider uses. Its plan is the
// external-episode shape verbatim — an EMPTY head plus an already-completed body — so the controller's instant-start
// path needs no special case: LoadFastStart defers the session, SupplyBody opens it on the file.
//
// Deliberately narrow (see the plan's non-goals): mp3/ogg/flac only, because those are the three decoders the audio
// host actually has (WaveeDecoderKind). An .m4a/.aac/.wav/.opus pick is refused with a typed error rather than played
// into silence.

/// <summary>Owns <c>wavee:local:file:&lt;b64url(absolute path)&gt;</c>. <see cref="MediaProviderCaps.None"/>: a local
/// file has no prepared-next hand-off (v1), publishes no Connect uri (the publisher masks it) and carries no wire
/// metadata — every absent capability selects a proven simpler path, never a failure.</summary>
public sealed class LocalFileMediaProvider : IPlayableMediaProvider
{
    public const string UriPrefix = PlayableUri.LocalFilePrefix;

    readonly Func<string, bool> _fileExists;
    readonly Func<string, long>? _probeDurationMs;

    /// <param name="fileExists">Existence probe — injected purely so the whole resolve is testable without a disk.</param>
    /// <param name="probeDurationMs">Optional codec-header duration probe (live wiring: LocalAudioDurationProbe.Probe).
    /// Consulted ONLY when the Track itself carries no duration, so the normal entry points (which probe once at pick /
    /// drop time) never pay for it on the resolve path.</param>
    public LocalFileMediaProvider(Func<string, bool>? fileExists = null, Func<string, long>? probeDurationMs = null)
    {
        _fileExists = fileExists ?? System.IO.File.Exists;
        _probeDurationMs = probeDurationMs;
    }

    public string Id => "local-file";

    public MediaProviderCaps Caps => MediaProviderCaps.None;

    public bool Owns(string playableUri) => playableUri.StartsWith(UriPrefix, StringComparison.Ordinal);

    public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
    {
        if (!PlayableUri.TryDecodeLocalFile(track.Uri, out var path))
            throw new AudioPlaybackException(AudioKeyFailureReason.Restricted,
                "malformed local-file playable uri: " + track.Uri);
        return Task.FromResult(PlanForPath(track.Uri, path, track.DurationMs, _fileExists, _probeDurationMs));
    }

    // ── the shared local-file plan (GenericMediaProvider resolves an audio-file payload through this too) ────────────

    /// <summary>Build the fast-start plan for a file on disk: the external-episode shape (empty head + completed body)
    /// with the path riding in <see cref="AudioStreamHandle.CdnUrl"/>, exactly the way the plain-HTTP path carries its
    /// URL. Throws the typed <see cref="AudioPlaybackException"/> the existing ReportPlaybackError surface expects.</summary>
    internal static FastStartPlan PlanForPath(string playableUri, string path, long knownDurationMs,
        Func<string, bool> fileExists, Func<string, long>? probeDurationMs)
    {
        bool exists;
        try { exists = fileExists(path); }
        catch { exists = false; }
        if (!exists)
            throw new AudioPlaybackException(AudioKeyFailureReason.Restricted, "the file is missing: " + path);

        var format = FormatOf(path);
        long durationMs = knownDurationMs > 0 ? knownDurationMs : Probe(path, probeDurationMs);

        // The body is ALREADY resolved — there is nothing to fetch. Keeping the fast-first shape (rather than adding a
        // second contract) is what lets the controller treat a local file exactly like an external podcast episode.
        var handle = new AudioStreamHandle(playableUri, "", path, default, format, durationMs, 0f,
            SourceKind: AudioSourceKind.LocalFile);
        var start = new AudioFastStart(playableUri, "", format, durationMs, 0f, default);   // EMPTY head → deferred open
        return new FastStartPlan(start, Task.FromResult(handle));
    }

    /// <summary>The extension → decoder map. The three supported containers are exactly the three
    /// <c>WaveeDecoderKind</c> leaves the audio host owns; anything else is refused HERE, before a decoder is asked to
    /// guess, so the user gets an honest message instead of silence.</summary>
    internal static AudioFormat FormatOf(string path)
    {
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return AudioFormat.Mp3;
        if (path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return AudioFormat.OggVorbis320;
        if (path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) return AudioFormat.Flac;
        throw new AudioPlaybackException(AudioKeyFailureReason.ArchUnsupported,
            "unsupported audio file (only .mp3, .ogg and .flac can be played): " + path);
    }

    /// <summary>Is this path one of the three containers the audio host can decode? The shared predicate behind the
    /// "Play file…" picker filter and the shell drop target, so a file can never be accepted by a surface and then
    /// refused by the resolver.</summary>
    public static bool IsSupportedAudioFile(string? path)
        => path is { Length: > 0 }
           && (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));

    static long Probe(string path, Func<string, long>? probe)
    {
        if (probe is null) return 0;
        try { return Math.Max(0, probe(path)); }
        catch { return 0; }   // an unreadable header is not a play failure — the host still decodes it
    }
}
