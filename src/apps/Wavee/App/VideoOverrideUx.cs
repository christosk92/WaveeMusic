using System;
using System.Collections.Generic;
using System.IO;
using Wavee.Backend;
using Wavee.Backend.MediaSources;
using Wavee.Core;

namespace Wavee;

// ── THE UX DECISION CORE for the local video-override surfaces (P3) ───────────────────────────────────────────────────
// Everything the context menu, the Settings roster and the row indicator DECIDE — which rows exist, which status a link
// reports, what a picked file is allowed to be, where a "Locate…" picker should start — lives here, engine-free
// (VideoOverrideService + Wavee.Core + BCL). The engine-bound files (Actions/VideoActions.cs, Menus.cs,
// SettingsPage.VideoOverrides.cs, TrackRow.cs) are thin adapters: they render what this decides and never re-decide it.
// That is what makes the whole P3 surface unit-testable headlessly, exactly like ActionRules/MediaSwitchLogic.

/// <summary>The status chip a curated attachment shows in Settings (and the badge language the toasts reuse).</summary>
public enum VideoOverrideStatus : byte
{
    /// <summary>The file is where the user left it — this attachment plays.</summary>
    Ok,
    /// <summary>The file is gone but its volume is still mounted: a move/rename. "Locate…" repairs it.</summary>
    Missing,
    /// <summary>The whole volume is absent (unplugged drive / offline share). NOT a repair prompt — it heals by itself
    /// when the drive comes back, so the copy must never suggest removing the link.</summary>
    DriveOffline,
    /// <summary>The file exists but failed to open this session (bad codec / corrupt container). Quarantined until a
    /// replace or a restart; no Retry CTA, because retrying the same file is not a fix.</summary>
    Unplayable,
}

/// <summary>Why an attach was refused. Validation is deliberately shallow — extension + existence — because a deep MF
/// probe would block the UI and the honest deep failure surfaces at play time through the recovery hook.</summary>
public enum VideoAttachRejection : byte
{
    /// <summary>Accepted.</summary>
    None,
    /// <summary>Not an <c>.mp4</c>.</summary>
    NotMp4,
    /// <summary>The path does not point at an existing file.</summary>
    NotFound,
}

/// <summary>Which rows the <c>Video ▸</c> submenu builds for a playable, decided at open time from the curation state.
/// Attach and Replace are mutually exclusive (the uri is the primary key, so a duplicate attach IS the replace).</summary>
[Flags]
public enum VideoMenuItems : byte
{
    None = 0,
    /// <summary>"Attach video file…" — nothing attached yet.</summary>
    Attach = 1,
    /// <summary>"Replace video file…" — something is attached.</summary>
    Replace = 2,
    /// <summary>"Remove video" (destructive, behind a separator; applies immediately + toast-undo, never a dialog).</summary>
    Remove = 4,
    /// <summary>"Locate video file…" — the link is broken, so offer the repair pick.</summary>
    Locate = 8,
    /// <summary>"Show in Explorer" — the file is present, so revealing it is meaningful.</summary>
    ShowInExplorer = 16,
}

/// <summary>One Settings roster row: the persisted attachment plus everything the row renders, resolved once at load
/// time (never per frame). <see cref="Title"/>/<see cref="Subtitle"/> fall back to the uri when the store has never seen
/// the playable — a device-wide roster outlives any one account's catalog.</summary>
public readonly record struct VideoOverrideRow(
    VideoOverride Override, VideoOverrideStatus Status, string Title, string? Subtitle, string FileName)
{
    public string Uri => Override.Uri;
    public string Path => Override.Path;
    /// <summary>The link is broken in a way "Locate video file…" can repair (a move/rename, not an absent volume).</summary>
    public bool CanLocate => Status == VideoOverrideStatus.Missing;
    /// <summary>Revealing the file in Explorer only makes sense while it is actually there.</summary>
    public bool CanReveal => Status is VideoOverrideStatus.Ok or VideoOverrideStatus.Unplayable;
}

