using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Wavee.Core;

namespace Wavee.Backend;

/// <summary>
/// Copy-paste friendly diagnostics for the playback queue/bucket pipeline.
/// Category: playback.buckets.
/// </summary>
internal static class PlaybackBucketDiagnostics
{
    const int MaxRows = 80;
    const string Category = "playback.buckets";

    public static void Startup(string source, string message, params WaveeLogField[] fields)
        => WaveeLog.Instance.Info(Category, "startup", source + " - " + message, fields);

    /// <summary>Info-level shuffle-toggle attribution (lands in the log file): the surface that flipped it, whether the
    /// state actually changed, the anchor row's provider, and the session context.</summary>
    public static void ShuffleToggle(string source, bool on, bool changed, QueueSnapshot snap)
        => WaveeLog.Instance.Info(Category, "shuffle.toggle",
            "source=" + source
            + " on=" + (on ? "1" : "0")
            + " changed=" + (changed ? "1" : "0")
            + " anchorProvider=" + (snap.Current?.Provider.ToWire() ?? "-")
            + " ctx=" + Safe(snap.ContextUri)
            + " upcoming=" + snap.Upcoming.Length.ToString(CultureInfo.InvariantCulture),
            WaveeLogField.Of("source", source),
            WaveeLogField.Of("on", on),
            WaveeLogField.Of("changed", changed),
            WaveeLogField.Of("anchorProvider", snap.Current?.Provider.ToWire() ?? ""),
            WaveeLogField.Of("ctx", snap.ContextUri ?? ""),
            WaveeLogField.Of("upcoming", snap.Upcoming.Length));

    public static void Continuation(string eventId, string message, params WaveeLogField[] fields)
        => WaveeLog.Instance.Debug(Category, eventId, message, fields);

    public static void QueueIfChanged(ref string? lastSignature, string reason, IReadOnlyList<QueueEntry> queue,
        string? contextUri = null, string? currentUri = null, int remainingInContext = -1, long revision = -1)
    {
        if (!WaveeLog.Instance.IsEnabled(WaveeLogLevel.Trace)) return;
        string sig = QueueSignature(queue, contextUri, currentUri, remainingInContext, revision);
        if (string.Equals(sig, lastSignature, StringComparison.Ordinal)) return;
        lastSignature = sig;
        Queue(reason, queue, contextUri, currentUri, remainingInContext, revision);
    }

    public static void Queue(string reason, IReadOnlyList<QueueEntry> queue,
        string? contextUri = null, string? currentUri = null, int remainingInContext = -1, long revision = -1)
    {
        if (!WaveeLog.Instance.IsEnabled(WaveeLogLevel.Trace)) return;
        CountEntries(queue, out int now, out int user, out int nextContext, out int nextAutoplay, out int history);
        string rows = Rows(queue);
        // Trace: embeds the full per-row queue dump (~3 KB) — far too voluminous for the Info file. The compact reason/counts
        // ride along in the same line, so the whole snapshot drops together.
        WaveeLog.Instance.Event(WaveeLogLevel.Trace, Category, "queue.snapshot",
            "reason=" + reason
            + " rev=" + revision.ToString(CultureInfo.InvariantCulture)
            + " total=" + queue.Count.ToString(CultureInfo.InvariantCulture)
            + " now=" + now.ToString(CultureInfo.InvariantCulture)
            + " user=" + user.ToString(CultureInfo.InvariantCulture)
            + " next.context=" + nextContext.ToString(CultureInfo.InvariantCulture)
            + " next.autoplay=" + nextAutoplay.ToString(CultureInfo.InvariantCulture)
            + " history=" + history.ToString(CultureInfo.InvariantCulture)
            + " remainingContext=" + remainingInContext.ToString(CultureInfo.InvariantCulture)
            + " ctx=" + Safe(contextUri)
            + " current=" + Safe(currentUri)
            + " rows=[" + rows + "]",
            fields:
            [
                WaveeLogField.Of("reason", reason),
                WaveeLogField.Of("rev", revision),
                WaveeLogField.Of("total", queue.Count),
                WaveeLogField.Of("now", now),
                WaveeLogField.Of("user", user),
                WaveeLogField.Of("nextContext", nextContext),
                WaveeLogField.Of("nextAutoplay", nextAutoplay),
                WaveeLogField.Of("history", history),
                WaveeLogField.Of("ctx", contextUri ?? ""),
                WaveeLogField.Of("current", currentUri ?? ""),
            ]);
    }

