// ── Shell/Shell.Drop.Run.cs ───────────────────────────────────────────────────────────────────────────────────────────
// DROP ANYWHERE, the run: what a drop does once a zone or a target took it (plan 240 §3.15; UX S12 "After the drop")
//
// Role: CORE (the walk and the tag reads run off the UI thread; the rows are staged and deposited on it, in batches)
// Spec: handoff/request-plans/240-local-playlist-files-implementation.md §3.15; 240-local-files-ux.md S12, F8, F9.
//
// Large drops never block: the folders are walked with `NtDirectory` (file-name order, files before subfolders, never through a
// reparse point) up to `Cap` files, each file's tags are read on the worker, and the rows land on the UI thread in announced
// batches. "Play" plays the first batch as soon as it is read (the first song starts while the walk goes on) and queues the
// rest after it; "Play next" and "Add to queue" fill the queue batch by batch; a queue drop inserts at its line. A big drop
// shows a progress toast that turns into a summary with Undo (queue changes come back out in one step).

using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Microsoft.Win32.SafeHandles;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>Where a drop's songs go (<see cref="Playlist"/>: added to one of the user's playlists, #240 D12).</summary>
public enum DropDeposit : byte { PlayNow, PlayNext, Append, At, Playlist }

public static partial class DropAnywhere
{
    /// <summary>Rows per UI-thread batch (a staged row is a table write; 25 keep a frame short).</summary>
    const int BatchRows = 25;
    /// <summary>A drop bigger than this, or one still walking after <see cref="ToastAfterMs"/>, says so with a progress toast.</summary>
    const int ToastFrom = 50;
    const int ToastAfterMs = 400;
    const string ToastKey = "drop.add";

    // ── the verbs ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Run one zone's verb on the plan (UI thread). The plan may be the drop-time refinement of the one the panel
    /// showed; the verbs mean the same on both.</summary>
    public static void Run(DropPlan plan, DropVerb verb)
    {
        Log.Info("drop", $"run verb={verb} kind={plan.Kind} files={plan.Files.Length} folders={plan.Folders.Length} links={plan.Entities.Length}");
        switch (verb)
        {
            case DropVerb.AddMusicFolder: AddMusicFolders(plan); return;
            case DropVerb.Open: OpenLinks(plan); return;
            case DropVerb.PlayNow or DropVerb.Play or DropVerb.PlayAsQueue: Deposit(plan, DropDeposit.PlayNow); return;
            case DropVerb.PlayNext: Deposit(plan, DropDeposit.PlayNext); return;
            case DropVerb.AddToQueue: Deposit(plan, DropDeposit.Append); return;
        }
    }

    /// <summary>Put the plan's songs where <paramref name="how"/> says (UI thread); <paramref name="at"/> is the queue-relative
    /// index of a queue drop. THE one path every target that takes local files runs (the panel's zones, the window's catch-all,
    /// Local Files' sidebar row and page, the queue's line, the player bar): each dropped file becomes a library entry ("Added
    /// files", <see cref="LocalLibrary.AddFile"/>) whatever playback then does with it. When playback cannot take the songs (local
    /// playback not wired yet, or another device plays and the drop was for the queue) they are still kept, and the toast says why
    /// they did not play.</summary>
    public static void Deposit(DropPlan plan, DropDeposit how, int at = 0)
    {
        if (plan.Kind == DropPayloadKind.Links) { DepositLinks(plan, how); return; }
        if (!plan.IsLocal) return;
        string? unplaced = Playback.Audio.LocalPath is null ? Loc.Get(Strings.LocalFile.NotReady)
            // Another device plays: a file on this PC cannot join its queue.
            : how != DropDeposit.PlayNow && Shell.QueueModel.IsViewer() ? Loc.Get(Strings.Drop.QueueOnThisPc)
            : null;
        new Expansion(plan, how, at, unplaced).Start();
    }