/// <summary>What the attachment manager's ROOT view is showing right now. A pure function of "how many attachments
/// exist" × "is the user searching" × "did the search hit anything" — so the flyout never re-decides it inline.</summary>
public enum VideoManagerSection : byte
{
    /// <summary>Nothing is attached at all: teach the context-menu attach path instead of showing an empty list.</summary>
    Empty,
    /// <summary>The resting root: the newest few attachments plus the "Browse all…" drill-in.</summary>
    Recent,
    /// <summary>A live query with hits — the results take the place of the recent section (no drill required).</summary>
    Results,
    /// <summary>A live query that matched nothing.</summary>
    NoMatches,
}

/// <summary>The pure decisions behind every video-override UX surface.</summary>
public static class VideoOverrideUx
{
    /// <summary>How many attachments the manager's "Recently added" section shows before the user has to Browse all.
    /// Small on purpose: the section answers "did the thing I just attached land?", not "what do I own".</summary>
    public const int RecentCount = 4;

    /// <summary>The one roster ordering: newest attachment first, ties broken by uri so the list can never shuffle
    /// between two rows attached in the same second.</summary>
    static readonly Comparison<VideoOverrideRow> RecencyOrder = static (a, b) =>
    {
        int c = b.Override.AddedAtUnix.CompareTo(a.Override.AddedAtUnix);
        return c != 0 ? c : string.CompareOrdinal(a.Uri, b.Uri);
    };

    /// <summary>The one accepted container. Deliberately narrow: the media host's file branch is MP4-only, and a filter
    /// the user cannot get wrong beats an error they have to read.</summary>
    public const string Extension = ".mp4";

    /// <summary>The picker filter tuple (label, spec) — one definition shared by "Attach…", "Replace…" and "Locate…".</summary>
    public static (string Name, string Spec) PickerFilter(string label) => (label, "*" + Extension);

    /// <summary>Extension test (case-insensitive, culture-invariant — a path is not prose).</summary>
    public static bool IsMp4(string? path)
        => path is { Length: > 0 } && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is this a file the AUDIO host can play (.mp3/.ogg/.flac)? Deliberately delegated to the resolver's own
    /// format map rather than restated here, so a surface can never accept a file the resolver would then refuse.</summary>
    public static bool IsAudioFile(string? path) => LocalFileMediaProvider.IsSupportedAudioFile(path);

    /// <summary>The picker filter for the "Play file…" command: everything Wavee can play from disk in one row (an mp4
    /// is playable because a dropped/picked video attaches as its own override and plays with its embedded audio).</summary>
    public static (string Name, string Spec) PlayableFilter(string label) => (label, "*.mp3;*.ogg;*.flac;*" + Extension);

    /// <summary>Validate a picked/dropped path before it becomes an attachment: the right container, and actually there.
    /// <paramref name="fileExists"/> is injected so the rule is testable without a disk.</summary>
    public static VideoAttachRejection Validate(string? path, Func<string, bool> fileExists)
    {
        if (!IsMp4(path)) return VideoAttachRejection.NotMp4;
        bool exists;
        try { exists = fileExists(path!); }
        catch { exists = false; }
        return exists ? VideoAttachRejection.None : VideoAttachRejection.NotFound;
    }

    /// <summary>The FIRST <c>.mp4</c> in a file drop, or null when the drop carries none. A mixed drop is not an error —
    /// the user aimed at a track row with something video-shaped in the set, so take it and ignore the rest.</summary>
    public static string? FirstMp4(IReadOnlyList<string>? paths)
    {
        if (paths is null) return null;
        for (int i = 0; i < paths.Count; i++)
            if (IsMp4(paths[i])) return paths[i];
        return null;
    }

