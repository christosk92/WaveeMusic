// ── Shell/Video.LinkedVideos.cs ────────────────────────────────────────────────────────────────────────────────────
// A Spotify track's video of mine: a video file on this PC LINKED to the track as one of its versions ("Your files and Spotify", one concept,
// owner 2026-11-24). There is no separate attachment store: attaching a video (the row's .mp4 drop, the row menu's Video › Attach, Settings'
// Replace / Locate) adds the file to the library (LocalLibrary.AddFile) and links it (LocalLink.LinkVersion); removing it unlinks it. The link
// store (locallinks.tsv) is the one home of the pairing; this file is the video surfaces' view of it:
//   · `LinkedVideos`: what the menu, the drop target and Settings ask (one record per track: its video version), and the verbs;
//   · `LinkedVideoMirror`: the row bit every "has video" reader goes through (`TrackFlags.LinkedVideo`, `TrackTable.LocalVideo`), written when
//     the links move (LocalLink.FeedVideos) and read by the track commit for rows that land later;
//   · the one-time migration of the old attachment store ("video.overrides.v1"): each record becomes an entry + a link, then the store is emptied.
// The video host plays a linked version through the local-video tier (Playback.Video.LocalVideos, fed by LocalLink.FeedVideos).
//
// Role: SHELL (UI thread; the hot reads are lock-guarded probes)

using FluentGpu.Signals;

namespace Wavee;

public static partial class Video
{
    /// <summary>The video surfaces' view of the linked video versions (see the file header).</summary>
    public static class LinkedVideos
    {
        static readonly HashSet<string> Quarantine = new(StringComparer.Ordinal);   // (uri + '\u001f' + path), this session
        static readonly HashSet<string> WarnedMissing = new(StringComparer.Ordinal);
        static readonly Lock Gate = new();

        /// <summary>Moves with every change of the links (an attach, a remove, an undo, a default picked).</summary>
        public static IReadSignal<int> Epoch => LocalLink.Version;

        /// <summary>`--fake --fake-video`: the one local clip every `spotify:track:` playable resolves to, in memory only (never linked).</summary>
        public static string? FakeClip { get; set; }

        /// <summary>Existence probes (injectable for the inspector).</summary>
        public static Func<string, bool> FileExists { get; set; } = File.Exists;
        public static Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

        /// <summary>Raised at most once per session per track when a play-time resolve finds its video file gone.</summary>
        public static event Action<string>? BrokenLink;

        /// <summary>The verbs are always there (the link store is): kept as the surfaces' one gate.</summary>
        public static bool Present => true;

        /// <summary>How many tracks have a video version.</summary>
        public static int Count => Tracks().Count;

        /// <summary>Does the track have a video version (a missing one included: the user still wants video here)?</summary>
        public static bool Has(string? uri) => TryGet(uri, out _);

        /// <summary>The track's video version as a record (its path; added = when the file joined the library; source key = its path).</summary>
        public static bool TryGet(string? uri, out LinkedVideoRecord record)
        {
            record = default;
            if (uri is not { Length: > 0 }) return false;
            if (FakeClip is { } clip && uri.StartsWith("spotify:track:", StringComparison.Ordinal)) { record = new LinkedVideoRecord(uri, clip, 0, clip); return true; }
            long file = LocalLink.VideoFileOf(uri);
            if (file == 0 || LocalLibrary.PathOf(file) is not { } path) return false;
            long added = LocalLibrary.Index.TryGet(file, out LocalEntry? e) && e.FirstSeenTicks > 0
                ? new DateTimeOffset(e.FirstSeenTicks, TimeSpan.Zero).ToUnixTimeSeconds() : 0;
            record = new LinkedVideoRecord(uri, path, added, path);
            return true;
        }

        /// <summary>Every track's video version (allocates: Settings' list).</summary>
        public static List<LinkedVideoRecord> All()
        {
            var list = new List<LinkedVideoRecord>();
            foreach (string uri in Tracks()) if (TryGet(uri, out LinkedVideoRecord r)) list.Add(r);
            return list;
        }

        static List<string> Tracks()
        {
            var songs = new List<string>();
            foreach (var (song, _) in LocalLink.VideoVersions()) if (!songs.Contains(song)) songs.Add(song);
            return songs;
        }

        /// <summary>Is the track's video version there to play: the same answer Settings and the menu show.</summary>
        public static LinkedVideoDecision Decide(string? uri)
        {
            if (!TryGet(uri, out LinkedVideoRecord rec)) return LinkedVideoDecision.None;
            bool quarantined;
            lock (Gate) quarantined = Quarantine.Contains(rec.Uri + '\u001f' + rec.Path);
            var d = LinkedVideoUx.Decide(true, in rec, quarantined, FileExists);
            if (d.Tier == LinkedVideoTier.Broken)
            {
                bool first;
                lock (Gate) first = WarnedMissing.Add(rec.Uri);
                if (first) BrokenLink?.Invoke(rec.Uri);
            }
            return d;
        }