    /// <summary>Local files dropped on one of the user's playlists (its sidebar row, #240 D12): each file becomes its library entry as any
    /// drop's does (one the index already holds is found; a new one is added to "Added files"), then every row goes into the playlist as its own
    /// <c>spotify:local:</c> item through the same deposit as the row menu's "Add to playlist" (its toast says "Added to {name}"). A file whose
    /// length is only an estimate is added too (other devices may not find it: logged). UI thread.</summary>
    public static void DepositToPlaylist(DropPlan plan, string playlistUri, string playlistName)
    {
        if (!plan.IsLocal) return;
        new Expansion(plan, DropDeposit.Playlist, 0, null) { PlaylistUri = playlistUri, PlaylistName = playlistName }.Start();
    }

    /// <summary>The last local drop deposited into a playlist (the inspector reports what the deposit was handed).</summary>
    public static (string PlaylistUri, Track[] Tracks)? LastPlaylistDeposit { get; set; }

    /// <summary>A drop's files are still being read and landed (UI thread; the inspector waits for it).</summary>
    public static bool Expanding => s_expanding > 0;

    static int s_expanding;

    // ── links (D16) ───────────────────────────────────────────────────────────────────────────────────────────────────

    static void OpenLinks(DropPlan plan)
    {
        if (plan.Entities.Length > 0) Shell.OpenLink(plan.Entities[0]);
    }

