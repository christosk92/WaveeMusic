// ── Shell/Shell.Drop.Plan.cs ──────────────────────────────────────────────────────────────────────────────────────────
// DROP ANYWHERE, the plan: what an OS drag carries, read ONCE per drag (plan 240 §3.15, D15, D16; UX S12)
//
// Role: CORE (pure classification + the one cache; the folder count runs off the UI thread)
// Spec: handoff/request-plans/240-local-playlist-files-implementation.md §3.15, §3.17 (the reserved Watch kind);
//       240-local-files-ux.md S12; board frames 22-24.
//
// A drag's payloads (Files, Uri, Text) become one `DropPlan` the first time any target asks (`OnEnter`, `CanAccept`): the
// payload kind (songs, folders, a playlist file, Spotify links, nothing usable with a reason), the zones the drop panel offers
// for it, the chip's words and the captions. Every later ask in the same drag is a reference compare (0-alloc, so `OnOver`
// and `CanAccept` stay inside the engine's zero-alloc frame region). The engine hands the file list on enter
// (fluent-gpu feat/drop-paths-on-enter); an engine that hands it only at drop gives an EMPTY list on enter, which plans as
// "files, kind not known yet": the song zones, refined at drop when the real list arrives (a new payload object, so a new plan).

using System.Globalization;

using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>What a drag carries, as the drop panel sorts it. <see cref="Watch"/> is reserved for local videos (plan §3.17): 0.4
/// never plans it (a dropped <c>.mp4</c> plays through the existing video path as a song does).</summary>
public enum DropPayloadKind : byte { None, Songs, Folders, PlaylistFile, Links, Watch, Refused }

/// <summary>One drop-panel zone's verb. <see cref="AddToLibrary"/> keeps dropped songs in Local files ("Added files") and nothing plays or
/// queues.</summary>
public enum DropVerb : byte { PlayNow, PlayNext, AddToQueue, Play, AddMusicFolder, PlayAsQueue, Open, AddToLibrary }

/// <summary>One drag's plan (built once, on the first ask; UI thread). The arrays are the drop's own order.</summary>
public sealed class DropPlan
{
    /// <summary>The playable files dragged directly (audio, and an <c>.mp4</c>), in drop order.</summary>
    public string[] Files = [];
    /// <summary>The folders dragged (walked at drop, file-name order).</summary>
    public string[] Folders = [];
    /// <summary>The first playlist file dragged (<c>.m3u</c>, <c>.m3u8</c>, <c>.pls</c>), or null.</summary>
    public string? PlaylistFile;
    /// <summary>The Spotify entities the links and text name (<c>spotify:</c> uris), in drop order.</summary>
    public string[] Entities = [];
    /// <summary>Every entity is a track: the links plan as songs (Play now · Play next · Add to queue).</summary>
    public bool AllTracks;
    /// <summary>The file list was known when planned (false: an engine that reads it only at drop; the plan is refined then).</summary>
    public bool PathsKnown = true;

    public DropPayloadKind Kind;
    /// <summary>The zones the panel offers, in order (two to four; one for a playlist file).</summary>
    public DropVerb[] Verbs = [];
    /// <summary>Why nothing here can be used (<see cref="DropPayloadKind.Refused"/>): the chip's sentence.</summary>
    public string? Refusal;

    /// <summary>The songs the drop holds: the files plus what the folders hold, or the playlist's found entries. -1 while the
    /// folder listing or the playlist read is still running (off the UI thread).</summary>
    public readonly Signal<int> Songs = new(-1);
    /// <summary>The playlist file's entry count (0 until read).</summary>
    public int PlaylistTotal;
    /// <summary>The folders hold more than the drop's cap: the walk stops there.</summary>
    public bool Capped;

    /// <summary>The chip's line ("12 songs · 2 folders"), recomposed when the count lands; read per frame, never built there.</summary>
    public string Chip = "";
    /// <summary>The queue's caption at its drop line ("3 songs land here").</summary>
    public string QueueCaption = "";

