using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Wavee.Backend;

namespace Wavee;

// ── USER-ATTACHED LOCAL VIDEO OVERRIDES — the warm, synchronous view ──────────────────────────────────────────────────
// The persisted roster lives in SQLite (`video_override`, schema v4) behind IStore; THIS is the warm mirror every hot
// caller reads. It exists because the decision "should this playable play as video?" is asked from playback/dealer
// threads — including for the NEXT track, by uri, before CurrentTrack has moved — so it must be an allocation-free
// dictionary lookup, never a signal read and never a SQLite round-trip.
//
// Engine-free by construction (IStore + WaveeLogger + BCL): the whole tier-1 decision is unit-testable headlessly, and
// CompositeVideoResolver is only the thin shell that maps a decision onto a PopOutVideoSource.

/// <summary>Which tier-1 branch a playable takes before the source's own video resolver is consulted.</summary>
public enum VideoOverrideTier
{
    /// <summary>No attachment for this playable — fall through to the source tier.</summary>
    None,
    /// <summary>An attachment exists and its file is present: play it (it always wins over the source's own video).</summary>
    UseOverride,
    /// <summary>An attachment exists but its file is gone (moved / drive offline). Fall through, KEEP the link for repair,
    /// and surface it once per session — never delete the user's curation behind their back.</summary>
    Broken,
    /// <summary>An attachment exists but this exact (uri, source key) already failed to open this session. Skip tier 1
    /// silently — this is the one-shot fallback latch that makes a bad file impossible to loop on.</summary>
    Quarantined,
}

/// <summary>The pure tier-1 outcome: the branch plus (for the two "we have a record" branches) the record itself.</summary>
public readonly record struct VideoOverrideDecision(VideoOverrideTier Tier, VideoOverride Override)
{
    public static VideoOverrideDecision None => new(VideoOverrideTier.None, default);
    /// <summary>True when the resolver should stop here and play <see cref="Override"/>.</summary>
    public bool Wins => Tier == VideoOverrideTier.UseOverride;
}

/// <summary>The warm, synchronous view of the user's video-override curation, plus the per-session quarantine that keeps
/// an unplayable file from looping. One instance per backend; attached to <c>PlaybackBridge</c> and to
/// <c>CompositeVideoResolver</c> at composition. With no instance attached every override path is unreachable — the
/// feature's kill switch.</summary>
public sealed class VideoOverrideService
{
    /// <summary>The logging category for store/service events (play-time events stay on "playback", UI on "ui").</summary>
    public const string LogCategory = "video.local";

    readonly IStore _store;
    readonly WaveeLogger _log;
    // Warm mirror of the persisted roster. Ordinal-keyed by the exact playable uri; read from playback/dealer threads.
    readonly ConcurrentDictionary<string, VideoOverride> _warm = new(StringComparer.Ordinal);
    // Per-SESSION only (never persisted): (uri, source key) pairs whose file failed to open. A restart is a fresh chance,
    // which is the honest behavior for "the codec might now be installed / the drive is back".
    readonly ConcurrentDictionary<string, byte> _quarantine = new(StringComparer.Ordinal);
    // Per-SESSION missing-file signatures, so a broken link warns ONCE rather than on every replay of the same track.
    readonly ConcurrentDictionary<string, byte> _warnedMissing = new(StringComparer.Ordinal);

