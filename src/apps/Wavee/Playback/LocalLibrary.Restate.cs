// ── Playback/LocalLibrary.Restate.cs ────────────────────────────────────────────────────────────────────────────────
// the index -> the Local Files list: every indexed audio file as a stable row (D13), the membership in album order, the
// path map the audio pump reads, and what changed this session
//
// Role: SHELL (UI thread for the staging; one background thread builds the faces; any thread for PathOf)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.7 step 1 and step 3, §3.10, D13;
//       240-local-files-ux.md S4-S6
//
// IDENTITY (D13). A file in a music folder is `wavee:local:file:~<LocalId>`: the id the index keeps across moves and renames,
// so a moved file keeps its row, its place in the queue and anything attached to it. `~` is outside the base64url alphabet, so
// it never collides with a dropped file's path form (`wavee:local:file:<base64url(path)>`, unchanged); both are Track rows.
//
// THE PASS. When the index's Version moves (at most once a second while a scan commits, at once otherwise): a background thread
// takes the snapshot and builds one face per audio entry (title, artists, album, length, unavailable, a hash of all of it);
// the UI thread then stages only the faces whose hash changed, 2,000 rows per frame (a 50,000-file first pass is 25 commits
// over 25 frames, never one long frame), replaces the Local Files membership (indexed files by album artist, album, disc,
// track, title; then any session-dropped file), and rebuilds the path map. Missing files stay listed and unavailable (S6);
// files of an offline folder stay listed and unavailable (S7); video entries are listed like songs (the owner overrode D19), and so are
// the "Added files" (LocalLibrary.Added.cs: a dropped file is an indexed entry, staged at once by the drop).
//
// SCOPE. The rows and the membership live in the current catalog scope. The login's welcome switches it (`Entities.Switch`: a new, empty
// table set), usually a moment after the start-up scan's first pass staged the rows, so `Rebind` (called beside `Playback.Rebind`) stages every
// face again into the new set and replaces its membership; the files did not change, so nothing is announced.
//
// WHAT CHANGED (S5). After the first pass (the baseline) every later pass counts added, moved, missing, back and changed
// files for the session; new ids keep a "New" mark and moved ids remember where they were, for the session.
//
// PATH MAP. `PathOf(EntityId)` is what `Playback.Audio.LocalPath` calls on the pump: one probe of an EntityId -> LocalId map
// under a lock (filled here on the UI thread from the committed rows, so the pump never reads id text), then the index's
// full path; a dropped file falls back to its path form.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>The id prefix of an indexed file (D13).</summary>
    public const string IdPrefix = Playlist.LocalFilePrefix + "~";

    /// <summary>The uri of an indexed file's row. PURE.</summary>
    public static string UriOf(long localId) => IdPrefix + localId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The LocalId inside an indexed file's uri. PURE.</summary>
    public static bool TryLocalIdOf(ReadOnlySpan<char> uri, out long localId)
    {
        localId = 0;
        return uri.StartsWith(IdPrefix, StringComparison.Ordinal)
               && long.TryParse(uri[IdPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out localId) && localId > 0;
    }

    /// <summary>What changed since the app opened (S5): counted after the first pass.</summary>
    public readonly record struct SessionChanges(int Added, int Moved, int Missing, int Back, int Changed)
    {
        public int Total => Added + Moved + Missing + Back + Changed;
    }

    /// <summary>The session's changes (UI thread).</summary>
    public static SessionChanges Changes { get; private set; }

    /// <summary>The last pass's changes alone (UI thread): what the "Updated · +4" chip and the "4 new songs" pill announce.</summary>
    public static SessionChanges LastChanges { get; private set; }

    /// <summary>Moves (UI thread) after every pass that changed a row; the page's pill and the chip read <see cref="LastChanges"/>.</summary>
    public static readonly FluentGpu.Signals.Signal<int> Restated = new(0);

    static readonly HashSet<long> s_new = new();
    static readonly Dictionary<long, string> s_movedFrom = new();

    /// <summary>Was this file added while the app was open (the "New" tag, S5)? UI thread.</summary>
    public static bool IsNew(long localId) => s_new.Contains(localId);

    /// <summary>Where a file moved from this session ("Moved from …" in File info, S5), or null. UI thread.</summary>
    public static string? MovedFrom(long localId) => s_movedFrom.TryGetValue(localId, out string? from) ? from : null;

    /// <summary>One row as the list shows it.</summary>
    /// <param name="Image">A cover the row shows: Spotify's, for a file whose accepted match shows Spotify's info ("Your files and Spotify"); else null.</param>
    /// <param name="Tags">The file's tags: a video file's row is staged as one (Playlist.StateLocalVideo: the film glyph, the video host, its size).</param>
    sealed record Face(long LocalId, string Title, string Artists, string Album, string SortArtist, int Disc, int Track, int DurationMs,
                       bool Unavailable, LocalState State, string FullPath, ulong Hash, string? Image = null, ulong Own = 0, LocalTagSet Tags = default);

    /// <param name="Links">The <see cref="LocalLink.Shown"/> version the faces were built from.</param>
    sealed record Pass(int Version, List<Face> Faces, Dictionary<int, Counts> Counts, int Albums, int Links = 0);

    // UI thread. `s_staged` is what the rows of `s_stagedScope` hold: a catalog scope switch (the login's welcome, `Entities.Switch`) drops every
    // row and the Local Files membership with the old table set, so the next pass stages every face again into the new one (`Rebind`).
    static readonly Dictionary<long, Face> s_staged = new();
    static Scope? s_stagedScope;
    static int s_restatedVersion = -1, s_wantedVersion = -1, s_restatedLinks = -1;
    static bool s_restating, s_baselined;
    static long s_lastRestateStart;

    // any thread
    static readonly Lock s_pathGate = new();
    static Dictionary<EntityId, long> s_pathMap = new();

    /// <summary>The file a track id plays from, or null: an indexed file through the path map (one probe under a lock, no id
    /// text read), a matched playlist item through the item map, a dropped file through its path form. ANY THREAD (the audio pump).</summary>
    public static string? PathOf(EntityId id)
    {
        // A playlist's matched spotify:local: item (D1 a): its own id, the file the index matched (LocalLibrary.Items.cs).
        if (id.Provider == EntityProvider.Spotify)
            return PathOfItem(id) ?? (PlayAsOf(id) is var file and not 0 ? PathOf(file) : null);   // phase 2: a song's default file
        long localId;
        lock (s_pathGate) { if (!s_pathMap.TryGetValue(id, out localId)) localId = 0; }
        return localId != 0 ? PathOf(localId) : Playlist.LocalPathOf(id);
    }

    /// <summary>The indexed length of a local track, or 0. Any thread.</summary>
    public static int DurationMsOf(EntityId id)
    {
        long localId;
        lock (s_pathGate) { if (!s_pathMap.TryGetValue(id, out localId) && !s_itemMap.TryGetValue(id, out localId)) return 0; }
        return s_index.TryGet(localId, out LocalEntry? e) ? Math.Max(0, e.Tags.DurationMs) : 0;
    }

    /// <summary>UI thread, right after a catalog scope switch (beside <c>Playback.Rebind</c>): the rows staged so far went with the old table set,
    /// so restate into the new one even though the index did not move. A pass already in flight stages into whatever scope is current when it
    /// lands, and <see cref="FinishPass"/> runs one more when the scope moved under it.</summary>
    public static void Rebind()
    {
        if (!s_attached && !s_fixture) return;
        if (s_stagedScope is null || ReferenceEquals(Entities.Current, s_stagedScope)) return;   // nothing staged yet, or already in this scope
        if (s_fixture && !s_restating) { RestateNow(); return; }
        MaybeRestate();
    }

    /// <summary>UI thread, from the progress post: start a pass when the index moved (at most once a second while a scan runs), or when the rows
    /// were staged into a scope that is no longer current.</summary>
    static void MaybeRestate()
    {
        if (!s_attached && !s_fixture) return;
        int version = s_index.Version;
        bool current = s_stagedScope is null || ReferenceEquals(Entities.Current, s_stagedScope);
        bool linksMoved = LocalLink.ShownVersion != s_restatedLinks;   // "Your files and Spotify": an accepted match changes a face
        if ((version == s_restatedVersion && current && !linksMoved) || s_restating) { s_wantedVersion = linksMoved ? -2 : version; return; }
        long now = Environment.TickCount64;
        if (Scanning && now - s_lastRestateStart < 1000) { s_wantedVersion = version; return; }   // the next progress post retries
        s_restating = true;
        s_lastRestateStart = now;
        new Thread(() =>
        {
            Pass pass = BuildPass();
            Playback.ToUi(() => BeginStaging(pass));
        })
        { IsBackground = true, Name = "Wavee.LocalLibrary.Restate", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>What an accepted match shows changed ("Your files and Spotify"): restate the rows whose faces it moves. UI thread.</summary>
    public static void RestateForLinks()
    {
        if (s_fixture) { if (!s_restating) RestateNow(); return; }
        MaybeRestate();
    }

    /// <summary>The inspector's link fixture changed the index: restate now. UI thread.</summary>
    internal static void InspectRestate()
    {
        Epoch.Value++;
        RestateNow();
        // A seeded library, not a change: nothing to announce.
        LastChanges = default;
        Changes = default;
        s_new.Clear();
        s_movedFrom.Clear();
    }

    /// <summary>Synchronous pass (the inspector's fixtures: no thread, the list is right on the next frame). UI thread.</summary>
    static void RestateNow()
    {
        Pass pass = BuildPass();
        s_restating = true;
        BeginStaging(pass, chunked: false);
    }

    /// <summary>Background: one face per audio entry, in list order.</summary>
    static Pass BuildPass()
    {
        int version = s_index.Version;
        // "Your files and Spotify": what an accepted match shows in place of the file's own info (an immutable map, any thread).
        int linksVersion = LocalLink.ShownVersion;
        var shown = LocalLink.Shown;
        var renames = LocalLink.Renames;
        LocalEntry[] entries = s_index.Snapshot();
        (Dictionary<int, Counts> counts, int albums) = CountPass(entries);
        var offlineRoots = new HashSet<int>();
        foreach (LocalRoot r in s_index.Roots())
            if (StateOf(r.Path) is LocalRootState.Offline or LocalRootState.SignInNeeded) offlineRoots.Add(r.RootId);
        var faces = new List<Face>(entries.Length);
        foreach (LocalEntry e in entries) faces.Add(FaceOf(e, shown, renames, offlineRoots));   // audio and video alike (the owner overrode D19)
        faces.Sort(static (a, b) =>
        {
            int c = string.Compare(a.SortArtist, b.SortArtist, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.Compare(a.Album, b.Album, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = a.Disc.CompareTo(b.Disc);
            if (c == 0) c = a.Track.CompareTo(b.Track);
            if (c == 0) c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.LocalId.CompareTo(b.LocalId);
        });
        return new Pass(version, faces, counts, albums, linksVersion);
    }

    /// <summary>One entry's row face. What the row shows (display only; the matching title stays the file's) is the one rule, LocalNames.Face
    /// (a rename, an accepted match's Spotify info, the tags, the file name's split).</summary>
    static Face FaceOf(LocalEntry e, IReadOnlyDictionary<long, LinkTrack> shown, IReadOnlyDictionary<long, LocalRename> renames, HashSet<int> offlineRoots)
    {
        LocalTagSet t = e.Tags;
        string sortArtist = t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.FirstArtist;
        bool unavailable = !e.Playable || e.State == LocalState.Missing || offlineRoots.Contains(e.RootId);
        string full = s_index.FullPathOf(e) ?? e.RelativePath;
        var face = LocalNames.Face(e, shown.TryGetValue(e.LocalId, out LinkTrack? link) ? link : null, renames.TryGetValue(e.LocalId, out LocalRename? named) ? named : null);
        (string title, string artists, string album, string? image) = (face.Title, face.Artists, face.Album, face.Image);
        ulong hash = HashFace(title, artists + "\0" + (image ?? ""), album, t.DurationMs, unavailable, e.State);
        // The file's own face: what "What changed" counts (a match accepted or undone changes what a row shows, never the file).
        ulong own = HashFace(e.MatchTitle, Playlist.LocalArtistLine(in t), t.Album, t.DurationMs, unavailable, e.State);
        return new Face(e.LocalId, title, artists, album, sortArtist, t.DiscNumber, t.TrackNumber, t.DurationMs, unavailable, e.State, full, hash, image, own, t);
    }

    static ulong HashFace(string title, string artists, string album, int durationMs, bool unavailable, LocalState state)
    {
        var h = new System.IO.Hashing.XxHash3();
        h.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(title.AsSpan()));
        h.Append([0]);
        h.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(artists.AsSpan()));
        h.Append([0]);
        h.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(album.AsSpan()));
        Span<int> tail = [durationMs, unavailable ? 1 : 0, (int)state];
        h.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tail));
        return h.GetCurrentHashAsUInt64();
    }

    /// <summary>UI thread: diff against what is staged, count the session's changes, then stage the changed rows in chunks. Every row is staged
    /// again when the scope moved since the last pass (its rows went with the old table set); the diff still counts only what the files did.</summary>
    static void BeginStaging(Pass pass, bool chunked = true)
    {
        bool restageAll = !ReferenceEquals(Entities.Current, s_stagedScope);
        s_stagedScope = Entities.Current;
        var changed = new List<Face>();
        int added = 0, moved = 0, missing = 0, back = 0, edited = 0;
        var seen = new HashSet<long>();
        foreach (Face f in pass.Faces)
        {
            seen.Add(f.LocalId);
            if (!s_staged.TryGetValue(f.LocalId, out Face? old))
            {
                changed.Add(f);
                if (s_baselined) { added++; s_new.Add(f.LocalId); }
                continue;
            }
            if (s_baselined && !string.Equals(old.FullPath, f.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                moved++;
                s_movedFrom.TryAdd(f.LocalId, old.FullPath);
            }
            if (old.Hash == f.Hash)
            {
                if (restageAll) changed.Add(f);
                else if (!ReferenceEquals(old, f)) s_staged[f.LocalId] = f;
                continue;
            }
            changed.Add(f);
            if (!s_baselined) continue;
            if (f.State == LocalState.Missing && old.State != LocalState.Missing) missing++;
            else if (f.State != LocalState.Missing && old.State == LocalState.Missing) back++;
            else if (old.Own != f.Own) edited++;
        }
        var gone = new List<long>();
        foreach (long id in s_staged.Keys) if (!seen.Contains(id)) gone.Add(id);
        foreach (long id in gone) s_staged.Remove(id);
        if (s_baselined && added + moved + missing + back + edited > 0)
        {
            LastChanges = new SessionChanges(added, moved, missing, back, edited);
            SessionChanges c = Changes;
            Changes = new SessionChanges(c.Added + added, c.Moved + moved, c.Missing + missing, c.Back + back, c.Changed + edited);
        }
        else if (s_baselined) LastChanges = default;
        s_baselined = true;
        if (!chunked) { StageChunk(changed, 0, changed.Count); FinishPass(pass, changed.Count > 0 || gone.Count > 0); return; }
        StageNext(pass, changed, 0, changed.Count > 0 || gone.Count > 0);
    }

    static void StageNext(Pass pass, List<Face> changed, int from, bool any)
    {
        const int chunk = 2000;
        int to = Math.Min(changed.Count, from + chunk);
        StageChunk(changed, from, to);
        if (to < changed.Count) { Playback.ToUi(() => StageNext(pass, changed, to, any)); return; }
        FinishPass(pass, any);
    }

    /// <summary>Stage rows [from, to) in one commit, the way <c>ImportLocalFile</c> stages a dropped file.</summary>
    static void StageChunk(List<Face> faces, int from, int to)
    {
        if (to <= from) return;
        var scope = Entities.Current;
        var staging = Staging.Rent();
        staging.Epoch = scope.Epoch;
        for (int i = from; i < to; i++)
        {
            Face f = faces[i];
            LocalTagSet tags = f.Tags;
            string uri = UriOf(f.LocalId);
            ref var row = ref staging.Tracks.RowFor(new StagedId(staging.AddText(Encoding.UTF8.GetBytes(uri))), Authority.Local,
                (uint)(TrackFields.Identity | TrackFields.Availability) | Playlist.LocalVideoFields(in tags));
            row.Title = staging.AddText(Encoding.UTF8.GetBytes(f.Title));
            if (f.Artists.Length > 0) row.ArtistLine = staging.AddText(Encoding.UTF8.GetBytes(f.Artists));
            // The cover: an accepted Spotify match's ("Your files and Spotify", show-only), else the one LocalArt already found (a peek: staging
            // never queues an extraction; a row asks on first display, `LocalArt.Want`). StageCovers re-stages through here, so both arrive one way.
            // A video file's cover is a still of it (LocalArt.FileCoverUri: asked for here, the row re-stated when it lands, StageLocalImages).
            string? still = tags.Kind == LocalKind.Video ? LocalArt.FileCoverUri(f.FullPath) : null;
            if ((f.Image ?? LocalArt.Known(f.LocalId) ?? still) is { Length: > 0 } cover) row.Image = staging.AddText(Encoding.UTF8.GetBytes(cover));
            row.DurationMs = Math.Max(0, f.DurationMs);
            row.Flags = (uint)(TrackFlags.Local | (f.Unavailable ? TrackFlags.Unavailable : 0));
            Playlist.StateLocalVideo(ref row, in tags);   // a video file: the film glyph, the video host, its size (#240 local video)
            // Its picture as a video (the stage's poster while it opens, the queue's still): the frame it wears (#240 version cards).
            if (tags.Kind == LocalKind.Video && LocalLink.FrameUrlOf(f.LocalId) is { } frame) row.VideoImage = staging.AddText(Encoding.UTF8.GetBytes(frame));
            Playlist.NoteLocalNames(uri, f.Artists, f.Album);
            s_staged[f.LocalId] = f;
        }
        Entities.Commit(staging);
        Staging.Return(staging);
    }

    /// <summary>LocalArt found covers for <paramref name="localIds"/>: re-state those rows with them (UI thread), so every surface that draws a
    /// track's cover (the rows, the player bar, Now playing, the OS media card) shows it through the row's own Image column; the cover fades in
    /// over the no-art tile in the same slot. A row not staged (yet) takes its cover at its staging instead.</summary>
    internal static void StageCovers(IReadOnlyList<long> localIds)
    {
        var faces = new List<Face>(localIds.Count);
        foreach (long id in localIds) if (s_staged.TryGetValue(id, out Face? f)) faces.Add(f);
        if (faces.Count == 0) return;
        StageChunk(faces, 0, faces.Count);
        Entities.Publish();
    }

    /// <summary>A video still landed for <paramref name="fullPath"/> (Playlist.StageLocalImages): re-state its indexed row, if it has one. UI thread.</summary>
    internal static bool StageStill(string fullPath)
    {
        if (!s_index.TryGetByPath(fullPath, out LocalEntry? e) || !s_staged.ContainsKey(e.LocalId)) return false;
        StageCovers([e.LocalId]);
        return true;
    }

    /// <summary>The indexed file behind a track id: an indexed row's, or a matched playlist item's; 0 for any other row. ANY THREAD.</summary>
    public static long LocalIdOf(EntityId id)
    {
        lock (s_pathGate) return s_pathMap.TryGetValue(id, out long localId) || s_itemMap.TryGetValue(id, out localId) ? localId : 0;
    }

    /// <summary>Replace the Local Files membership (indexed files in album order, then session-dropped ones) and the path map.</summary>
    static void FinishPass(Pass pass, bool any)
    {
        s_counts = pass.Counts;
        Volatile.Write(ref s_albums, pass.Albums);
        try
        {
            var scope = Entities.Current;
            var slots = new List<int>(pass.Faces.Count + 16);
            var map = new Dictionary<EntityId, long>(pass.Faces.Count);
            foreach (Face f in pass.Faces)
            {
                if (!scope.Tracks.TryGetSlot(UriOf(f.LocalId).AsSpan(), out int slot)) continue;
                slots.Add(slot);
                map[new Track(slot).Id] = f.LocalId;
            }
            lock (s_pathGate) s_pathMap = map;
            Playlist.EnsureLocalFilesHeader();
            var local = Playlist.LocalFiles;
            var e = scope.Edges.PlaylistTracks;
            // A session-dropped file (its path form) stays after the indexed files; an old indexed row that left the index goes.
            foreach (int member in e.Targets(local.Slot))
            {
                string text = new Track(member).Uri.Text;
                if (text.StartsWith(Playlist.LocalFilePrefix, StringComparison.Ordinal) && !text.StartsWith(IdPrefix, StringComparison.Ordinal)) slots.Add(member);
            }
            if (any || e.State(local.Slot) != EdgeState.Complete || !SameMembers(e.Targets(local.Slot), slots))
            {
                int addedAt = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var payload = new PlaylistTrackEdge[slots.Count];
                ReadOnlySpan<PlaylistTrackEdge> old = e.Payload(local.Slot);
                ReadOnlySpan<int> oldTargets = e.Targets(local.Slot);
                var oldAt = new Dictionary<int, PlaylistTrackEdge>(oldTargets.Length);
                for (int i = 0; i < oldTargets.Length && i < old.Length; i++) oldAt[oldTargets[i]] = old[i];
                for (int i = 0; i < slots.Count; i++)
                    payload[i] = oldAt.TryGetValue(slots[i], out PlaylistTrackEdge kept) ? kept : new PlaylistTrackEdge(default, addedAt, 0, 0, 0, 0, 0);
                e.Replace(local.Slot, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(slots), payload, EdgeState.Complete, slots.Count);
                local.Refold();
            }
            Entities.Publish();
            RestateItems();   // step 2: the playlists' spotify:local: items this session saw, re-ruled against the new index
        }
        finally
        {
            s_restatedVersion = pass.Version;
            s_restatedLinks = pass.Links;
            s_restating = false;
            if (any) Restated.Value++;
        }
        // The index moved during the pass, the scope switched under a chunked one (its first rows went with the old table set), or what an
        // accepted "Your files and Spotify" match shows moved: one more.
        if ((s_wantedVersion != s_restatedVersion && s_wantedVersion >= 0) || s_wantedVersion == -2 || LocalLink.ShownVersion != s_restatedLinks
            || !ReferenceEquals(Entities.Current, s_stagedScope))
        {
            s_wantedVersion = -1;
            MaybeRestate();
        }
    }

    static bool SameMembers(ReadOnlySpan<int> current, List<int> wanted)
    {
        if (current.Length != wanted.Count) return false;
        for (int i = 0; i < current.Length; i++) if (current[i] != wanted[i]) return false;
        return true;
    }
}