    public bool Usable => Kind is not (DropPayloadKind.None or DropPayloadKind.Refused);
    /// <summary>The player bar plays files and links, never folders (board 23) or a playlist file.</summary>
    public bool PlayerBarTakes => Kind == DropPayloadKind.Songs || Kind == DropPayloadKind.Links && Entities.Length > 0;
    /// <summary>The queue takes files, folders, a playlist file's songs, and track links.</summary>
    public bool QueueTakes => Kind is DropPayloadKind.Songs or DropPayloadKind.Folders or DropPayloadKind.PlaylistFile
                              || Kind == DropPayloadKind.Links && AllTracks;
    /// <summary>Local files (not links) are what a Spotify playlist cannot take yet (D12).</summary>
    public bool IsLocal => Kind is DropPayloadKind.Songs or DropPayloadKind.Folders or DropPayloadKind.PlaylistFile;
}

/// <summary>Drop anywhere (plan §3.15): the plan of the live OS drag, its targets and what a drop does.</summary>
public static partial class DropAnywhere
{
    /// <summary>The most files one drop expands to (plan §3.15).</summary>
    public const int Cap = 5000;

    static object? s_planKey;
    static DropPlan s_plan = new();

    /// <summary>The live drag's plan, or null when no OS drag is in flight.</summary>
    public static DropPlan? Current => s_planKey is null ? null : s_plan;

    /// <summary>The plan of this session's OS payloads, built once per drag (a reference compare after that: 0-alloc).</summary>
    public static DropPlan PlanOf(DragSession s)
    {
        object? key = s.PayloadOf(DropKinds.Files) ?? s.PayloadOf(DropKinds.Uri) ?? s.PayloadOf(DropKinds.Text);
        if (key is not null && ReferenceEquals(key, s_planKey)) return s_plan;
        s_planKey = key;
        s_plan = key is null ? new DropPlan() : Plan(s.PayloadOf(DropKinds.Files) as FileDropData,
            s.PayloadOf(DropKinds.Uri) as UriDropData, s.PayloadOf(DropKinds.Text) as TextDropData);
        return s_plan;
    }

    /// <summary>Is this payload one of the OS kinds (what the queue's foreign gate sees)?</summary>
    public static bool IsOsPayload(object? payload) => payload is FileDropData or UriDropData or TextDropData;

    /// <summary>The plan of a payload the queue was handed (its gate sees the payload, not the session): the live plan when it
    /// is that drag's, else a fresh one.</summary>
    public static DropPlan PlanOfPayload(object? payload)
    {
        if (payload is not null && ReferenceEquals(payload, s_planKey)) return s_plan;
        return payload switch
        {
            FileDropData f => Plan(f, null, null),
            UriDropData u => Plan(null, u, null),
            TextDropData t => Plan(null, null, t),
            _ => new DropPlan(),
        };
    }

    /// <summary>The drag ended: forget its plan (a late folder count lands on the orphan plan and changes nothing on screen).</summary>
    public static void Forget()
    {
        s_planKey = null;
        s_plan = new DropPlan();
    }

    // ── classification (pure apart from Directory.Exists and the music-folder list) ────────────────────────────────────

    /// <summary>Sort a drag's payloads into a plan. Files win over links (a file drag can carry its path as text too).</summary>
    public static DropPlan Plan(FileDropData? files, UriDropData? uris, TextDropData? text)
    {
        var plan = new DropPlan();
        if (files is not null) PlanFiles(plan, files.Paths);
        else PlanLinks(plan, uris, text);
        Compose(plan);
        return plan;
    }

    /// <summary>Dropped song files: Play now · Play next · Add to queue · Add to library (kept in Local files, nothing plays). Shared: a
    /// plan never writes its verbs.</summary>
    static readonly DropVerb[] s_songVerbs = [DropVerb.PlayNow, DropVerb.PlayNext, DropVerb.AddToQueue, DropVerb.AddToLibrary];

