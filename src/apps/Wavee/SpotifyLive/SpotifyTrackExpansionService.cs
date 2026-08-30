using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Hydration.Projectors;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Af = Wavee.Protocol.Audiofiles;
using Md = Wavee.Protocol.Metadata;
using Wf = Wavee.Waveforms;
using Va = Wavee.Protocol.ExtendedMetadata;
using Xm = Wavee.Protocol.ExtendedMetadata;
// EntityKind: the ONE uri vocabulary (Wavee.Core), not the transport's thin Backend.Metadata projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.SpotifyLive;

/// <summary>Resolves a track's alternate versions and playable audio formats for the expanded row drawer.
///
/// Everything here is fetched ON EXPAND. That is the whole performance argument: a 10k-row playlist realizes rows
/// constantly, and the waveform/association planes are far too heavy to ride that path — kind 237 alone is ~38 KB per
/// track. Tempo and tint DO ride the row bundle (the trait pipeline's audio-attributes and visual-identity projectors)
/// because they are a few bytes and appear in the row itself.
///
/// The version LABEL problem, stated plainly: kinds 98/99 return a <c>target_uri</c> and artwork and nothing else — no
/// name, no "Live"/"Remix" tag. So the associations are resolved to real tracks in a second read, and the drawer shows
/// the resolved TRACK NAME. Any type wording beyond video-vs-audio would be invented, so it is not shown.
///
/// THIN OVER <see cref="IExtensionReader"/> (design §2.5). Three consequences worth stating, because they are the
/// measurable half of this rewrite:
/// <list type="bullet">
/// <item>the target resolve reads <b>TrackV4 only</b>. It used to ask for 222 (AUDIO_ATTRIBUTES_V2) beside it and then
/// <i>discard the payload</i> — the tempo/key the drawer prints is read off <c>_store.GetTrack</c>, written by the row
/// bundle. That was one wasted kind per association target on every expand;</item>
/// <item>TrackV4 now rides the SAME etag cache the catalogue reads, so a version target the page already hydrated
/// costs nothing;</item>
/// <item>kind 237 gets that cache for free as well (design finding 25) — which is what turns a re-expand into a 304
/// instead of a fresh ~38 KB body.</item>
/// </list></summary>
public sealed class SpotifyTrackExpansionService : ITrackExpansionService
{
    /// <summary>How many assembled drawers are memoized. The memo is the REDUCTION, not the bytes: the reader and the
    /// etag cache below it already hold the payloads, so what this saves is re-walking three ~12 KB waveform bands into
    /// 220 columns every time a row is re-expanded. Past the cap a drawer still opens — it just re-assembles.</summary>
    const int MemoCap = 256;

    static readonly MessageParser<Va.VideoAssociations> AssocParser =
        Va.VideoAssociations.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Af.AudioFilesExtensionResponse> AudioFilesParser =
        Af.AudioFilesExtensionResponse.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Md.Track> TrackParser = Md.Track.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Wf.ThreeBandWaveforms> WaveformParser =
        Wf.ThreeBandWaveforms.Parser.WithDiscardUnknownFields(true);

    readonly IExtensionReader _reader;
    readonly IStore _store;
    readonly WaveeLogger _log;

    // Per-item format override. Session-scoped by design: "play this ONE track as FLAC" is a momentary choice, and
    // persisting it would silently diverge from the user's global quality setting forever.
    readonly ConcurrentDictionary<string, int> _formatOverrides = new(StringComparer.Ordinal);

    // The ASSEMBLED drawer per track — see MemoCap. Not an in-flight guard bolted on the side: it is ONE dictionary of
    // Tasks, so a second opener of the same row joins the first assembly rather than racing it.
    readonly ConcurrentDictionary<string, Task<TrackExpansion>> _cache = new(StringComparer.Ordinal);

    public SpotifyTrackExpansionService(IExtensionReader reader, IStore store, WaveeLogger log = default)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    public Task<TrackExpansion> GetAsync(string trackUri, CancellationToken ct = default)
    {
        // TRACK-ONLY on purpose, and it stays that way after P4's "an episode is a playable" sweep: every plane this
        // drawer assembles is a recording fact the podcast catalogue does not file — alternate/video versions (98/5),
        // the audio-file format list, the 237 waveform. An episode has exactly one rendition and no counterparts, so
        // widening would buy a guaranteed-empty drawer for one extra POST per expanded row.
        if (string.IsNullOrEmpty(trackUri) || EntityUri.KindOf(trackUri) != EntityKind.Track)
            return Task.FromResult(TrackExpansion.Empty);

        // The load runs on CancellationToken.None (inside LoadAsync): it is SHARED, so one caller navigating away must
        // not cancel an assembly a second surface is waiting on — the same rule the reader applies one layer down.
        if (!_cache.TryGetValue(trackUri, out var memo))
            memo = _cache.Count >= MemoCap
                ? LoadAsync(trackUri)                                   // past the cap: still correct, just unmemoized
                : _cache.GetOrAdd(trackUri, static (u, self) => self.LoadAsync(u), this);

        // …and the CALLER's token only ever detaches the caller.
        return memo.WaitAsync(ct);
    }

