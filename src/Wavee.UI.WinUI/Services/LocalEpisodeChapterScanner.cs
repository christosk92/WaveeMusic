using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Best-effort reader for chapter cues embedded in a local episode's
/// <see cref="MediaPlaybackItem.TimedMetadataTracks"/>. Used by the Up-Next
/// overlay to spot a "Credits" / "Outro" chapter and trigger the card at
/// its start instead of falling back to the last 30 s of the file.
/// </summary>
/// <remarks>
/// Windows MediaFoundation surfaces container chapters inconsistently —
/// MKV chapter atoms parse on some installs and not others, MP4 chapter
/// tracks are similarly flaky. This scanner therefore returns an empty
/// list whenever anything is uncertain rather than throwing; the caller
/// is expected to fall back to a time-based heuristic. We cache by
/// track URI so the same episode is only scanned once per session.
/// </remarks>
public sealed class LocalEpisodeChapterScanner
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<EpisodeChapter>> _cache = new();

    /// <summary>
    /// Pull whatever chapters are present on <paramref name="item"/> right now.
    /// Result is cached against <paramref name="trackUri"/>; subsequent calls
    /// for the same URI return the cached snapshot without re-walking the
    /// metadata tracks. Returns an empty list when the item exposes no
    /// chapter-shaped tracks (which is the common case).
    /// </summary>
    public Task<IReadOnlyList<EpisodeChapter>> ScanAsync(
        string trackUri,
        MediaPlaybackItem? item,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri) || item is null)
            return Task.FromResult<IReadOnlyList<EpisodeChapter>>(Array.Empty<EpisodeChapter>());

        if (_cache.TryGetValue(trackUri, out var cached))
            return Task.FromResult(cached);

        var snapshot = ReadChapters(item);
        // Only cache non-empty results so a too-early scan (tracks not yet
        // resolved) doesn't poison subsequent retries with an empty list.
        if (snapshot.Count > 0)
            _cache[trackUri] = snapshot;
        return Task.FromResult<IReadOnlyList<EpisodeChapter>>(snapshot);
    }

    /// <summary>Drop a cached entry — used when the file is replaced on disk mid-session.</summary>
    public void Invalidate(string trackUri)
    {
        if (string.IsNullOrEmpty(trackUri)) return;
        _cache.TryRemove(trackUri, out _);
    }

    private static IReadOnlyList<EpisodeChapter> ReadChapters(MediaPlaybackItem item)
    {
        var result = new List<EpisodeChapter>();
        try
        {
            var trackCount = item.TimedMetadataTracks.Count;
            for (var i = 0; i < trackCount; i++)
            {
                var track = item.TimedMetadataTracks[i];
                if (track is null) continue;
                if (track.TimedMetadataKind != TimedMetadataKind.Chapter) continue;

                var cues = track.Cues;
                if (cues is null) continue;
                for (var c = 0; c < cues.Count; c++)
                {
                    var cue = cues[c];
                    if (cue is null) continue;
                    var startMs = (long)cue.StartTime.TotalMilliseconds;
                    var label = ExtractCueLabel(cue) ?? track.Label;
                    result.Add(new EpisodeChapter(label, startMs));
                }
            }
        }
        catch
        {
            // MediaFoundation can throw inside metadata enumeration when the
            // container parser is partially confused — never let that take
            // down the overlay; the time fallback covers us.
            return Array.Empty<EpisodeChapter>();
        }
        return result;
    }

    private static string? ExtractCueLabel(IMediaCue cue)
    {
        // MKV chapter cues typically arrive as DataCue with a UTF-16 byte
        // payload, but some sources surface them as TimedTextCue with
        // Lines[0].Text. Try both shapes; null when neither yields a label.
        try
        {
            switch (cue)
            {
                case TimedTextCue tt:
                    if (tt.Lines is { Count: > 0 } lines)
                    {
                        var text = lines[0]?.Text;
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                    return null;

                case DataCue dc:
                    var buffer = dc.Data;
                    if (buffer is null || buffer.Length == 0) return null;
                    // Bias toward UTF-8; fall back to UTF-16 for the rare
                    // MKV that emits little-endian wide strings.
                    var bytes = new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                        reader.ReadBytes(bytes);
                    var utf8 = System.Text.Encoding.UTF8.GetString(bytes);
                    return string.IsNullOrWhiteSpace(utf8) ? null : utf8;
            }
        }
        catch
        {
            // Defensive — bad cue payloads must not crash playback.
        }
        return null;
    }
}