    static void DepositLinks(DropPlan plan, DropDeposit how)
    {
        var services = Actions.Services;
        string[] uris = plan.Entities;
        if (uris.Length == 0) return;
        if (!plan.AllTracks)
        {
            // An album, a playlist, an artist, a show: play the first as a context.
            Shell.OnPlayContext?.Invoke(EntityUri.Parse(uris[0]));
            return;
        }
        switch (how)
        {
            case DropDeposit.PlayNow:
                services.Play?.Invoke(EntityUri.Parse(uris[0]));
                for (int i = 1; i < uris.Length; i++) services.AddToQueue?.Invoke(EntityUri.Parse(uris[i]));
                break;
            case DropDeposit.PlayNext:
                for (int i = uris.Length - 1; i >= 0; i--) services.PlayNext?.Invoke(EntityUri.Parse(uris[i]));
                break;
            default:
                for (int i = 0; i < uris.Length; i++) services.AddToQueue?.Invoke(EntityUri.Parse(uris[i]));
                break;
        }
        if (how != DropDeposit.PlayNow)
            Notify.Say(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(uris.Length)), InfoBarSeverity.Success);
    }

    // ── Add as a music folder (F8) ────────────────────────────────────────────────────────────────────────────────────

    static void AddMusicFolders(DropPlan plan)
    {
        int added = 0;
        string addedName = "";
        foreach (string folder in plan.Folders)
        {
            // The folder's own name: a share root (\\nas\music) has no file name, and the toast read " is now a music folder".
            string name = LocalCollection.LeafOf(folder);
            if (LocalLibrary.AddFolder(folder, out string reason)) { added++; addedName = name; continue; }
            string why = reason switch
            {
                "duplicate" => Strings.Drop.AlreadyMusicFolder(name),
                "inside" => Strings.Drop.InsideMusicFolder(name),
                "contains" => Strings.Drop.HoldsMusicFolder(name),
                _ => Strings.Drop.NotAFolder(name),
            };
            Notify.Say(why, InfoBarSeverity.Warning);
        }
        if (added == 0) return;
        Notify.Say(added == 1 ? Strings.Drop.MusicFolderAdded(addedName)
                              : Strings.Drop.MusicFoldersAdded(added), InfoBarSeverity.Success);
        // The folder's first scan shows on Local Files (board 23 b).
        Shell.GoTo(Shell.Parse("local"));
    }

    // ── the expansion ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One drop's walk → read → stage → deposit, with its toast and its Undo. <paramref name="unplaced"/>: why playback cannot
    /// take the songs (they are only kept in Local Files), or null.</summary>
    sealed class Expansion(DropPlan plan, DropDeposit how, int at, string? unplaced)
    {
        readonly FloatSignal _progress = new(0f);
        readonly List<ulong> _queued = [];
        readonly List<Track> _forPlaylist = [];
        public string PlaylistUri { get; init; } = "";
        public string PlaylistName { get; init; } = "";
        int _estimated;
        HashSet<ulong>? _before;
        ToastHandle? _toast;
        int _landed, _skipped, _inserted;
        bool _played, _capped;
        long _started;

        public void Start()
        {
            _started = Environment.TickCount64;
            s_expanding++;
            int expected = plan.Songs.Peek();
            if (expected >= ToastFrom) ShowProgress(expected);
            _ = Task.Run(Walk);
        }

        /// <summary>Worker: the files in drop order, then each folder's walk, then the playlist's entries; tags read here.</summary>
        void Walk()
        {
            var batch = new List<(string Path, Spotify.Audio.Format Format, LocalTagSet Tags, bool Read, string Why)>(BatchRows);
            int seen = 0;
            bool Take(string path)
            {
                if (seen >= Cap) { _capped = true; return false; }
                if (!Playlist.IsLocalAudioFile(path) && !Playlist.IsLocalVideoFile(path)) { Interlocked.Increment(ref _skipped); return true; }
                // The reader is the judge, not the extension: a damaged or foreign file named .mp3 (or ALAC in an .m4a)
                // reads as no format Wavee can play, and is counted as skipped instead of queued as a row that cannot play.
                bool read = LocalTags.TryRead(path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why);
                bool known = read && format != Spotify.Audio.Format.Unknown;
                if (!known && !Playlist.IsLocalVideoFile(path)) { Interlocked.Increment(ref _skipped); return true; }
                seen++;
                batch.Add((path, format, tags, read, why));
                if (batch.Count >= BatchRows || !_played && how == DropDeposit.PlayNow)
                {
                    var rows = batch.ToArray();
                    batch.Clear();
                    Playback.ToUi(() => Land(rows));
                }
                return true;
            }
            try
            {
                bool go = true;
                foreach (string f in plan.Files) if (go) go = Take(f);
                foreach (string folder in plan.Folders) if (go) go = WalkFolder(folder, Take);
                if (go && plan.PlaylistFile is { } list)
                    foreach (string entry in ReadPlaylist(list))
                    {
                        if (!go) break;
                        if (File.Exists(entry)) go = Take(entry);
                        else Interlocked.Increment(ref _skipped);
                    }
            }
            catch (Exception ex) { Log.Warn("drop", "walk failed", ex); }
            var last = batch.ToArray();
            Playback.ToUi(() => { if (last.Length > 0) Land(last); Finish(); });
        }

        /// <summary>UI thread: stage a batch's rows and put them where the drop said.</summary>
        void Land((string Path, Spotify.Audio.Format Format, LocalTagSet Tags, bool Read, string Why)[] rows)
        {
            var refs = new EntityRef[rows.Length];
            int n = 0;
            foreach (var (path, format, tags, read, why) in rows)
            {
                // A dropped file is a library entry ("Added files", LocalLibrary.Added.cs): indexed, kept, listed like a music-folder file.
                // A video file is its own row's video (TrackFlags.LocalVideoFile, from its tags): it plays on the video host, nothing attached.
                var track = LocalLibrary.AddFile(path, format, in tags, read, why);
                if (!track.IsValid) { _skipped++; continue; }
                refs[n++] = new EntityRef(EntityKind.Track, track.Slot);
                if (how == DropDeposit.Playlist)
                {
                    _forPlaylist.Add(track);
                    // Added anyway: the playlist item names the file by its whole seconds, and another device reading the length exactly may not
                    // find it. The file name only (paths hold the account name).
                    if (tags.DurationEstimated) { _estimated++; Log.Info("drop", "drop.playlist estimated-length file=" + Path.GetFileName(path)); }
                }
            }
            if (n == 0) return;
            _landed += n;
            if (unplaced is null) Place(refs.AsSpan(0, n));   // else kept only: nothing plays or queues (Finish says why)
            if (_toast is null && (_landed >= ToastFrom || Environment.TickCount64 - _started > ToastAfterMs)) ShowProgress(plan.Songs.Peek());
            int expected = plan.Songs.Peek();
            if (expected > 0) _progress.Value = Math.Clamp(_landed / (float)expected, 0f, 1f);
        }

        /// <summary>UI thread: put a landed batch where the drop said (the first batch of a Play plays at once).</summary>
        void Place(ReadOnlySpan<EntityRef> span)
        {
            if (how == DropDeposit.Playlist) return;   // the rows go into the playlist in one deposit at the end
            if (how == DropDeposit.PlayNow && !_played)
            {
                // The first batch plays at once (the first song is the first row); the walk goes on behind it.
                _played = true;
                Playback.PlayRows(span, 0, Playlist.LocalFiles.Id);
                return;
            }
            _before ??= ItemIds();
            int index = how switch
            {
                DropDeposit.PlayNext => _inserted,
                DropDeposit.At => at + _inserted,
                _ => int.MaxValue,
            };
            _inserted += Queue.InsertUser(span, Playback.Snap().Cursor, index);
            Shell.QueueVerbs.Changed();
        }

        void ShowProgress(int expected)
        {
            string title = expected > 0 ? Strings.Drop.Adding(Strings.Detail.SongCount(expected)) : Loc.Get(Strings.Drop.AddingSongs);
            _toast = Notify.Say(title, InfoBarSeverity.Informational, dedupeKey: ToastKey, durationMs: 0f, title: title,
                customContent: () => ViewSlots.View(ViewKeys.OverlayToast, new ToastCardProps { Title = title, Progress = _progress }));
        }

        /// <summary>UI thread: the summary, with Undo for a queue change.</summary>
        void Finish()
        {
            s_expanding--;
            _toast?.Close();

            Log.Info("drop", $"done how={how} landed={_landed} skipped={_skipped} capped={_capped}");
            if (_landed == 0) { Notify.Say(Loc.Get(Strings.Drop.NoneFound), InfoBarSeverity.Warning); return; }
            if (how == DropDeposit.Playlist)
            {
                // The row menu's deposit: one ADD of every row as its spotify:local: item, then "Added to {name}" (Spotify.Library).
                Track[] tracks = [.. _forPlaylist];
                LastPlaylistDeposit = (PlaylistUri, tracks);
                Log.Info("drop", $"drop.playlist add rows={tracks.Length} estimated={_estimated} skipped={_skipped}");
                if (Sidebar.LibraryWrites?.DepositTracks is { } deposit) deposit(PlaylistUri, PlaylistName, PayloadOf(tracks));
                else NavModel.RefuseDrop(SidebarDropRefusal.WritesUnavailable, "no deposit seam");
                return;
            }
            if (_before is not null)
                foreach (QueueEdge row in Queue.Rows)
                    if (row.ItemId != 0 && !_before.Contains(row.ItemId)) _queued.Add(row.ItemId);
            string songs = _capped ? Strings.Drop.SongsCapped(_landed) : Strings.Detail.SongCount(_landed);
            if (unplaced is not null)
            {
                Notify.Say(unplaced, InfoBarSeverity.Warning, title: Strings.Drop.Kept(songs));
                return;
            }
            if (how == DropDeposit.PlayNow)
            {
                // Small plays say nothing (the player bar is the answer); a big one closes its progress toast with what played.
                if (_toast is not null || _skipped > 0)
                    Notify.Say(_skipped > 0 ? Strings.Drop.PlayingSkipped(songs, _skipped) : Strings.Drop.Playing(songs), InfoBarSeverity.Success);
                return;
            }
            string message = _skipped > 0 ? Strings.Drop.AddedSkipped(songs, _skipped) : Strings.Drop.Added(songs);
            ulong[] undo = [.. _queued];
            Notify.Say(message, InfoBarSeverity.Success, actionLabel: undo.Length > 0 ? Loc.Get(Strings.Drop.Undo) : null,
                onAction: undo.Length > 0 ? () => Undo(undo) : null);
        }

        static DragPayload PayloadOf(Track[] tracks)
        {
            string uri = tracks[0].Uri.Text;
            return new DragPayload(DragKind.Track, uri, uri, tracks[0].Title, new EntityRef(EntityKind.Track, tracks[0].Slot), Tracks: tracks);
        }

        static HashSet<ulong> ItemIds()
        {
            var ids = new HashSet<ulong>();
            foreach (QueueEdge row in Queue.Rows) if (row.ItemId != 0) ids.Add(row.ItemId);
            return ids;
        }

        /// <summary>The whole drop back out of the queue, in one step (rows already played stay where they are).</summary>
        static void Undo(ulong[] ids)
        {
            var cursor = Playback.Snap().Cursor;
            int removed = 0;
            foreach (ulong id in ids)
            {
                int index = Queue.IndexOfItem(id);
                if (index >= 0 && Queue.RemoveUpcoming(index, cursor)) removed++;
            }
            if (removed > 0) Shell.QueueVerbs.Changed();
        }
    }

    // ── the folder walk (NtDirectory, §3.6's listing rules) ─────────────────────────────────────────────────────────

    /// <summary>Visit the files under <paramref name="root"/>: each folder's files in file-name order, then its subfolders in
    /// file-name order; never through a reparse point, the recycle bin or a hidden or system folder. <paramref name="take"/>
    /// returns false to stop. False when the walk was stopped.</summary>
    static bool WalkFolder(string root, Func<string, bool> take)
    {
        using SafeFileHandle? handle = NtDirectory.OpenDirectory(null, root, out int status);
        if (handle is null) { Log.Warn("drop", $"open folder failed status=0x{status:X8}"); return true; }
        uint serial = NtDirectory.VolumeOf(handle, root).Serial;
        return WalkInto(handle, root, serial, take);
    }

    static bool WalkInto(SafeFileHandle dir, string path, uint serial, Func<string, bool> take)
    {
        var entries = new List<NtEntry>();
        if (!NtDirectory.List(dir, serial, entries, out _)) return true;
        entries.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (NtEntry e in entries)
            if (!e.IsDirectory && LocalTags.IsScanExtension(e.Name) && !take(Path.Combine(path, e.Name))) return false;
        foreach (NtEntry e in entries)
        {
            if (!NtDirectory.WalksInto(in e)) continue;
            using SafeFileHandle? child = NtDirectory.OpenDirectory(dir, e.Name, out _);
            if (child is not null && !WalkInto(child, Path.Combine(path, e.Name), serial, take)) return false;
        }
        return true;
    }

    /// <summary>How many playable files a folder holds, up to <paramref name="limit"/> (the panel's count; worker thread).</summary>
    static int CountFolder(string root, int limit)
    {
        int n = 0;
        WalkFolder(root, path =>
        {
            if (Playlist.IsLocalAudioFile(path) || Playlist.IsLocalVideoFile(path)) n++;
            return n < limit;
        });
        return n;
    }

    // ── playlist files ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The entries of an <c>.m3u</c>/<c>.m3u8</c> (one path per line, <c>#</c> lines are directives) or a <c>.pls</c>
    /// (<c>FileN=path</c>), each resolved against the playlist file's folder; urls are left out. Worker thread.</summary>
    public static List<string> ReadPlaylist(string file)
    {
        var entries = new List<string>();
        string folder = Path.GetDirectoryName(file) ?? "";
        bool pls = file.EndsWith(".pls", StringComparison.OrdinalIgnoreCase);
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            if (pls)
            {
                int eq = line.IndexOf('=');
                if (eq < 5 || !line.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;
                line = line[(eq + 1)..].Trim();
            }
            else if (line[0] == '#') continue;
            if (line.Contains("://", StringComparison.Ordinal) && !line.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out Uri? uri)) line = uri.LocalPath;
            try { entries.Add(Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(folder, line))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (entries.Count >= Cap) break;
        }
        return entries;
    }
}
