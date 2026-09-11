using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;
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

/// <summary>Finite expanded-row document assembly. Resource state, bytes and scheduling belong to the catalog.</summary>
public sealed class SpotifyTrackExpansionService : ITrackExpansionService
{
    static readonly MessageParser<Va.VideoAssociations> AssocParser =
        Va.VideoAssociations.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Af.AudioFilesExtensionResponse> AudioFilesParser =
        Af.AudioFilesExtensionResponse.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Wf.ThreeBandWaveforms> WaveformParser =
        Wf.ThreeBandWaveforms.Parser.WithDiscardUnknownFields(true);

    readonly CatalogExtensionReader _reader;
    readonly CatalogRepository _catalog;
    readonly IResourceCoordinator _resources;
    readonly PlaybackQueueProjection _projection;
    readonly Func<string, CatalogScope> _scope;
    readonly WaveeLogger _log;

    // Per-item format override. Session-scoped by design: "play this ONE track as FLAC" is a momentary choice, and
    // persisting it would silently diverge from the user's global quality setting forever.
    readonly ConcurrentDictionary<string, int> _formatOverrides = new(StringComparer.Ordinal);

    public SpotifyTrackExpansionService(CatalogExtensionReader reader, CatalogRepository catalog,
        IResourceCoordinator resources, PlaybackQueueProjection projection, Func<string, CatalogScope> scope,
        WaveeLogger log = default)
    { _reader = reader; _catalog = catalog; _resources = resources; _projection = projection; _scope = scope; _log = log; }

    public Task<TrackExpansion> GetAsync(string trackUri, CancellationToken ct = default)
        => string.IsNullOrEmpty(trackUri) || EntityUri.KindOf(trackUri) != EntityKind.Track
            ? Task.FromResult(TrackExpansion.Empty) : LoadAsync(trackUri, ct);

    public void SetFormatOverride(string uri, int? formatId)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (formatId is { } id) _formatOverrides[uri] = id;
        else _formatOverrides.TryRemove(uri, out _);
    }

    public int? FormatOverrideFor(string uri)
        => !string.IsNullOrEmpty(uri) && _formatOverrides.TryGetValue(uri, out var id) ? id : null;

    async Task<TrackExpansion> LoadAsync(string trackUri, CancellationToken ct)
    {
        try
        {
            var videoKey = new ResourceKey(_scope(trackUri), trackUri, FacetKind.VideoAssociation);
            var raw = await _reader.ReadDocumentsAsync(
                [(trackUri, Xm.ExtensionKind.AudioAssociations), (trackUri, Xm.ExtensionKind.AudioFiles),
                 (trackUri, Xm.ExtensionKind.ThreebandWaveforms)], ct, [videoKey]).ConfigureAwait(false);

            var targets = new List<(string Uri, TrackVersionKind Kind)>(4);
            if (_catalog.Peek(videoKey).Value is VideoAssociationValue { Association.CounterpartUri: { Length: > 0 } counterpart })
                targets.Add((counterpart, TrackVersionKind.Video));
            raw.TryGetValue((trackUri, Xm.ExtensionKind.AudioAssociations), out var audio);
            CollectTargets(audio, TrackVersionKind.Audio, targets);

            raw.TryGetValue((trackUri, Xm.ExtensionKind.AudioFiles), out var files);
            var formats = MapFormats(files);

            raw.TryGetValue((trackUri, Xm.ExtensionKind.ThreebandWaveforms), out var wave);
            var waveform = MapWaveform(wave);

            var versions = targets.Count == 0
                ? (IReadOnlyList<TrackVersion>)Array.Empty<TrackVersion>()
                : await ResolveAsync(targets, ct).ConfigureAwait(false);

            return new TrackExpansion(versions, formats, waveform);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Event(WaveeLogLevel.Warning, "expansion.fail", "track expansion failed", trackUri, ex: ex);
            // Never memoize a failure — the next open must retry rather than inherit an empty drawer.
            return TrackExpansion.Empty;
        }
    }

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
    async Task<IReadOnlyList<TrackVersion>> ResolveAsync(List<(string Uri, TrackVersionKind Kind)> targets, CancellationToken ct)
    {
        var keys = new List<ResourceKey>(targets.Count);
        foreach (var (uri, _) in targets) keys.Add(new(_scope(uri), uri, FacetKind.TrackIdentity));
        await _resources.EnsureAsync(keys, ct: ct).ConfigureAwait(false);
        var list = new List<TrackVersion>(targets.Count);
        foreach (var (uri, kind) in targets)
        {
            var row = _projection.ReadTrack(uri);
            list.Add(new TrackVersion(uri, kind, row.Title, row.Image, row.DurationMs,
                TempoBpm: row.TempoBpm, MusicalKey: row.MusicalKey,
                CamelotCode: row.CamelotCode, CamelotColor: row.CamelotColor));
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