        public static LinkedVideoDecision DecideRecord(LinkedVideoRecord rec) => Decide(rec.Uri);

        /// <summary>Attach a video file to a track: the file joins the library and is linked to the track as its video version (a previous video
        /// version is unlinked: it stays in the library). Null when the file is refused. UI thread (one header read).</summary>
        public static LinkedVideoChange? Attach(string uri, string path, string sourceKey, long nowUnix) => Link(uri, path, allowMissing: false);

        static LinkedVideoChange? Link(string uri, string path, bool allowMissing)
        {
            // A Spotify track's video version: a local row is its own file (its video plays as itself, #240 local video).
            if (uri is not { Length: > 0 } || !EntityId.TryParse(uri, out EntityId id) || id.Provider != EntityProvider.Spotify) return null;
            if (!allowMissing && LinkedVideoUx.Validate(path, FileExists) != AttachRejection.None) return null;
            bool read = LocalTags.TryRead(path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why);
            if (!read && allowMissing && !File.Exists(path))
                tags = new LocalTagSet(LocalKind.Video, "", [], "", "", 0, 0, 0, 0, false, 0, LocalCover.None, 0, 0, true, 0, 0, 0);
            Track file = LocalLibrary.AddFile(path, format, in tags, read, why, allowMissing);
            if (!file.IsValid || !LocalLibrary.TryLocalIdOf(file.Uri.Text, out long localId)) return null;
            long before = LocalLink.VideoFileOf(uri);
            if (before != 0 && before != localId) LocalLink.Unlink(before);
            if (!LocalLink.LinkVersion(localId, Entities.Track(id))) return null;
            lock (Gate) { Quarantine.RemoveWhere(k => k.StartsWith(uri + '\u001f', StringComparison.Ordinal)); WarnedMissing.Remove(uri); }
            return before != 0 ? LinkedVideoChange.Replace : LinkedVideoChange.Attach;
        }

        /// <summary>Unlink the track's video versions (the files stay in the library). Whether anything was linked.</summary>
        public static bool Remove(string uri)
        {
            bool any = false;
            for (long file = LocalLink.VideoFileOf(uri); file != 0; file = LocalLink.VideoFileOf(uri)) { LocalLink.Unlink(file); any = true; }
            lock (Gate) WarnedMissing.Remove(uri);
            return any;
        }

        /// <summary>Did this track's video file fail to open this session? Any thread (the resolver asks it on an api thread).</summary>
        public static bool IsQuarantined(string uri, string path)
        {
            lock (Gate) return Quarantine.Count > 0 && Quarantine.Contains(uri + '\u001f' + path);
        }

        /// <summary>This track's video file failed to open this session: skipped until a restart or a replace.</summary>
        public static void Quarantined(string uri, string path)
        {
            bool added;
            lock (Gate) added = Quarantine.Add(uri + '\u001f' + path);
            if (added) Log.Warn(State.LogCategory, "local video failed to open, skipped for this session");
        }

        // ── the one-time move of the old attachment store ────────────────────────────────────────────────────────────

        /// <summary>The store attachments lived in before they were links (one line per attachment: uri, added, source key, path).</summary>
        static readonly SettingKey<string> s_legacy = new("video.overrides.v1", "");

        /// <summary>Once, after the library and the links have loaded (LocalLink.Use): every old attachment becomes a library entry linked to its
        /// track (a file that is gone stays as a Missing entry, so nothing is lost), then the old store is emptied. UI thread.</summary>
        public static int MigrateLegacy()
        {
            string raw = Platform.Settings.Get(s_legacy);
            if (raw.Length == 0) return 0;
            int moved = 0, failed = 0;
            foreach (string line in raw.Split('\n'))
            {
                string[] parts = line.Split('\t', 4);
                if (parts.Length != 4 || parts[0].Length == 0 || parts[3].Length == 0) continue;
                if (Link(parts[0], parts[3], allowMissing: true) is not null) moved++; else failed++;
            }
            Platform.Settings.Set(s_legacy, "");
            Log.Info(State.LogCategory, "local video attachments moved to links: " + moved + (failed > 0 ? ", unreadable: " + failed : ""));
            return moved;
        }

        /// <summary>The inspector: write an old-style attachment, as an older build left it.</summary>
        internal static void SeedLegacy(string uri, string path)
            => Platform.Settings.Set(s_legacy, uri + "\t" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\t" + path + "\t" + path);
    }

    /// <summary>The row bit of a linked video version: <see cref="TrackFlags.LinkedVideo"/> and <c>TrackTable.LocalVideo</c>, written when the links
    /// move (a direct column write + Bump + Publish, never a staged answer: the Video group's Known bit stays the catalogue's) and read by the track
    /// commit for a row that lands later. UI thread for the writes; the reads are a locked probe.</summary>
    public static class LinkedVideoMirror
    {
        static readonly Dictionary<EntityId, string> Index = new();
        static readonly Dictionary<EntityId, string> Frames = new();   // the frame a track's video version wears (#240 version cards)
        static readonly Lock Gate = new();