    /// <summary>Free-form UI-side event on the same category (queue PANEL row builds etc.) — change-gate at the caller
    /// via <paramref name="lastSignature"/> so steady re-renders log nothing.</summary>
    public static void UiIfChanged(ref string? lastSignature, string eventId, string message)
    {
        if (!WaveeLog.Instance.IsEnabled(WaveeLogLevel.Trace)) return;
        if (string.Equals(message, lastSignature, StringComparison.Ordinal)) return;
        lastSignature = message;
        // Trace: the caller (queue panel) passes a full per-row dump here.
        WaveeLog.Instance.Event(WaveeLogLevel.Trace, Category, eventId, message);
    }

    public static void RemoteClusterIfChanged(ref string? lastSignature, string reason, in ClusterDelta c)
    {
        if (!WaveeLog.Instance.IsEnabled(WaveeLogLevel.Trace)) return;
        string sig = RemoteSignature(c);
        if (string.Equals(sig, lastSignature, StringComparison.Ordinal)) return;
        lastSignature = sig;

        CountRemote(c.NextTracks, out int nextQueue, out int nextContext, out int nextAutoplay, out int nextDelimiter);
        CountRemote(c.PrevTracks ?? Array.Empty<RemoteTrack>(), out int prevQueue, out int prevContext, out int prevAutoplay, out int prevDelimiter);
        // Trace: embeds the full next.rows/prev.rows dumps.
        WaveeLog.Instance.Event(WaveeLogLevel.Trace, Category, "remote.cluster",
            "reason=" + reason
            + " active=" + Safe(c.ActiveDeviceId)
            + " ctx=" + Safe(c.ContextUri)
            + " track=" + Safe(c.HasTrack ? c.Track.Uri : "")
            + " queueRevision=" + Safe(c.QueueRevision)
            + " next.count=" + c.NextTracks.Count.ToString(CultureInfo.InvariantCulture)
            + " next.queue=" + nextQueue.ToString(CultureInfo.InvariantCulture)
            + " next.context=" + nextContext.ToString(CultureInfo.InvariantCulture)
            + " next.autoplay=" + nextAutoplay.ToString(CultureInfo.InvariantCulture)
            + " next.delimiter=" + nextDelimiter.ToString(CultureInfo.InvariantCulture)
            + " prev.count=" + ((c.PrevTracks?.Count) ?? 0).ToString(CultureInfo.InvariantCulture)
            + " prev.queue=" + prevQueue.ToString(CultureInfo.InvariantCulture)
            + " prev.context=" + prevContext.ToString(CultureInfo.InvariantCulture)
            + " prev.autoplay=" + prevAutoplay.ToString(CultureInfo.InvariantCulture)
            + " prev.delimiter=" + prevDelimiter.ToString(CultureInfo.InvariantCulture)
            + " next.rows=[" + RemoteRows(c.NextTracks) + "]"
            + " prev.rows=[" + RemoteRows(c.PrevTracks ?? Array.Empty<RemoteTrack>()) + "]",
            fields:
            [
                WaveeLogField.Of("reason", reason),
                WaveeLogField.Of("active", c.ActiveDeviceId),
                WaveeLogField.Of("ctx", c.ContextUri ?? ""),
                WaveeLogField.Of("track", c.HasTrack ? c.Track.Uri : ""),
                WaveeLogField.Of("revision", c.QueueRevision ?? ""),
                WaveeLogField.Of("next", c.NextTracks.Count),
                WaveeLogField.Of("prev", c.PrevTracks?.Count ?? 0),
            ]);
    }

    static string QueueSignature(IReadOnlyList<QueueEntry> queue, string? contextUri, string? currentUri, int remainingInContext, long revision)
    {
        var sb = new StringBuilder(128 + queue.Count * 32);
        sb.Append(revision).Append('|').Append(contextUri).Append('|').Append(currentUri).Append('|').Append(remainingInContext);
        for (int i = 0; i < queue.Count; i++)
        {
            var e = queue[i];
            sb.Append('|').Append(e.EntryId)
                .Append(':').Append(e.Bucket)
                .Append(':').Append(e.Provider.ToWire())
                .Append(':').Append(e.IsAutoplay ? '1' : '0')
                .Append(':').Append(e.Uid)
                .Append(':').Append(e.Track.Uri);
        }
        return sb.ToString();
    }

