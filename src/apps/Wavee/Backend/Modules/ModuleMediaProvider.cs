using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Backend.MediaSources;
using Wavee.Core;
using Wavee.Sdk;

namespace Wavee.Backend.Modules;

// ── ONE PROVIDER PER INSTALLED MODULE — the module's entry into the playback routing table ───────────────────────────
// This is the whole of "a module can play things": it owns `wavee:module:<id>:` , resolves through the module's
// `playback/resolve`, and maps the answer onto ONE of the four body shapes the audio host already knows —
//
//   url + progressive              → AudioSourceKind.ExternalPlain   (a finite, rangeable body)
//   url + icy, or isLive           → AudioSourceKind.LiveStream      (forward-only; a socket drop is a reconnect)
//   stream                         → AudioSourceKind.ModuleStream    (ModuleByteStream over stream/open|read|close)
//   form == video                  → refused here; the video tier plays it (CompositeVideoResolver's module tier)
//
// so the host needs no per-module code at all. Registered AFTER the built-ins and (at go-live) after Spotify, because
// registration order is the routing table.

/// <summary>The <see cref="IPlayableMediaProvider"/> for one installed module.</summary>
public sealed class ModuleMediaProvider : IPlayableMediaProvider
{
    readonly ModuleHost _host;
    readonly InstalledModule _module;
    readonly string _prefix;
    int _capsRaw;