    static void PlanFiles(DropPlan plan, string[] paths)
    {
        if (paths.Length == 0)
        {
            // The engine has not handed the list yet: the song zones, refined at drop.
            plan.PathsKnown = false;
            plan.Kind = DropPayloadKind.Songs;
            plan.Verbs = s_songVerbs;
            plan.Songs.Value = 0;
            return;
        }
        var playable = new List<string>(paths.Length);
        var folders = new List<string>();
        string? playlist = null, unplayable = null;
        foreach (string path in paths)
        {
            if (Directory.Exists(path)) folders.Add(path);
            else if (Playlist.IsLocalAudioFile(path) || Playlist.IsLocalVideoFile(path)) playable.Add(path);
            else if (IsPlaylistFile(path)) playlist ??= path;
            else unplayable ??= path;
        }
        plan.Files = [.. playable];
        plan.Folders = [.. folders];
        plan.PlaylistFile = playlist;
        if (folders.Count > 0)
        {
            plan.Kind = DropPayloadKind.Folders;
            // "Add as a music folder" only for a pure folder drop that Music on this PC can take (not one already watched).
            bool addable = playable.Count == 0 && playlist is null && folders.TrueForAll(CanAddMusicFolder);
            plan.Verbs = addable ? [DropVerb.Play, DropVerb.AddMusicFolder, DropVerb.AddToQueue] : [DropVerb.Play, DropVerb.AddToQueue];
        }
        else if (playable.Count > 0)
        {
            plan.Kind = DropPayloadKind.Songs;
            plan.Verbs = s_songVerbs;
            plan.Songs.Value = playable.Count;
        }
        else if (playlist is not null)
        {
            plan.Kind = DropPayloadKind.PlaylistFile;
            plan.Verbs = [DropVerb.PlayAsQueue];
        }
        else
        {
            plan.Kind = DropPayloadKind.Refused;
            string ext = unplayable is null ? "" : Path.GetExtension(unplayable);
            plan.Refusal = ext.Length > 1 ? Strings.Drop.CantPlayType(ext.ToLowerInvariant()) : Loc.Get(Strings.Drop.NothingUsable);
        }
    }

    static void PlanLinks(DropPlan plan, UriDropData? uris, TextDropData? text)
    {
        var found = new List<string>();
        if (uris is not null)
            foreach (string uri in uris.Uris) AddEntity(found, uri);
        if (found.Count == 0 && text is not null)
            foreach (string word in text.Text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) AddEntity(found, word);
        if (found.Count == 0)
        {
            plan.Kind = DropPayloadKind.Refused;
            plan.Refusal = Loc.Get(Strings.Drop.OnlySpotifyLinks);
            return;
        }
        plan.Kind = DropPayloadKind.Links;
        plan.Entities = [.. found];
        plan.AllTracks = found.TrueForAll(static e => e.StartsWith("spotify:track:", StringComparison.Ordinal));
        plan.Verbs = plan.AllTracks ? [DropVerb.PlayNow, DropVerb.PlayNext, DropVerb.AddToQueue] : [DropVerb.Open, DropVerb.Play];
        plan.Songs.Value = plan.AllTracks ? found.Count : 0;
    }

    /// <summary>A <c>spotify:</c> uri or an <c>open.spotify.com</c> link, through the deep-link parser (any locale segment, any
    /// <c>?si=</c>); anything else is not usable.</summary>
    static void AddEntity(List<string> into, string link)
    {
        if (Shell.TryEntityOfLink(link.AsSpan().Trim(), out string uri) && !into.Contains(uri)) into.Add(uri);
    }