    /// <summary>Map a tier-1 decision onto the roster's status chip. The Broken tier splits here — and ONLY here — into
    /// "Missing" (the volume is mounted, the file moved: repairable) and "Drive offline" (the volume itself is gone: it
    /// heals by itself when the drive returns, so never prompt to remove it).</summary>
    public static VideoOverrideStatus StatusOf(in VideoOverrideDecision d, Func<string, bool> directoryExists)
        => d.Tier switch
        {
            VideoOverrideTier.UseOverride => VideoOverrideStatus.Ok,
            VideoOverrideTier.Quarantined => VideoOverrideStatus.Unplayable,
            VideoOverrideTier.Broken => RootExists(d.Override.Path, directoryExists)
                ? VideoOverrideStatus.Missing
                : VideoOverrideStatus.DriveOffline,
            _ => VideoOverrideStatus.Ok,
        };

    static bool RootExists(string path, Func<string, bool> directoryExists)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(path);
            // A rootless (relative) path has no volume to be offline — treat it as a plain move.
            if (root is not { Length: > 0 }) return true;
            return directoryExists(root);
        }
        catch { return true; }
    }

    /// <summary>The deepest still-existing ancestor directory of a (now missing) path — where a "Locate video file…"
    /// picker should open, so the user restarts from the closest surviving landmark rather than from My Computer
    /// (the Lightroom repair pattern). Null when nothing on the chain exists (an offline volume).</summary>
    public static string? NearestExistingAncestor(string? path, Func<string, bool> directoryExists)
    {
        if (path is not { Length: > 0 }) return null;
        string? dir;
        try { dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)); }
        catch { return null; }
        while (dir is { Length: > 0 })
        {
            bool exists;
            try { exists = directoryExists(dir); }
            catch { exists = false; }
            if (exists) return dir;
            string? parent;
            try { parent = System.IO.Path.GetDirectoryName(dir); }
            catch { return null; }
            if (string.Equals(parent, dir, StringComparison.Ordinal)) return null;   // hit the root and it is gone
            dir = parent;
        }
        return null;
    }

    /// <summary>Which rows the <c>Video ▸</c> submenu shows for one playable. The submenu itself only exists for a SINGLE
    /// selection (attaching one file to N tracks is not a thing the model expresses) and only on a build that has the
    /// curation service — with no service the whole feature is unreachable, which is its kill switch.</summary>
    public static VideoMenuItems MenuFor(bool singleSelection, string? playableUri, VideoOverrideService? svc)
    {
        if (!singleSelection || playableUri is not { Length: > 0 } || svc is null) return VideoMenuItems.None;
        if (!svc.Has(playableUri)) return VideoMenuItems.Attach;

        var items = VideoMenuItems.Replace | VideoMenuItems.Remove;
        // Decide() is the same tier walk playback takes, so the menu can never disagree with what will actually play.
        switch (svc.Decide(playableUri).Tier)
        {
            case VideoOverrideTier.Broken:
                items |= VideoMenuItems.Locate;
                break;
            case VideoOverrideTier.UseOverride:
            case VideoOverrideTier.Quarantined:
                items |= VideoMenuItems.ShowInExplorer;
                break;
        }
        return items;
    }

    /// <summary>Build the Settings roster: newest attachment first (the roster answers "what have I attached?", and the
    /// thing you just attached is the thing you are looking for), each row carrying its resolved status + display copy.
    /// Allocates freely — it runs once per load, never per frame.</summary>
    public static IReadOnlyList<VideoOverrideRow> BuildRoster(
        VideoOverrideService? svc, Func<string, bool> directoryExists, Func<string, Track?>? resolveTrack = null)
    {
        if (svc is null) return Array.Empty<VideoOverrideRow>();
        var all = svc.All();
        var rows = new List<VideoOverrideRow>(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            var o = all[i];
            var status = StatusOf(svc.Decide(o.Uri), directoryExists);
            Track? t = null;
            if (resolveTrack is not null)
            {
                try { t = resolveTrack(o.Uri); }
                catch { t = null; }
            }
            rows.Add(new VideoOverrideRow(o, status, TitleFor(o.Uri, t), SubtitleFor(t), FileNameOf(o.Path)));
        }
        rows.Sort(RecencyOrder);
        return rows;
    }

    // ── the attachment-manager flyout (root: recent + search + browse-all; leaf: the full roster) ────────────────────
    // All of it pure, because the flyout is a presentational shell: it renders what these decide and never re-decides.

    /// <summary>The newest <paramref name="count"/> attachments, newest first — the manager's "Recently added" section.
    /// Re-sorts defensively rather than trusting the caller's order, so a roster built by some other path still yields
    /// a truthful "recent". Never mutates the input.</summary>
    public static IReadOnlyList<VideoOverrideRow> RecentlyAdded(
        IReadOnlyList<VideoOverrideRow>? rows, int count = RecentCount)
    {
        if (rows is null || rows.Count == 0 || count <= 0) return Array.Empty<VideoOverrideRow>();
        var copy = new List<VideoOverrideRow>(rows);
        copy.Sort(RecencyOrder);
        if (copy.Count > count) copy.RemoveRange(count, copy.Count - count);
        return copy;
    }

    /// <summary>Is the user actually searching? Whitespace is not a query — an all-space field must restore the resting
    /// root rather than show "no matches".</summary>
    public static bool IsSearching(string? query)
        => query is { Length: > 0 } && !string.IsNullOrWhiteSpace(query);

    /// <summary>Does this row match the query? Case-insensitive substring over the three things the user could
    /// plausibly remember — the track title, the artist line, and the file name. NOT the full path: a path match would
    /// make every row under one folder a hit, which is noise, and the folder is visible on the row anyway.</summary>
    public static bool Matches(in VideoOverrideRow row, string? query)
    {
        if (!IsSearching(query)) return true;
        string q = query!.Trim();
        return Contains(row.Title, q) || Contains(row.Subtitle, q) || Contains(row.FileName, q);
    }

    static bool Contains(string? haystack, string needle)
        => haystack is { Length: > 0 } && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Filter the roster by the manager's search box, preserving the roster's newest-first order. An empty /
    /// whitespace query returns the input untouched (the caller then shows the resting root, not "all results").</summary>
    public static IReadOnlyList<VideoOverrideRow> Search(IReadOnlyList<VideoOverrideRow>? rows, string? query)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<VideoOverrideRow>();
        if (!IsSearching(query)) return rows;
        var hits = new List<VideoOverrideRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            if (Matches(rows[i], query)) hits.Add(rows[i]);
        return hits;
    }

    /// <summary>Which section the manager's ROOT view renders. <paramref name="matchCount"/> is the size of
    /// <see cref="Search"/>'s result — passed in rather than recomputed so the caller filters exactly once.</summary>
    public static VideoManagerSection RootSection(int total, string? query, int matchCount)
    {
        if (total <= 0) return VideoManagerSection.Empty;
        if (!IsSearching(query)) return VideoManagerSection.Recent;
        return matchCount > 0 ? VideoManagerSection.Results : VideoManagerSection.NoMatches;
    }

    /// <summary>Does the root offer the "Browse all…" drill-in? Whenever anything is attached and no query is live —
    /// even when the recent section already lists everything, because the LEAF is where the full action set lives
    /// (the compact recent rows deliberately carry status only).</summary>
    public static bool ShowsBrowseAll(int total, string? query)
        => total > 0 && !IsSearching(query);

    /// <summary>Row title: the playable's title when the store knows it, else the raw uri (a device-wide roster survives
    /// accounts and catalogs — showing the uri is honest, showing nothing is not).</summary>
    public static string TitleFor(string uri, Track? t)
        => t is { Title.Length: > 0 } track ? track.Title : uri;

    /// <summary>Row subtitle: the artist line, or null when the playable is unknown. Joined locally rather than through
    /// <c>DetailFormat</c> so this whole file stays engine-free (and therefore unit-testable).</summary>
    public static string? SubtitleFor(Track? t)
    {
        if (t is not { Artists.Count: > 0 } track) return null;
        var artists = track.Artists;
        if (artists.Count == 1) return artists[0].Name;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(artists[i].Name);
        }
        return sb.ToString();
    }

    /// <summary>File name for display (the full path rides in the tooltip / secondary line).</summary>
    public static string FileNameOf(string? path)
    {
        if (path is not { Length: > 0 }) return "";
        try { return System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : path; }
        catch { return path; }
    }
}