    /// <summary>Build the provider for one module.</summary>
    /// <param name="host">The module host that owns the process and the resolve cache.</param>
    /// <param name="module">The module this provider fronts.</param>
    public ModuleMediaProvider(ModuleHost host, InstalledModule module)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(module);
        _host = host;
        _module = module;
        _prefix = ModuleUri.Prefix(module.Id);
    }

    /// <summary>The module id — also the diagnostics label.</summary>
    public string Id => _module.Id;

    /// <summary>The module this provider fronts.</summary>
    public InstalledModule Module => _module;

    /// <summary>
    /// The capabilities of the most recently resolved playable of this module (<see cref="MediaProviderCaps.None"/>
    /// until one has resolved). Caps are declared PER PLAYABLE by the module (<c>ResolvedPlayable.Caps</c>), but the
    /// registry asks the provider — so this reports the last answer and defaults to None, which is the safe direction:
    /// an absent capability selects the proven simpler path (hard-cut boundaries, a masked Connect uri, no wire meta).
    /// </summary>
    public MediaProviderCaps Caps => (MediaProviderCaps)Volatile.Read(ref _capsRaw);

    /// <summary>Cheap ordinal prefix test — this runs for every provider until one claims the uri.</summary>
    /// <param name="playableUri">The playable uri.</param>
    public bool Owns(string playableUri) => playableUri.StartsWith(_prefix, StringComparison.Ordinal);

    /// <summary>Resolve the playable into the audio host's fast-first shape (empty head + an already-completed body:
    /// there is nothing to fetch in parallel — the module already did the fetching).</summary>
    /// <param name="track">The synthetic module Track.</param>
    /// <param name="ct">Cancels the resolve.</param>
    public async Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
    {
        ResolvedPlayable resolved = await _host.ResolveAsync(track.Uri, force: false, ct).ConfigureAwait(false);
        Volatile.Write(ref _capsRaw, (int)CapsOf(resolved.Caps));

        if (resolved.Form == Wavee.Sdk.MediaForm.Video)
            throw new AudioPlaybackException(AudioKeyFailureReason.Restricted,
                "video playable; use the video host");

        AudioStreamHandle body = HandleFor(track.Uri, resolved);
        var start = new AudioFastStart(track.Uri, "", body.Format, body.DurationMs, body.NormalizationGainDb, default);
        return new FastStartPlan(start, Task.FromResult(body));
    }

    /// <summary>The wire identity for Connect/telemetry, when the module declares <c>wireMeta</c> for this playable.</summary>
    /// <param name="track">The synthetic module Track.</param>
    /// <param name="ct">Cancels the resolve.</param>
    public async Task<PlaybackTrackMeta?> ResolveWireMetaAsync(Track track, CancellationToken ct = default)
    {
        ResolvedPlayable resolved = await _host.ResolveAsync(track.Uri, force: false, ct).ConfigureAwait(false);
        if (resolved.Wire is not { } w) return null;
        return new PlaybackTrackMeta(w.MediaId ?? [], w.FileId ?? [], w.BitrateKbps, w.AudioFormat ?? "", w.DurationMs);
    }

    /// <summary>Best-effort pre-resolve (<c>playback/warm</c>). Never throws to the caller.</summary>
    /// <param name="track">The playable to warm.</param>
    /// <param name="reason">Why, for the log.</param>
    public void Warm(Track track, string reason = "") => _host.Warm(track.Uri, reason);

    // ── the locator → handle map ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Map one module answer onto the audio host's handle. Pure and internal-static so the whole mapping table
    /// (icy vs progressive vs stream, the content-type → codec map, the live duration-0 rule) is unit-testable without
    /// a module, a process or a host.</summary>
    /// <param name="playableUri">The playable uri the handle belongs to.</param>
    /// <param name="resolved">The module's resolve answer.</param>
    public static AudioStreamHandle HandleFor(string playableUri, ResolvedPlayable resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        MediaLocator media = resolved.Media
            ?? throw new AudioPlaybackException(AudioKeyFailureReason.Restricted,
                "the module returned no media locator for " + playableUri);

        if (string.Equals(media.Kind, MediaLocator.KindStream, StringComparison.Ordinal))
        {
            if (media.StreamId is not { Length: > 0 } streamId)
                throw new AudioPlaybackException(AudioKeyFailureReason.Restricted,
                    "the module returned a stream locator with no streamId for " + playableUri);
            return new AudioStreamHandle(playableUri, "", streamId, default,
                FormatOf(media.ContentType), resolved.DurationMs, resolved.GainDb,
                SourceKind: AudioSourceKind.ModuleStream);
        }

        if (media.Url is not { Length: > 0 } url)
            throw new AudioPlaybackException(AudioKeyFailureReason.Restricted,
                "the module returned a url locator with no url for " + playableUri);

        bool live = resolved.IsLive
            || string.Equals(media.Container, MediaLocator.ContainerIcy, StringComparison.OrdinalIgnoreCase);

        // A live body has NO end to approach: duration 0 keeps every ending-soon / gapless / prepared-next arm off, and
        // the LiveStream kind keeps it off the ranged path that would buffer an endless body into memory.
        return live
            ? new AudioStreamHandle(playableUri, "", url, default, FormatOf(media.ContentType), 0, resolved.GainDb,
                SourceKind: AudioSourceKind.LiveStream)
            : new AudioStreamHandle(playableUri, "", url, default, FormatOf(media.ContentType), resolved.DurationMs,
                resolved.GainDb, SourceKind: AudioSourceKind.ExternalPlain);
    }

    /// <summary>Content-type → the host's format enum. The host still sniffs the first bytes when the station lied or
    /// said nothing; this is only the resolve-time hint. Anything unknown reports <see cref="AudioFormat.Mp3"/>, which
    /// is what an unlabelled internet stream is ~always.</summary>
    /// <param name="contentType">The MIME type the module reported, or null.</param>
    public static AudioFormat FormatOf(string? contentType)
    {
        if (contentType is not { Length: > 0 }) return AudioFormat.Mp3;
        string t = contentType;
        if (t.Contains("aac", StringComparison.OrdinalIgnoreCase)) return AudioFormat.Aac;
        if (t.Contains("flac", StringComparison.OrdinalIgnoreCase)) return AudioFormat.Flac;
        if (t.Contains("ogg", StringComparison.OrdinalIgnoreCase)
            || t.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return AudioFormat.OggVorbis320;
        return AudioFormat.Mp3;
    }

    /// <summary>Map the module's per-playable capability tokens onto <see cref="MediaProviderCaps"/>. Unknown tokens are
    /// ignored (an unknown capability is an absent one, never a failure).</summary>
    /// <param name="caps">The tokens from <c>ResolvedPlayable.Caps</c>.</param>
    public static MediaProviderCaps CapsOf(string[]? caps)
    {
        var result = MediaProviderCaps.None;
        if (caps is null) return result;
        for (int i = 0; i < caps.Length; i++)
        {
            if (string.Equals(caps[i], "preparedNext", StringComparison.OrdinalIgnoreCase)) result |= MediaProviderCaps.PreparedNext;
            else if (string.Equals(caps[i], "connectPublish", StringComparison.OrdinalIgnoreCase)) result |= MediaProviderCaps.ConnectPublish;
            else if (string.Equals(caps[i], "wireMeta", StringComparison.OrdinalIgnoreCase)) result |= MediaProviderCaps.WireMeta;
        }

        return result;
    }
}