    public void SetFormatOverride(string uri, int? formatId)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (formatId is { } id) _formatOverrides[uri] = id;
        else _formatOverrides.TryRemove(uri, out _);
    }

    public int? FormatOverrideFor(string uri)
        => !string.IsNullOrEmpty(uri) && _formatOverrides.TryGetValue(uri, out var id) ? id : null;

    async Task<TrackExpansion> LoadAsync(string trackUri)
    {
        try
        {
            // ONE POST for all four planes: the reader groups every kind under a single EntityRequest, so the two
            // association kinds, the audio-file ladder and the ~38 KB waveform cost one round trip between them. That
            // is exactly why 237 is here and not in the row bundle, where 300 realized rows would pull ~11 MB.
            // Revalidate: an expand is the user asking for THIS row's truth, so the read is conditional — the etag
            // rides the request and an unchanged plane comes back as a 304 with no body.
            var raw = await _reader.ReadRawAsync(
                new (string Uri, Xm.ExtensionKind Kind)[]
                {
                    (trackUri, Xm.ExtensionKind.VideoAssociations),
                    (trackUri, Xm.ExtensionKind.AudioAssociations),
                    (trackUri, Xm.ExtensionKind.AudioFiles),
                    (trackUri, Xm.ExtensionKind.ThreebandWaveforms),
                },
                TraitSurface.TrackExpansion, CancellationToken.None, new ReadOptions(Revalidate: true))
                .ConfigureAwait(false);

            var targets = new List<(string Uri, TrackVersionKind Kind)>(4);
            raw.TryGetValue((trackUri, Xm.ExtensionKind.VideoAssociations), out var video);
            raw.TryGetValue((trackUri, Xm.ExtensionKind.AudioAssociations), out var audio);
            // Teach the plane what this fetch just learned — the SAME kind-99 fold the trait pipeline's projector uses,
            // so the row's has-video indicator and this drawer can never disagree about a payload one of them holds.
            // This is also the heal path: a row whose association was a stale negative lights up on expand.
            if (video is not null) VideoProjector.Fold(_store, trackUri, video, DateTimeOffset.UtcNow);
            CollectTargets(Body(video), TrackVersionKind.Video, targets);
            CollectTargets(Body(audio), TrackVersionKind.Audio, targets);

            raw.TryGetValue((trackUri, Xm.ExtensionKind.AudioFiles), out var files);
            var formats = MapFormats(Body(files));

            raw.TryGetValue((trackUri, Xm.ExtensionKind.ThreebandWaveforms), out var wave);
            var waveform = MapWaveform(Body(wave));

            var versions = targets.Count == 0
                ? (IReadOnlyList<TrackVersion>)Array.Empty<TrackVersion>()
                : await ResolveAsync(targets).ConfigureAwait(false);

            return new TrackExpansion(versions, formats, waveform);
        }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "expansion.fail", "track expansion failed", trackUri, ex: ex);
            // Never memoize a failure — the next open must retry rather than inherit an empty drawer.
            _cache.TryRemove(trackUri, out _);
            return TrackExpansion.Empty;
        }
    }

    /// <summary>The decodable body of one answer: null both for "the wire did not answer" and for an explicit negative.
    /// The distinction the etag cache keeps (absent ≠ missing) has no meaning in a drawer — both are "no plane".</summary>
    static ByteString? Body(CachedExtension? answer)
        => answer is { Missing: false, Payload: { IsEmpty: false } payload } ? payload : null;

    /// <summary>Both association kinds share ONE message shape (<c>associations[].target_uri</c> + artwork); only the
    /// image aspect differs (16:9 video stills vs square covers). So one collector serves both, and the KIND comes
    /// from which extension carried it.</summary>
    void CollectTargets(ByteString? payload, TrackVersionKind kind, List<(string, TrackVersionKind)> into)
    {
        if (payload is null || payload.IsEmpty) return;
        try
        {
            var assoc = AssocParser.ParseFrom(payload);
            // The existing proto models field 1 as a SINGLE Association (matching the wire: one counterpart per
            // extension), with the quality variants nested under it. `associated_uri` is that counterpart.
            // Track-only because an alternate/video VERSION of a recording is itself a track — a counterpart of any
            // other kind is a payload we cannot resolve to a version row, and taking it would put an unopenable entry
            // in the drawer.
            if (assoc.Association?.AssociatedUri is { Length: > 0 } uri
                && EntityUri.KindOf(uri) == EntityKind.Track)
                into.Add((uri, kind));
        }
        catch (InvalidProtocolBufferException ex)
        {
            // One malformed association must not lose the other plane's versions — but a silent drop here looked
            // identical to "this track has no versions", which is the one thing the drawer must not lie about.
            _log.Event(WaveeLogLevel.Warning, "expansion.assoc.parse", "video-associations parse failed", ex: ex);
        }
    }

    /// <summary>Columns the waveform is reduced to. Wide enough to read as a shape at any drawer width, small enough
    /// that the whole thing is a few hundred floats instead of ~37 000 bytes held per expanded track.</summary>
    const int WaveformColumns = 220;

    /// <summary>Kind 237 → one 0..1 magnitude per column.
    ///
    /// Reduced HERE, once, rather than in the renderer: the payload is three ~12 KB arrays at 50 Hz and the drawer
    /// draws a strip a few hundred pixels wide, so keeping the raw bytes around would be ~37 KB per expanded row for
    /// resolution nothing can show.
    ///
    /// Each band is walked across its OWN length. The wire ships band_low LONGER than the other two (a confirmed
    /// oddity — 12886 vs 12466 on the reference track), so indexing all three off one cursor drifts ~8 s by the end of
    /// the track. Mapping each band by fraction-of-itself keeps them aligned in TIME.</summary>
    TrackWaveform? MapWaveform(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return null;
        try
        {
            var w = WaveformParser.ParseFrom(payload);
            ReadOnlySpan<byte> low = w.BandLow.Span, mid = w.BandMid.Span, high = w.BandHigh.Span;
            if (low.IsEmpty && mid.IsEmpty && high.IsEmpty) return null;

            var peaks = new float[WaveformColumns];
            float peak = 0f;
            for (int i = 0; i < WaveformColumns; i++)
            {
                // The column's span as a FRACTION of the track, resolved per band against that band's own length.
                float a = (float)i / WaveformColumns, b = (float)(i + 1) / WaveformColumns;
                float sum = BandMax(low, a, b) + BandMax(mid, a, b) + BandMax(high, a, b);
                peaks[i] = sum;
                if (sum > peak) peak = sum;
            }
            if (peak <= 0f) return null;
            for (int i = 0; i < peaks.Length; i++) peaks[i] /= peak;   // normalise to the track's own loudest column
            return new TrackWaveform(peaks);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _log.Event(WaveeLogLevel.Debug, "expansion.waveform.parse", "waveform parse failed", ex: ex);
            return null;
        }
    }

    /// <summary>The loudest sample in [<paramref name="a"/>, <paramref name="b"/>) of a band, as fractions of its own
    /// length. MAX, not mean: averaging flattens transients and every track ends up the same soft blob.</summary>
    static float BandMax(ReadOnlySpan<byte> band, float a, float b)
    {
        if (band.IsEmpty) return 0f;
        int from = (int)(a * band.Length);
        int to = Math.Max(from + 1, Math.Min(band.Length, (int)(b * band.Length)));
        byte max = 0;
        for (int i = from; i < to; i++) if (band[i] > max) max = band[i];
        return max;
    }

    /// <summary>One association target's TrackV4 facts. A record rather than a tuple because it is what the reader
    /// CACHES: the parsed answer is uri-independent, so a target already resolved for another drawer is free.
    /// Non-null whenever the payload decoded at all — returning null would write the shared negative memo for
    /// TrackV4, and "this track has no name" is not an answer anyone should memoize.</summary>
    sealed record TrackFacts(string? Title, long Duration, Image? Art);

    /// <summary>The reader's parse hook for a version target. Only the three fields the drawer prints are kept: the
    /// cover in particular was here all along — a music video is an entity the playlist never contained, so the store
    /// lookup always missed and the thumbnail rendered as an empty tile, while the SAME TrackV4 payload carries
    /// <c>album.cover_group</c>. Kinds 98/99 only ever ship DASH file ids, so this is the only place a video's art can
    /// come from.</summary>
    static TrackFacts ParseTitleArtDuration(ByteString payload)
    {
        var track = TrackParser.ParseFrom(payload);
        return new TrackFacts(
            track.Name is { Length: > 0 } name ? name : null,
            track.Duration > 0 ? track.Duration : 0,
            ExtendedMetadataSource.PickImage(track.Album?.CoverGroup));
    }

    /// <summary>Resolve association targets to real tracks (name + duration + art) so the drawer can show what actually
    /// differs between versions rather than a list of ids. Tempo/key come off the STORE — the row bundle already wrote
    /// them, which is why the dead 222 ask that used to ride this request is gone.</summary>
    async Task<IReadOnlyList<TrackVersion>> ResolveAsync(List<(string Uri, TrackVersionKind Kind)> targets)
    {
        var uris = new List<string>(targets.Count);
        foreach (var (uri, _) in targets) uris.Add(uri);

        IReadOnlyDictionary<string, TrackFacts> resolved;
        try
        {
            // Same kind, same etag cache, same chunking as the catalogue's own TrackV4 reads — so a target the page
            // already hydrated is answered without a request.
            resolved = await _reader.ReadManyAsync(uris, Xm.ExtensionKind.TrackV4, ParseTitleArtDuration,
                                                   TraitSurface.TrackExpansion, CancellationToken.None)
                                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "expansion.resolve.fail", "version title resolve failed", ex: ex);
            // Fall back to id-titled entries rather than dropping the versions entirely — the user still learns the
            // track HAS alternates and can open them.
            return Fallback(targets);
        }

        var list = new List<TrackVersion>(targets.Count);
        foreach (var (uri, kind) in targets)
        {
            TrackFacts? facts = resolved.TryGetValue(uri, out var hit) ? hit : null;
            string title = facts?.Title ?? EntityUri.IdOf(uri);
            long duration = facts?.Duration ?? 0;

            // Prefer whatever the store already knows (the row bundle may have adorned it) over the payload's cover.
            var stored = _store.GetTrack(uri);
            var art = facts?.Art ?? stored?.Image;

            list.Add(new TrackVersion(uri, kind, title, art, duration,
                TempoBpm: stored?.TempoBpm, MusicalKey: stored?.MusicalKey,
                CamelotCode: stored?.CamelotCode, CamelotColor: stored?.CamelotColor));
        }
        return list;
    }

    static IReadOnlyList<TrackVersion> Fallback(List<(string Uri, TrackVersionKind Kind)> targets)
    {
        var list = new List<TrackVersion>(targets.Count);
        foreach (var (uri, kind) in targets) list.Add(new TrackVersion(uri, kind, EntityUri.IdOf(uri), null));
        return list;
    }

    /// <summary>The formats this track actually has, best first. Ordered by bitrate DESC so the quality ladder reads
    /// top-down; a format with no bitrate sorts last rather than pretending to be lossless.</summary>
    IReadOnlyList<AudioFormatOption> MapFormats(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return Array.Empty<AudioFormatOption>();
        try
        {
            var response = AudioFilesParser.ParseFrom(payload);
            var list = new List<AudioFormatOption>(response.Files.Count);
            foreach (var f in response.Files)
            {
                if (f.File is null) continue;
                int id = (int)f.File.Format;
                list.Add(new AudioFormatOption(id, FormatLabel(f.File.Format), f.AverageBitrate));
            }
            list.Sort(static (a, b) => b.AverageBitrate.CompareTo(a.AverageBitrate));
            return list;
        }
        catch (InvalidProtocolBufferException ex)
        {
            _log.Event(WaveeLogLevel.Warning, "expansion.formats.parse", "audio-format parse failed", ex: ex);
            return Array.Empty<AudioFormatOption>();
        }
    }

    /// <summary>A short human label for a Spotify audio format. Unknown values render their raw enum id rather than
    /// being hidden — a format we cannot name is still one the user can select, and hiding it would silently shrink
    /// the ladder.</summary>
    internal static string FormatLabel(Md.AudioFile.Types.Format format) => format switch
    {
        Md.AudioFile.Types.Format.OggVorbis96 => "OGG 96",
        Md.AudioFile.Types.Format.OggVorbis160 => "OGG 160",
        Md.AudioFile.Types.Format.OggVorbis320 => "OGG 320",
        Md.AudioFile.Types.Format.Mp396 => "MP3 96",
        Md.AudioFile.Types.Format.Mp3160 => "MP3 160",
        Md.AudioFile.Types.Format.Mp3256 => "MP3 256",
        Md.AudioFile.Types.Format.Mp3320 => "MP3 320",
        Md.AudioFile.Types.Format.Aac24 => "AAC 24",
        Md.AudioFile.Types.Format.Aac48 => "AAC 48",
        Md.AudioFile.Types.Format.FlacFlac => "FLAC",
        _ => "Format " + ((int)format).ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Switchable expansion seam — the drawer holds this for the session; go-live/GoOffline swap the inner
/// implementation so a page never caches a stale service across a login change.</summary>
public sealed class SwitchableTrackExpansionService : ITrackExpansionService
{
    volatile ITrackExpansionService _inner = NullTrackExpansionService.Instance;

    public void SetInner(ITrackExpansionService inner) => _inner = inner ?? NullTrackExpansionService.Instance;
    public void Reset() => _inner = NullTrackExpansionService.Instance;

    public Task<TrackExpansion> GetAsync(string trackUri, CancellationToken ct = default)
        => _inner.GetAsync(trackUri, ct);
    public void SetFormatOverride(string uri, int? formatId) => _inner.SetFormatOverride(uri, formatId);
    public int? FormatOverrideFor(string uri) => _inner.FormatOverrideFor(uri);
}