    /// <summary>A playlist file the drop reads as a queue.</summary>
    public static bool IsPlaylistFile(string path)
        => path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase);

    /// <summary>Would Music on this PC take this folder? Not when it is one already, sits inside one, or holds one.</summary>
    static bool CanAddMusicFolder(string folder)
    {
        string f;
        try { f = LocalLibrary.Normalize(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        foreach (string have in LocalLibrary.Folders)
            if (Within(have, f) || Within(f, have)) return false;
        return true;
    }

    static bool Within(string outer, string inner)
        => inner.Length >= outer.Length && inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase)
           && (inner.Length == outer.Length || outer.EndsWith('\\') || inner[outer.Length] == '\\');

    // ── the words (cold: on plan, and when a count lands) ──────────────────────────────────────────────────────────────

    static void Compose(DropPlan plan)
    {
        switch (plan.Kind)
        {
            case DropPayloadKind.Folders:
            case DropPayloadKind.PlaylistFile:
                Recount(plan);
                StartCount(plan);
                break;
            default:
                Recount(plan);
                break;
        }
    }

    /// <summary>The chip and the queue caption from the current count (UI thread).</summary>
    static void Recount(DropPlan plan)
    {
        int songs = plan.Songs.Peek();
        string songWords = songs < 0 ? Loc.Get(Strings.Drop.Counting) : SongWords(plan, songs);
        plan.Chip = plan.Kind switch
        {
            DropPayloadKind.Folders => Strings.Drop.ChipFolders(Strings.Drop.FolderCount(plan.Folders.Length), songWords),
            DropPayloadKind.Links when !plan.AllTracks => Strings.Drop.LinkCount(plan.Entities.Length),
            DropPayloadKind.Songs when !plan.PathsKnown => Loc.Get(Strings.Drop.Files),
            DropPayloadKind.Refused or DropPayloadKind.None => "",
            _ => songWords,
        };
        plan.QueueCaption = plan.Kind == DropPayloadKind.Songs && !plan.PathsKnown
            ? Loc.Get(Strings.Drag.AddToQueue)
            : Strings.Drop.LandHere(songWords);
    }

    /// <summary>"12 songs", "5,000+ songs" past the cap, "38 of 40 songs found" for a playlist file with gaps.</summary>
    public static string SongWords(DropPlan plan, int songs)
    {
        if (plan.Kind == DropPayloadKind.PlaylistFile && plan.PlaylistTotal > songs)
            return Strings.Drop.FoundOf(songs.ToString("N0", CultureInfo.CurrentCulture), Strings.Detail.SongCount(plan.PlaylistTotal));
        return plan.Capped ? Strings.Drop.SongsCapped(songs) : Strings.Detail.SongCount(songs);
    }

    /// <summary>One listing off the UI thread: the folders' playable files (capped), or the playlist's found entries; the count
    /// lands on the UI thread and recomposes the words.</summary>
    static void StartCount(DropPlan plan)
    {
        string[] files = plan.Files, folders = plan.Folders;
        string? playlist = plan.PlaylistFile;
        _ = Task.Run(() =>
        {
            int count = files.Length, total = 0;
            bool capped = false;
            try
            {
                if (playlist is not null && folders.Length == 0)
                {
                    var entries = ReadPlaylist(playlist);
                    total = entries.Count;
                    count = 0;
                    // "found" = what the drop will queue: an existing file of a kind Wavee plays (a .wma entry is not one)
                    foreach (string e in entries)
                        if ((Playlist.IsLocalAudioFile(e) || Playlist.IsLocalVideoFile(e)) && File.Exists(e)) count++;
                }
                else
                {
                    foreach (string folder in folders)
                    {
                        count += CountFolder(folder, Cap - count);
                        if (count >= Cap) { capped = true; count = Cap; break; }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("drop", "count failed", ex); }
            Playback.ToUi(() =>
            {
                plan.PlaylistTotal = total;
                plan.Capped = capped;
                plan.Songs.Value = count;
                Recount(plan);
            });
        });
    }
}