    public VideoOverrideService(IStore store, WaveeLogger log = default)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log.Sink is null ? log : log.With(LogCategory);
        Reload();
    }

    /// <summary>Existence probe for an override's file. Injectable purely so the tier-1 decision is testable without a
    /// disk; production is <see cref="File.Exists"/>.</summary>
    public Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>Raised (on the caller's thread) after every attach/replace/remove with the affected playable uri and the
    /// mutation kind. The app wires this to <c>PlaybackBridge.NotifyVideoOverrideChanged</c>, which is the ONE mutation
    /// entry point that applies <see cref="VideoOverrideMutationCore.Plan"/> (latch clears, availability, force-reload).</summary>
    public Action<string, OverrideMutationKind>? OnChanged;

    /// <summary>Raised at most ONCE per session per (uri, path) when a play-time resolve finds the file gone. The app
    /// turns it into a single non-blocking warning; playback has already fallen through to the original.</summary>
    public Action<string>? OnBrokenLink;

    /// <summary>Re-read the whole roster from the store into the warm mirror (startup / after a backend swap).</summary>
    public void Reload()
    {
        _warm.Clear();
        var rows = _store.VideoOverrides();
        for (int i = 0; i < rows.Count; i++) _warm[rows[i].Uri] = rows[i];
        if (rows.Count > 0) _log.Info($"video overrides loaded: {rows.Count}");
    }

    // ── the hot reads (allocation-free dictionary lookups; called from playback/dealer threads) ───────────────────────

    /// <summary>Is an override attached to this playable? A single ordinal dictionary probe — this is what
    /// <c>ShouldPlayAsVideo</c> / <c>RecomputeHasVideo</c> OR into the has-video answer, for the CURRENT and the NEXT
    /// playable alike. Quarantine is deliberately NOT folded in: a quarantined attachment still means "the user wants
    /// video here", it just resolves to the source's own video (or to audio) instead of the broken file.</summary>
    public bool Has(string? playableUri) => playableUri is { Length: > 0 } && _warm.ContainsKey(playableUri);

    /// <summary>The attachment for this playable, if any.</summary>
    public bool TryGetActive(string? playableUri, out VideoOverride o)
    {
        if (playableUri is { Length: > 0 }) return _warm.TryGetValue(playableUri, out o);
        o = default;
        return false;
    }

    /// <summary>The whole roster (Settings list). Allocates — never call it from a playback path.</summary>
    public IReadOnlyList<VideoOverride> All()
    {
        var list = new List<VideoOverride>(_warm.Count);
        foreach (var kv in _warm) list.Add(kv.Value);
        return list;
    }

    public int Count => _warm.Count;

    // ── the PURE tier-1 decision (the whole of CompositeVideoResolver's first tier) ───────────────────────────────────

    /// <summary>Decide what tier 1 does for this playable: play the attachment, fall through because its file is gone,
    /// skip because it already failed this session, or fall through because there is no attachment. Side-effect free —
    /// the caller does the logging/notification for the branch it took, which is what keeps this unit-testable.</summary>
    public VideoOverrideDecision Decide(string? playableUri)
    {
        if (!TryGetActive(playableUri, out var o)) return VideoOverrideDecision.None;
        if (IsQuarantined(o.Uri, o.SourceKey)) return new VideoOverrideDecision(VideoOverrideTier.Quarantined, o);
        bool exists;
        try { exists = FileExists(o.Path); }
        catch { exists = false; }   // an unreachable UNC/offline drive throws — treat exactly like "gone" (link kept)
        return new VideoOverrideDecision(exists ? VideoOverrideTier.UseOverride : VideoOverrideTier.Broken, o);
    }

    // ── mutations ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Attach (or REPLACE — the uri is the primary key, so a duplicate attach IS the replace) a local video file
    /// to a playable. The file is LINKED: the absolute normalized path is stored, nothing is copied or moved. Returns the
    /// persisted record. Throws <see cref="ArgumentException"/> for an empty uri/path.</summary>
    public VideoOverride Attach(string playableUri, string path)
    {
        if (string.IsNullOrWhiteSpace(playableUri)) throw new ArgumentException("playable uri is required", nameof(playableUri));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required", nameof(path));

        string full = NormalizePath(path);
        string id = IdFor(full);
        long size = 0, mtime = 0;
        try
        {
            var info = new FileInfo(full);
            if (info.Exists) { size = info.Length; mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(); }
        }
        catch { /* stat failure is a staleness hint at worst — never a reason to refuse the attachment */ }

        bool replaced = _warm.ContainsKey(playableUri);
        var o = new VideoOverride(playableUri, full, id, 0, size, mtime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _warm[playableUri] = o;
        // A replace re-arms the file: the NEW (uri, key) was never quarantined, and dropping the old one lets the user
        // repair a bad attachment by re-picking the same path after fixing it.
        _quarantine.TryRemove(QuarantineKey(playableUri, o.SourceKey), out _);
        _warnedMissing.TryRemove(MissingKey(o), out _);
        _store.UpsertVideoOverride(o);
        _log.Event(WaveeLogLevel.Info, replaced ? "override.replace" : "override.attach",
            replaced ? "replaced the attached video" : "attached a local video",
            fields: [WaveeLogField.Of("uri", playableUri), WaveeLogField.Of("path", full), WaveeLogField.Of("sizeBytes", size)]);
        OnChanged?.Invoke(playableUri, replaced ? OverrideMutationKind.Replace : OverrideMutationKind.Attach);
        return o;
    }

    /// <summary>Detach the override from a playable. Never touches the file on disk. Returns false when nothing was
    /// attached (a no-op, so no signal and no notification).</summary>
    public bool Remove(string playableUri)
    {
        if (playableUri is not { Length: > 0 } || !_warm.TryRemove(playableUri, out var o)) return false;
        _quarantine.TryRemove(QuarantineKey(playableUri, o.SourceKey), out _);
        _warnedMissing.TryRemove(MissingKey(o), out _);
        _store.RemoveVideoOverride(playableUri);
        _log.Event(WaveeLogLevel.Info, "override.remove", "detached the local video",
            fields: [WaveeLogField.Of("uri", playableUri), WaveeLogField.Of("path", o.Path)]);
        OnChanged?.Invoke(playableUri, OverrideMutationKind.Remove);
        return true;
    }

    /// <summary>Record the media engine's authoritative duration for an attachment (the mp4's real length). Persisted so
    /// the roster can show it; does NOT notify (it is a metadata refinement, not a curation change).</summary>
    public void NoteDuration(string playableUri, long durationMs)
    {
        if (durationMs <= 0 || !TryGetActive(playableUri, out var o) || o.DurationMs == durationMs) return;
        var updated = o with { DurationMs = durationMs };
        _warm[playableUri] = updated;
        _store.UpsertVideoOverride(updated);
    }

    // ── per-session quarantine + the one-shot notices ────────────────────────────────────────────────────────────────

    /// <summary>Latch this exact (playable, source key) as unplayable for the rest of the session, so the next resolve
    /// skips tier 1 and the fallback can never loop. Cleared by a replace/remove, and by a restart.</summary>
    public void Quarantine(string playableUri, string sourceKey)
    {
        if (playableUri is not { Length: > 0 } || sourceKey is not { Length: > 0 }) return;
        _quarantine[QuarantineKey(playableUri, sourceKey)] = 1;
        _log.Event(WaveeLogLevel.Warning, "override.open_failed", "the attached video could not be played — falling back",
            fields: [WaveeLogField.Of("uri", playableUri), WaveeLogField.Of("key", sourceKey)]);
    }

    public bool IsQuarantined(string playableUri, string sourceKey)
        => playableUri is { Length: > 0 } && sourceKey is { Length: > 0 } && _quarantine.ContainsKey(QuarantineKey(playableUri, sourceKey));

    /// <summary>A play-time resolve found the attached file missing. Logs at Warning and raises <see cref="OnBrokenLink"/>
    /// AT MOST ONCE per session per (uri, path) — a broken link is a quiet fallback, not a repeated interruption.</summary>
    public void NoteBroken(string playableUri, in VideoOverride o)
    {
        if (playableUri is not { Length: > 0 }) return;
        if (!_warnedMissing.TryAdd(MissingKey(o), 1)) return;
        _log.Event(WaveeLogLevel.Warning, "override.missing", "the attached video file is missing — playing the original",
            fields: [WaveeLogField.Of("uri", playableUri), WaveeLogField.Of("path", o.Path)]);
        OnBrokenLink?.Invoke(playableUri);
    }

    /// <summary>An override won tier 1 and is about to be played (once per load).</summary>
    public void NoteResolved(string playableUri, in VideoOverride o)
        => _log.Event(WaveeLogLevel.Info, "override.resolved", "playing the attached video",
            fields: [WaveeLogField.Of("uri", playableUri), WaveeLogField.Of("key", o.SourceKey)]);

    // ── identity ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Absolute, canonical form of a user-picked path. Falls back to the input when the OS refuses it (a
    /// malformed path is still worth storing verbatim — the roster shows it as Missing rather than losing the record).</summary>
    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>The stable per-file id: the first 16 hex chars of SHA-256 over the CASE-FOLDED normalized path. Case
    /// folding matches Windows path semantics, so re-picking the same file with different casing is the same identity
    /// (and therefore the same remount key). The content is deliberately NOT hashed — these are multi-GB files.</summary>
    public static string IdFor(string normalizedPath)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath.ToLowerInvariant()), hash);
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    static string QuarantineKey(string uri, string sourceKey) => uri + " " + sourceKey;
    static string MissingKey(in VideoOverride o) => o.Uri + " " + o.Path;
}