/// <summary>The ROW-INDICATOR seam: "does this playable have a video?" answered the same way everywhere — the source's
/// own association OR a user attachment. One process-wide attachment point (set at composition, exactly like the rest of
/// the bag-style app services) keeps the call an allocation-free dictionary probe on the row path, where a context read
/// or a signal subscription per row would not be acceptable.
/// <para>Deliberately NOT a signal: row rendering must not subscribe per row, and the roster only changes at human rate
/// — the mutating surfaces bump their own epoch (<c>VideoOverride.ChangeKey</c> / the page's own invalidation) so the
/// affected views re-render.</para></summary>
public static class VideoPresence
{
    static VideoOverrideService? _svc;
    static IStore? _store;

    /// <summary>Attach the curation and the store (composition root). Null on a backend without them — every path then
    /// reports "no video", which is exactly what a backend with no association plane can honestly say.</summary>
    public static void Attach(VideoOverrideService? svc, IStore? store = null) { _svc = svc; _store = store; }

    /// <summary>The attached curation, for surfaces that need more than the predicate.</summary>
    public static VideoOverrideService? Service => _svc;

    /// <summary>Is a user attachment curated for this playable? One ordinal dictionary probe.</summary>
    public static bool HasOverride(string? playableUri) => _svc is { } s && s.Has(playableUri);

