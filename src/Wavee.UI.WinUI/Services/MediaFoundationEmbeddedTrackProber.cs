using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Extensions.Logging;
using Wavee.Local.Subtitles;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// WinUI-specific <see cref="IEmbeddedTrackProber"/> using
/// <see cref="MediaSource"/> to enumerate embedded audio / video / subtitle
/// streams without decoding. Keeps the Wavee.Local scanner OS-agnostic;
/// non-Windows surfaces register no prober and embedded-track indexing
/// becomes a no-op.
///
/// <para>Returns empty on every failure path — never throws. The scanner
/// pipeline runs sync inside a Task.Run so we block on the async APIs.</para>
/// </summary>
public sealed class MediaFoundationEmbeddedTrackProber : IEmbeddedTrackProber
{
    private readonly ILogger? _logger;

    public MediaFoundationEmbeddedTrackProber(ILogger<MediaFoundationEmbeddedTrackProber>? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<EmbeddedTrackInfo> Probe(string filePath)
    {
        try
        {
            return ProbeAsync(filePath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Embedded track probe failed for {Path}", filePath);
            return Array.Empty<EmbeddedTrackInfo>();
        }
    }

    private async Task<IReadOnlyList<EmbeddedTrackInfo>> ProbeAsync(string filePath)
    {
        if (!File.Exists(filePath)) return Array.Empty<EmbeddedTrackInfo>();

        var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
        using var source = MediaSource.CreateFromStorageFile(storageFile);
        await source.OpenAsync();

        var tracks = new List<EmbeddedTrackInfo>();

        if (source.ExternalTimedMetadataTracks is { Count: > 0 } sub)
        {
            for (int i = 0; i < sub.Count; i++)
            {
                var t = sub[i];
                tracks.Add(new EmbeddedTrackInfo(
                    Kind: EmbeddedTrackKind.Subtitle,
                    StreamIndex: i,
                    Language: NullIfBlank(t.Language),
                    Label: NullIfBlank(t.Label),
                    Codec: t.TimedMetadataKind.ToString(),
                    IsDefault: false));
            }
        }

        // Audio + video tracks live behind MediaPlaybackItem. Wrapping the
        // MediaSource is a few-millisecond overhead — cheap, and the
        // payoff is the multi-language audio picker the Korean .mkv use
        // case explicitly needs (Bug D / Continuation 7).
        try
        {
            var playbackItem = new MediaPlaybackItem(source);
            if (playbackItem.AudioTracks is { Count: > 0 } audio)
            {
                for (int i = 0; i < audio.Count; i++)
                {
                    var a = audio[i];
                    tracks.Add(new EmbeddedTrackInfo(
                        Kind: EmbeddedTrackKind.Audio,
                        StreamIndex: i,
                        Language: NullIfBlank(a.Language),
                        Label: NullIfBlank(a.Label),
                        Codec: null,             // EncodingProperties is async; skip for the probe pass
                        IsDefault: i == 0));     // Best-effort: WinRT doesn't expose a "default" flag
                }
            }
            // Embedded timed-text (subtitles inside .mkv etc.) ride
            // TimedMetadataTracks on the playback item, NOT
            // ExternalTimedMetadataTracks (which is for sidecar tracks
            // attached programmatically). Walk both so we cover .mkv
            // subtitle streams that the external loop above missed.
            if (playbackItem.TimedMetadataTracks is { Count: > 0 } tm)
            {
                for (int i = 0; i < tm.Count; i++)
                {
                    var t = tm[i];
                    tracks.Add(new EmbeddedTrackInfo(
                        Kind: EmbeddedTrackKind.Subtitle,
                        StreamIndex: i,
                        Language: NullIfBlank(t.Language),
                        Label: NullIfBlank(t.Label),
                        Codec: t.TimedMetadataKind.ToString(),
                        IsDefault: false));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaPlaybackItem track enumeration failed for {Path}", filePath);
        }

        return tracks;
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