    static string RemoteSignature(in ClusterDelta c)
    {
        var sb = new StringBuilder(128 + c.NextTracks.Count * 32 + (c.PrevTracks?.Count ?? 0) * 32);
        sb.Append(c.ActiveDeviceId).Append('|').Append(c.ContextUri).Append('|')
            .Append(c.HasTrack ? c.Track.Uri : "").Append('|').Append(c.QueueRevision);
        AppendRemoteSig(sb, c.NextTracks, "n");
        AppendRemoteSig(sb, c.PrevTracks ?? Array.Empty<RemoteTrack>(), "p");
        return sb.ToString();
    }

    static void AppendRemoteSig(StringBuilder sb, IReadOnlyList<RemoteTrack> tracks, string prefix)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            sb.Append('|').Append(prefix).Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(t.Provider)
                .Append(':').Append(t.Uid)
                .Append(':').Append(t.Uri);
        }
    }

    static void CountEntries(IReadOnlyList<QueueEntry> queue, out int now, out int user,
        out int nextContext, out int nextAutoplay, out int history)
    {
        now = user = nextContext = nextAutoplay = history = 0;
        for (int i = 0; i < queue.Count; i++)
        {
            var e = queue[i];
            switch (e.Bucket)
            {
                case QueueBucket.NowPlaying: now++; break;
                case QueueBucket.UserQueue: user++; break;
                case QueueBucket.History: history++; break;
                case QueueBucket.NextUp:
                    if (IsAutoplay(e)) nextAutoplay++;
                    else nextContext++;
                    break;
            }
        }
    }

    static void CountRemote(IReadOnlyList<RemoteTrack> tracks, out int queue, out int context,
        out int autoplay, out int delimiter)
    {
        queue = context = autoplay = delimiter = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Uri == "spotify:delimiter") { delimiter++; continue; }
            string provider = string.IsNullOrEmpty(tracks[i].Provider) ? "context" : tracks[i].Provider;
            if (string.Equals(provider, "queue", StringComparison.OrdinalIgnoreCase)) queue++;
            else if (string.Equals(provider, "autoplay", StringComparison.OrdinalIgnoreCase)) autoplay++;
            else context++;
        }
    }

    static string Rows(IReadOnlyList<QueueEntry> queue)
    {
        if (queue.Count == 0) return "";
        var sb = new StringBuilder(queue.Count * 96);
        int n = Math.Min(queue.Count, MaxRows);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append("; ");
            var e = queue[i];
            sb.Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(" id=").Append(Safe(e.EntryId))
                .Append(" itemId=").Append(e.ItemId.Value.ToString(CultureInfo.InvariantCulture))
                .Append(" bucket=").Append(e.Bucket)
                .Append(" provider=").Append(Safe(e.Provider.ToWire()))
                .Append(" autoplay=").Append(IsAutoplay(e) ? "1" : "0")
                .Append(" uid=").Append(Safe(e.Uid))
                .Append(" uri=").Append(Safe(e.Track.Uri))
                .Append(" title=").Append(Quote(e.Track.Title));
        }
        if (queue.Count > n) sb.Append("; ... +").Append((queue.Count - n).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    static string RemoteRows(IReadOnlyList<RemoteTrack> tracks)
    {
        if (tracks.Count == 0) return "";
        var sb = new StringBuilder(tracks.Count * 96);
        int n = Math.Min(tracks.Count, MaxRows);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append("; ");
            var t = tracks[i];
            string provider = string.IsNullOrEmpty(t.Provider) ? "context" : t.Provider;
            sb.Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(" provider=").Append(Safe(provider))
                .Append(" uid=").Append(Safe(t.Uid))
                .Append(" uri=").Append(Safe(t.Uri))
                .Append(" title=").Append(Quote(t.Title));
        }
        if (tracks.Count > n) sb.Append("; ... +").Append((tracks.Count - n).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    static bool IsAutoplay(QueueEntry e) =>
        e.IsAutoplay || e.Provider == QueueProvider.Autoplay;

    static string Safe(string? s) => string.IsNullOrEmpty(s) ? "-" : s.Replace('\r', ' ').Replace('\n', ' ');

    static string Quote(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        var v = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace('\r', ' ').Replace('\n', ' ');
        return "\"" + v + "\"";
    }
}