/// <summary>The has-video LATCH, extracted from <c>PlaybackBridge.RecomputeHasVideo</c> so its one genuinely tricky rule
/// is unit-testable. Once a playable is known to have a video, a later transient false for the SAME uri is a read glitch
/// (the association store is never evicted) and must not commit an availability downgrade — that would cost a full
/// Video→Audio→Video media-kind round trip. The one thing that MUST get through is a real user removal, which is why
/// <see cref="ClearFor"/> exists: it is the difference between "detach did nothing" and "detach worked".</summary>
public static class HasVideoLatch
{
    /// <summary>Fold the latch over a freshly computed has-video answer. Returns the answer to publish and updates the
    /// latched uri in place.</summary>
    public static bool Apply(bool has, string? uri, ref string? latchedUri)
    {
        if (has) { latchedUri = uri; return true; }
        if (latchedUri is not null && (uri is null || string.Equals(uri, latchedUri, StringComparison.Ordinal)))
            return true;   // transient glitch on the latched playable — suppress the downgrade
        return false;
    }

    /// <summary>End the latch for one playable (a real user mutation — an override removal — not a read glitch), so the
    /// next recompute is allowed to publish true→false.</summary>
    public static void ClearFor(string? uri, ref string? latchedUri)
    {
        if (uri is { Length: > 0 } && string.Equals(uri, latchedUri, StringComparison.Ordinal)) latchedUri = null;
    }
}