    /// <summary>THE has-video answer — every row indicator, the "Videos only" filter, the player-bar button and the
    /// Connect projection ask this and nothing else.
    ///
    /// It reads the ASSOCIATION PLANE (Spotify's kind-99 verdict, keyed by uri) plus any user attachment. The answer
    /// deliberately does not live on the track row: a row is written by half a dozen sources that know nothing about
    /// videos, so a mirrored copy there needed an OR-merge to survive them and still drifted out of step with the
    /// association the detect pass had just stored. One fact, one home, and disagreement becomes unrepresentable.</summary>
    public static bool HasVideo(Track t) => HasVideo(t.Uri);

    /// <summary>The same answer for a bare uri — for callers holding a playable rather than a hydrated row.</summary>
    /// <remarks>Three planes, one answer: Spotify's kind-99 association, the user's own attachment, and — for a
    /// playback-module playable — the module's own <c>form: video</c> verdict from its resolve. The third is a
    /// dictionary probe on the module cache (<see cref="Wavee.Backend.Modules.ModulePlayables"/>), so this stays the
    /// single allocation-free boolean every row indicator and the player-bar button already call.</remarks>
    public static bool HasVideo(string? playableUri)
        => (playableUri is { Length: > 0 } u && _store?.GetVideoAssociation(u) is { HasVideo: true })
           || HasOverride(playableUri)
           || Wavee.Backend.Modules.ModulePlayables.HasVideo(playableUri);

    /// <summary>DIAGNOSTIC ONLY — the raw association record behind <see cref="HasVideo(string?)"/>, so an off-render-path
    /// sweep can tell "no row at all" (never asked / nothing came back) apart from "a row that says no" (a cached negative
    /// verdict). Never call this from a row or a frame: <see cref="HasVideo(Track)"/> is the render-path answer, and it
    /// stays a single boolean probe precisely so it never has to hand a record out.</summary>
    public static VideoAssociation? Association(string? playableUri)
        => playableUri is { Length: > 0 } u ? _store?.GetVideoAssociation(u) : null;
}