        /// <summary>The frame a track's linked video version wears (its <c>VideoImage</c>: the album's video card, the queue's still, the stage's
        /// poster), or none (the album cover was chosen). The commit's question after the Video arm, which a catalogue answer would overwrite.</summary>
        public static bool TryFrame(EntityId id, out string frame)
        {
            frame = "";
            lock (Gate)
            {
                if (Frames.Count == 0 || !Frames.TryGetValue(id, out string? found)) return false;
                frame = found;
                return true;
            }
        }

        /// <summary>Does the row's track have a linked video version, and where (the commit's question for every staged row)?</summary>
        public static bool TryPath(EntityId id, out string path)
        {
            path = "";
            if (id.IsEmpty) return false;
            if (LinkedVideos.FakeClip is { } clip && id.Provider == EntityProvider.Spotify) { path = clip; return true; }
            lock (Gate)
            {
                if (Index.Count == 0 || !Index.TryGetValue(id, out string? found)) return false;
                path = found;
                return true;
            }
        }

        /// <summary>The links moved (LocalLink.FeedVideos): track uri → its video file, before and now. Writes the rows that changed. UI thread.</summary>
        public static void Apply(IReadOnlyDictionary<string, LinkedVideoFeed> before, IReadOnlyDictionary<string, LinkedVideoFeed> now)
        {
            var changed = new List<(EntityId Id, LinkedVideoFeed? Feed)>();
            lock (Gate)
            {
                foreach (var (uri, _) in before)
                    if (!now.ContainsKey(uri) && EntityId.TryParse(uri, out EntityId gone)) { Index.Remove(gone); Frames.Remove(gone); changed.Add((gone, null)); }
                foreach (var (uri, feed) in now)
                    if ((!before.TryGetValue(uri, out LinkedVideoFeed? was) || was != feed) && EntityId.TryParse(uri, out EntityId id))
                    {
                        Index[id] = feed.Path;
                        if (feed.Frame is { } f) Frames[id] = f; else Frames.Remove(id);
                        changed.Add((id, feed));
                    }
            }
            if (changed.Count == 0 || Entities.Current is not { } scope) return;
            var t = scope.Tracks;
            bool any = false;
            foreach (var (id, feed) in changed)
            {
                if (!t.TryGetSlot(id, out int slot) || slot <= Table.None) continue;
                if (feed is not null)
                {
                    t.Flags[slot] |= (uint)TrackFlags.LinkedVideo;
                    t.SetText(ref t.LocalVideo, slot, Entities.Strings.Intern(feed.Path));
                    // The video's picture (the album's video card, the queue's still, the stage's poster): its frame, or none for the cover.
                    if (feed.Frame is { } frame) t.SetText(ref t.VideoImage, slot, Entities.Strings.Intern(frame));
                    else if ((t.Flags[slot] & (uint)TrackFlags.HasVideo) == 0) t.ClearText(ref t.VideoImage, slot);
                }
                else
                {
                    t.Flags[slot] &= ~(uint)TrackFlags.LinkedVideo;
                    t.ClearText(ref t.LocalVideo, slot);
                    if ((t.Flags[slot] & (uint)TrackFlags.HasVideo) == 0) t.ClearText(ref t.VideoImage, slot);
                }
                t.Bump(slot);   // not the Video group's Known bit: this is the user's link, not a catalogue answer
                any = true;
            }
            if (any) Entities.Publish();
            // The badge of the playing track, now (its upgrade is deferred: the surface waits for the user's click).
            var current = Playback.CurrentId.Peek();
            foreach (var (id, _) in changed)
                if (current.Equals(id)) State.FoldForTrack(t.TryGetSlot(id, out int s) && s > Table.None ? new Track(s).HasVideo : TryPath(id, out _), boundary: false);
        }

        /// <summary>`--fake --fake-video`: the rows the fake seed committed before start-up get the clip's bit now (later rows by <see cref="TryPath"/>).</summary>
        public static void ApplyFakeClip()
        {
            if (LinkedVideos.FakeClip is not { } clip || Entities.Current is not { } scope) return;
            var t = scope.Tracks;
            var interned = Entities.Strings.Intern(clip);
            for (int slot = 1; slot < t.Count; slot++)
            {
                if (t.Id[slot].IsEmpty || t.Id[slot].Provider != EntityProvider.Spotify) continue;
                t.Flags[slot] |= (uint)TrackFlags.LinkedVideo;
                t.SetText(ref t.LocalVideo, slot, interned);
                t.Bump(slot);
            }
            Entities.Publish();
        }
    }
}