/// <summary>The DEAD-VIDEO latch: the backend has PROVEN, for one playable, that no video is actually playing (the media
/// host handed back no player and the current media is not video — a fallback to audio, an open failure, or the surface
/// being closed mid-load). Availability alone cannot express that: it is computed from "does this uri have a video
/// association", which stays true, so a mounted surface keeps presenting a source that will never arrive and shows its
/// indeterminate "Loading" poster FOREVER (there is no timeout anywhere in the surfaces — that is the observed
/// "video still on screen, paused, with an endless buffering indicator").
/// <para>It is scoped to ONE playable and cleared on a real track change, so it can never become a sticky "no video on
/// this account". It is deliberately NOT an intent write: <c>Requested</c> is untouched, so the user's standing
/// "watch video" survives and the very next video-bearing track opens the surface exactly as before.</para></summary>
public static class VideoMediaLatch
{
    /// <summary>Mark <paramref name="uri"/> as having no live video media. Returns true when this CHANGED the latch (so
    /// the caller only recomputes/republishes on a real edge).</summary>
    public static bool MarkDead(string? uri, ref string? deadUri)
    {
        if (uri is not { Length: > 0 } || string.Equals(uri, deadUri, StringComparison.Ordinal)) return false;
        deadUri = uri;
        return true;
    }

    /// <summary>Is this playable latched dead? A null/empty uri is never dead (nothing is playing).</summary>
    public static bool IsDead(string? uri, string? deadUri)
        => uri is { Length: > 0 } && string.Equals(uri, deadUri, StringComparison.Ordinal);

    /// <summary>Fold the latch over a computed has-video answer. Applied AFTER <see cref="HasVideoLatch"/>: a proven
    /// "there is no video media" is a FACT and must beat the glitch-suppression latch, which only exists to absorb a
    /// transient read.</summary>
    public static bool Apply(bool has, string? uri, string? deadUri) => has && !IsDead(uri, deadUri);

    /// <summary>A real track change (or an explicit re-arm, e.g. the user attaching a video to this playable) ends the
    /// latch — the next playable gets a clean slate.</summary>
    public static void ClearFor(string? uri, ref string? deadUri)
    {
        if (uri is null || string.Equals(uri, deadUri, StringComparison.Ordinal)) deadUri = null;
    }
}
