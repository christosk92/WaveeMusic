// ── Playback/LocalCollection.cs ────────────────────────────────────────────────────────────────────────────────────
// the "On this PC" page's model: albums, artists, folders, stats, shelves and insights folded from the local index (#240 P2)
//
// Role: SHELL (the fold runs on a pool thread; the result is published on the UI thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-files-page-concept.md (§1-§6), 240-local-files-ux.md S10 (Insights),
//       240-p2-contract.md (P2-page row: "Aggregation in Playback/LocalCollection.cs ... recomputed off the UI thread on LocalLibrary.Epoch")
//
// WHEN. `Request()` (the page calls it from an effect that reads `LocalLibrary.Epoch` and `LocalArt.Epoch`) schedules one fold on the
// pool; a request while a fold runs marks it dirty and the fold runs once more when it ends, so a scan's 4-per-second epochs never
// queue more than one fold. The fold reads `LocalLibrary.Index.Snapshot()` (one copy under the index's lock) and the folder states,
// and `Playback.ToUi` publishes the new `View` (a new instance per fold; `Version` counts them). Nothing here runs per frame.
//
// WHAT. Albums are (album artist, else first artist; album) pairs from the tags, artists are album artists (else first artists):
// names, never Spotify entities (concept "What it changes"). An album's "added" time is its newest file's write time: the index keeps
// no first-seen time (LocalEntry has LastSeenTicks only), so a file copied in keeps its own write time. Quality is the format and,
// for lossy files, the bitrate ("FLAC", "MP3 320"); the tag set carries no sample rate or bit depth, so nothing is called hi-res
// unless the reader says Flac24. Untagged files (no album) count as songs and live in the Folders browser, not on the album wall.

using System.Globalization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The local collection's folded view (see the file header).</summary>
public static class LocalCollection
{
    static readonly Signal<LocalCollectionView> s_view = new(LocalCollectionView.Empty);
    static int s_running, s_dirty, s_version;

    /// <summary>The latest fold (UI thread). <see cref="LocalCollectionView.Version"/> 0 until the first one lands.</summary>
    public static IReadSignal<LocalCollectionView> View => s_view;

    /// <summary>Fold again (any thread): one fold at a time, a request during one runs one more after it.</summary>
    public static void Request()
    {
        if (Interlocked.Exchange(ref s_running, 1) != 0) { Volatile.Write(ref s_dirty, 1); return; }
        ThreadPool.UnsafeQueueUserWorkItem(static _ => Loop(), null);
    }

    static void Loop()
    {
        while (true)
        {
            Volatile.Write(ref s_dirty, 0);
            LocalCollectionView view;
            try { view = Fold(DateTime.UtcNow.Ticks); }
            catch (Exception ex) { Log.Warn("local.collection", "fold failed", ex); view = s_view.Peek(); }
            Playback.ToUi(() => s_view.Value = view);
            if (Volatile.Read(ref s_dirty) == 0)
            {
                Volatile.Write(ref s_running, 0);
                // A request between the check and the release found running=1 and only set dirty: take it now.
                if (Volatile.Read(ref s_dirty) == 0 || Interlocked.Exchange(ref s_running, 1) != 0) return;
            }
        }
    }

    // ── labels (format names, never localised) ───────────────────────────────────────────────────────────────────────

    /// <summary>"FLAC", "MP3 320", "AAC 256", "Ogg 160", or "" when unknown.</summary>
    public static string QualityOf(Spotify.Audio.Format format, int kbps) => format switch
    {
        Spotify.Audio.Format.Flac24 => "Hi-Res FLAC",
        Spotify.Audio.Format.Flac => "FLAC",
        Spotify.Audio.Format.Mp3 => kbps > 0 ? "MP3 " + Round(kbps) : "MP3",
        Spotify.Audio.Format.Aac => kbps > 0 ? "AAC " + Round(kbps) : "AAC",
        Spotify.Audio.Format.OggVorbis96 or Spotify.Audio.Format.OggVorbis160 or Spotify.Audio.Format.OggVorbis320
            => kbps > 0 ? "Ogg " + Round(kbps) : "Ogg",
        _ => "",
    };

    static string Round(int kbps) => (kbps >= 310 ? 320 : kbps >= 250 ? 256 : kbps >= 180 ? 192 : kbps >= 150 ? 160 : kbps >= 120 ? 128 : kbps).ToString(CultureInfo.InvariantCulture);

    static bool IsLossless(Spotify.Audio.Format f) => f is Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24;

    static string FormatName(Spotify.Audio.Format f) => f switch
    {
        Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24 => "FLAC",
        Spotify.Audio.Format.Mp3 => "MP3",
        Spotify.Audio.Format.Aac => "AAC",
        Spotify.Audio.Format.OggVorbis96 or Spotify.Audio.Format.OggVorbis160 or Spotify.Audio.Format.OggVorbis320 => "Ogg",
        _ => "Other",
    };

    /// <summary>The album key the page's verbs take: (album artist or first artist, album), case-folded.</summary>
    public static string AlbumKey(string artist, string album) => artist.ToUpperInvariant() + " / " + album.ToUpperInvariant();

    static string ArtistOf(in LocalTagSet t) => t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.FirstArtist;

    /// <summary>The ids of the playable files in an album, in disc/track order (UI thread or any).</summary>
    public static List<long> AlbumFiles(string key)
    {
        var list = new List<(int Disc, int Track, string Title, long Id)>();
        foreach (var e in LocalLibrary.Index.Snapshot())
        {
            if (e.State == LocalState.Missing || !e.Playable || e.Tags.Album.Length == 0) continue;
            if (AlbumKey(ArtistOf(e.Tags), e.Tags.Album) != key) continue;
            list.Add((e.Tags.DiscNumber, e.Tags.TrackNumber, e.MatchTitle, e.LocalId));
        }
        list.Sort(static (a, b) => a.Disc != b.Disc ? a.Disc.CompareTo(b.Disc) : a.Track != b.Track ? a.Track.CompareTo(b.Track) : string.CompareOrdinal(a.Title, b.Title));
        return list.ConvertAll(static x => x.Id);
    }

    /// <summary>The ids of the playable files that match a filter (artist key, decade, folder), in album order.</summary>
    public static List<long> FilesWhere(Func<LocalEntry, bool> keep)
    {
        var all = LocalLibrary.Index.Snapshot();
        var list = new List<LocalEntry>(all.Length);
        foreach (var e in all) if (e.State != LocalState.Missing && e.Playable && keep(e)) list.Add(e);
        list.Sort(static (a, b) =>
        {
            int c = string.Compare(ArtistOf(a.Tags), ArtistOf(b.Tags), StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.Compare(a.Tags.Album, b.Tags.Album, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = a.Tags.DiscNumber.CompareTo(b.Tags.DiscNumber);
            if (c == 0) c = a.Tags.TrackNumber.CompareTo(b.Tags.TrackNumber);
            return c != 0 ? c : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        });
        return list.ConvertAll(static e => e.LocalId);
    }

    /// <summary>Is <paramref name="e"/> inside the folder node <paramref name="key"/> ("root|rel"), recursively?</summary>
    public static bool InFolder(LocalEntry e, string key)
    {
        int bar = key.IndexOf('|');
        if (bar < 0 || !int.TryParse(key.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out int root) || e.RootId != root) return false;
        string rel = key[(bar + 1)..];
        return rel.Length == 0 || (e.RelativePath.Length > rel.Length && e.RelativePath.StartsWith(rel, StringComparison.OrdinalIgnoreCase) && e.RelativePath[rel.Length] == '\\');
    }

    public static bool IsArtist(LocalEntry e, string key) => ArtistOf(e.Tags).ToUpperInvariant() == key;

    public static bool InDecade(LocalEntry e, int decade) => e.Tags.Year > 0 && e.Tags.Year / 10 * 10 == decade;

    // ── the fold ─────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class AlbumAcc
    {
        public string Key = "", Title = "", Artist = "";
        public int Year, Tracks, Lossless, Lossy, MaxKbps;
        public long Duration, Added;
        public Spotify.Audio.Format Format;
        public bool MixedFormat;
        public int Root;
        public string Folder = "";
    }

    sealed class FolderAcc
    {
        public string Key = "", Name = "";
        public string? Parent;
        public int Root;
        public string Rel = "";
        public int Songs;
        public long Duration, DurationHere;
        public readonly List<string> Children = new();
        public readonly List<LocalEntry> Files = new();
        public readonly List<string> AlbumKeys = new();
        public string? Image;
        public Spotify.Audio.Format Format;
        public bool Mixed;
        public int Kbps;
    }

    static readonly string[] s_coverNames = ["cover", "folder", "front", "album"];

    static LocalCollectionView Fold(long now)
    {
        var index = LocalLibrary.Index;
        LocalEntry[] entries = index.Snapshot();
        LocalRoot[] roots = index.Roots();
        IReadOnlyList<string> folders = LocalLibrary.Folders;
        string word = LocalLibrary.StateWord;
        var phase = word switch { "none" => LocalLibraryPhase.None, "scanning" => LocalLibraryPhase.Scanning, "offline" => LocalLibraryPhase.Offline, _ => LocalLibraryPhase.Ready };

        var albums = new Dictionary<string, AlbumAcc>(StringComparer.Ordinal);
        var artists = new Dictionary<string, (string Name, HashSet<string> Albums, int Tracks)>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, FolderAcc>(StringComparer.Ordinal);
        var rootKeyOf = new Dictionary<int, string>();
        var rootPath = new Dictionary<int, string>();
        foreach (var r in roots) { rootPath[r.RootId] = r.Path; }

        int songs = 0, lossless = 0, lossy = 0, untagged = 0, noAlbumArtist = 0, noTrack = 0, missing = 0, cloud = 0;
        long duration = 0, bytes = 0;
        var formats = new Dictionary<string, int>(StringComparer.Ordinal);
        var bitrates = new Dictionary<string, int>(StringComparer.Ordinal);
        var unplayable = new Dictionary<string, int>(StringComparer.Ordinal);
        var dupes = new Dictionary<(string, string, int), int>();
        var perRootSongs = new Dictionary<int, int>();

        foreach (var e in entries)
        {
            if (e.Tags.Kind != LocalKind.Audio) continue;
            songs++;
            bytes += e.Size;
            perRootSongs[e.RootId] = perRootSongs.GetValueOrDefault(e.RootId) + 1;
            if (e.State == LocalState.Missing) missing++;
            if (e.State == LocalState.CloudOnly) cloud++;
            if (!e.Playable) { string why = e.Why.Length > 0 ? e.Why : FormatName(e.Format); unplayable[why] = unplayable.GetValueOrDefault(why) + 1; }
            var t = e.Tags;
            duration += t.DurationMs;
            if (e.Playable && e.State != LocalState.Missing)
            {
                if (IsLossless(e.Format)) lossless++; else if (e.Format != Spotify.Audio.Format.Unknown) lossy++;
                string fn = FormatName(e.Format);
                formats[fn] = formats.GetValueOrDefault(fn) + 1;
                if (!IsLossless(e.Format) && t.BitrateKbps > 0) { string b = Round(t.BitrateKbps); bitrates[b] = bitrates.GetValueOrDefault(b) + 1; }
            }
            if (t.Title.Length == 0 && t.Artists.Length == 0) untagged++;
            if (t.Album.Length > 0 && t.AlbumArtist.Length == 0) noAlbumArtist++;
            if (t.Album.Length > 0 && t.TrackNumber == 0) noTrack++;
            if (t.Title.Length > 0 && t.DurationMs > 0) { var dk = (t.Title.ToUpperInvariant(), t.FirstArtist.ToUpperInvariant(), t.DurationMs / 2000); dupes[dk] = dupes.GetValueOrDefault(dk) + 1; }

            // Albums and artists (tagged files only).
            string artist = ArtistOf(t);
            if (t.Album.Length > 0)
            {
                string key = AlbumKey(artist, t.Album);
                if (!albums.TryGetValue(key, out var a))
                {
                    a = new AlbumAcc { Key = key, Title = t.Album, Artist = artist, Format = e.Format, Root = e.RootId, Folder = AlbumFolderOf(e.RelativePath) };
                    albums[key] = a;
                }
                a.Tracks++;
                a.Duration += t.DurationMs;
                if (t.Year > 0 && (a.Year == 0 || t.Year < a.Year)) a.Year = t.Year;
                long added = e.WriteTicks > 0 ? e.WriteTicks : e.LastSeenTicks;
                if (added > a.Added) a.Added = added;
                if (e.Format != a.Format) a.MixedFormat = true;
                if (IsLossless(e.Format)) a.Lossless++; else a.Lossy++;
                if (t.BitrateKbps > a.MaxKbps) a.MaxKbps = t.BitrateKbps;
            }
            if (artist.Length > 0)
            {
                string ak = artist.ToUpperInvariant();
                if (!artists.TryGetValue(ak, out var ar)) ar = (artist, new HashSet<string>(StringComparer.Ordinal), 0);
                if (t.Album.Length > 0) ar.Albums.Add(AlbumKey(artist, t.Album));
                artists[ak] = (ar.Name, ar.Albums, ar.Tracks + 1);
            }

            // Folders: the file's folder and every folder above it up to its music folder.
            var leaf = NodeOf(nodes, rootKeyOf, rootPath, e.RootId, DirOf(e.RelativePath));
            leaf.Files.Add(e);
            leaf.DurationHere += t.DurationMs;
            if (leaf.Files.Count == 1) { leaf.Format = e.Format; leaf.Kbps = t.BitrateKbps; }
            else if (leaf.Format != e.Format) leaf.Mixed = true;
            if (t.Album.Length > 0) { string k = AlbumKey(artist, t.Album); if (!leaf.AlbumKeys.Contains(k)) leaf.AlbumKeys.Add(k); }
            for (FolderAcc? n = leaf; n is not null; n = n.Parent is null ? null : nodes[n.Parent])
            {
                n.Songs++;
                n.Duration += t.DurationMs;
            }
        }

        // Folder images from the listing (no bytes read): a fixed cover name, the file must still exist.
        foreach (var f in index.Folders())
        {
            if (f.Images.Length == 0) continue;
            string key = KeyOf(f.RootId, f.RelativePath);
            if (!nodes.TryGetValue(key, out var n) || !rootPath.TryGetValue(f.RootId, out string? rp)) continue;
            foreach (string img in f.Images)
            {
                string stem = Path.GetFileNameWithoutExtension(img);
                if (Array.FindIndex(s_coverNames, c => string.Equals(c, stem, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                string full = LocalIndex.Join(LocalIndex.Join(rp, f.RelativePath), img);
                if (File.Exists(full)) { n.Image = new Uri(full).AbsoluteUri; break; }
            }
        }

        // Cards.
        var cards = new List<LocalAlbumCard>(albums.Count);
        var coverOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        DateTime local = new DateTime(now, DateTimeKind.Utc).ToLocalTime();
        long today = local.Date.ToUniversalTime().Ticks;
        long week = local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)).ToUniversalTime().Ticks;
        long month = new DateTime(local.Year, local.Month, 1).ToUniversalTime().Ticks;
        foreach (var a in albums.Values)
        {
            string? cover = LocalArt.AlbumCoverUri(a.Artist, a.Title) ?? LocalCollectionFixture.CoverOf(a.Artist, a.Title);
            coverOf[a.Key] = cover;
            var when = a.Added >= today ? LocalAddedWhen.Today : a.Added >= week ? LocalAddedWhen.ThisWeek : a.Added >= month ? LocalAddedWhen.ThisMonth : LocalAddedWhen.Earlier;
            string quality = a.Lossless > 0 && a.Lossy == 0 ? QualityOf(a.Format, 0) : a.Lossy > 0 && !a.MixedFormat ? QualityOf(a.Format, a.MaxKbps) : "";
            cards.Add(new LocalAlbumCard(a.Key, a.Title, a.Artist, a.Year, a.Tracks, a.Duration, quality, a.Lossless > 0 && a.Lossy == 0,
                when == LocalAddedWhen.Today, cover, a.Added, when, KeyOf(a.Root, a.Folder)));
        }
        cards.Sort(static (x, y) => y.AddedTicks != x.AddedTicks ? y.AddedTicks.CompareTo(x.AddedTicks) : string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase));
        var byArtist = new List<LocalAlbumCard>(cards);
        byArtist.Sort(static (x, y) => { int c = string.Compare(x.Artist, y.Artist, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : x.Year != y.Year ? x.Year.CompareTo(y.Year) : string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase); });
        var byTitle = new List<LocalAlbumCard>(cards);
        byTitle.Sort(static (x, y) => string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase));
        var byYear = new List<LocalAlbumCard>(cards);
        byYear.Sort(static (x, y) => x.Year != y.Year ? y.Year.CompareTo(x.Year) : string.Compare(x.Artist, y.Artist, StringComparison.OrdinalIgnoreCase));

        // Shelves.
        var recent = new List<LocalShelfGroup>(3);
        int recentMonth = 0;
        foreach (var when in (ReadOnlySpan<LocalAddedWhen>)[LocalAddedWhen.Today, LocalAddedWhen.ThisWeek, LocalAddedWhen.ThisMonth])
        {
            var g = cards.FindAll(c => c.When == when);
            recentMonth += g.Count;
            if (g.Count > 0) recent.Add(new LocalShelfGroup(when, g.Count > ShelfMax ? g.GetRange(0, ShelfMax) : g));
        }
        // A library nothing arrived in this month still shows what came in last, as one "Earlier" group.
        if (recent.Count == 0 && cards.Count > 0) recent.Add(new LocalShelfGroup(LocalAddedWhen.Earlier, cards.Count > ShelfMax ? cards.GetRange(0, ShelfMax) : cards));
        var losslessCards = cards.FindAll(static c => c.Lossless);
        int losslessCount = losslessCards.Count;
        if (losslessCards.Count > ShelfMax) losslessCards = losslessCards.GetRange(0, ShelfMax);
        var decades = new SortedDictionary<int, List<LocalAlbumCard>>();
        foreach (var c in cards)
        {
            if (c.Year <= 0) continue;
            int d = c.Year / 10 * 10;
            if (!decades.TryGetValue(d, out var l)) decades[d] = l = new List<LocalAlbumCard>();
            l.Add(c);
        }
        var decadeList = new List<LocalDecade>(decades.Count);
        foreach (var (d, l) in decades) decadeList.Add(new LocalDecade(d, l.Count, CoversOf(l, 3)));

        // Artists.
        var artistCards = new List<LocalArtistCard>(artists.Count);
        foreach (var (k, a) in artists)
        {
            string? cover = null;
            foreach (string ak in a.Albums) if (coverOf.GetValueOrDefault(ak) is { } c) { cover = c; break; }
            artistCards.Add(new LocalArtistCard(k, a.Name, a.Albums.Count, a.Tracks, cover));
        }
        artistCards.Sort(static (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

        // Folder nodes.
        var outNodes = new Dictionary<string, LocalFolderNode>(nodes.Count, StringComparer.Ordinal);
        foreach (var n in nodes.Values)
        {
            n.Children.Sort(static (x, y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase));
            n.Files.Sort(static (a, b) =>
            {
                int c = a.Tags.DiscNumber.CompareTo(b.Tags.DiscNumber);
                if (c == 0) c = a.Tags.TrackNumber.CompareTo(b.Tags.TrackNumber);
                return c != 0 ? c : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
            });
            var files = new List<LocalSongItem>(n.Files.Count);
            int num = 0;
            foreach (var e in n.Files)
                files.Add(new LocalSongItem(e.LocalId, ++num, e.MatchTitle, e.Tags.Album, Path.GetFileName(e.RelativePath), QualityOf(e.Format, e.Tags.BitrateKbps), e.Tags.DurationMs,
                    e.Playable && e.State == LocalState.Present));
            var mosaic = new List<string>(4);
            CollectCovers(n, nodes, coverOf, mosaic);
            string quality = n.Files.Count > 0 && !n.Mixed ? (IsLossless(n.Format) ? QualityOf(n.Format, 0) : "") : "";
            outNodes[n.Key] = new LocalFolderNode(n.Key, n.Name, n.Parent, n.Songs, n.Duration, quality, n.Image, mosaic, n.Children.ToArray(), files, n.DurationHere);
        }
        var rootKeys = new List<string>(folders.Count);
        var chips = new List<LocalFolderChip>(folders.Count);
        foreach (string f in folders)
        {
            int rootId = 0;
            foreach (var r in roots) if (string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase)) { rootId = r.RootId; break; }
            var state = LocalLibrary.StateOf(f) switch
            {
                LocalRootState.Scanning or LocalRootState.Waking => LocalChipState.Scanning,
                LocalRootState.Offline => LocalChipState.Offline,
                LocalRootState.SignInNeeded => LocalChipState.NoAccess,
                LocalRootState.Unknown => LocalChipState.Waiting,
                _ => LocalChipState.Ok,
            };
            chips.Add(new LocalFolderChip(f, f, state, rootId > 0 ? perRootSongs.GetValueOrDefault(rootId) : 0));
            if (rootId > 0 && rootKeyOf.TryGetValue(rootId, out string? rk)) rootKeys.Add(rk);
        }

        int dupCount = 0;
        foreach (int n in dupes.Values) if (n > 1) dupCount += n;
        int withCover = 0;
        foreach (var c in cards) if (c.Cover is not null) withCover++;
        var insights = new LocalInsights(Shares(formats), Shares(bitrates), lossless, lossy, withCover, cards.Count, untagged, noAlbumArtist, noTrack, dupCount,
            Shares(unplayable), missing, cloud, chips);

        var mosaicCovers = new List<string>(18);
        foreach (var c in cards) { if (c.Cover is { } u) mosaicCovers.Add(u); if (mosaicCovers.Count == 18) break; }

        return new LocalCollectionView
        {
            Version = Interlocked.Increment(ref s_version),
            Phase = phase,
            Songs = songs,
            AlbumCount = cards.Count,
            ArtistCount = artistCards.Count,
            DurationMs = duration,
            LosslessPct = lossless + lossy > 0 ? (int)Math.Round(100.0 * lossless / (lossless + lossy)) : 0,
            Bytes = bytes,
            Chips = chips,
            Mosaic = mosaicCovers,
            Recent = recent,
            RecentThisMonth = recentMonth,
            Lossless = losslessCards,
            LosslessSongs = lossless,
            Decades = decadeList,
            Albums = cards,
            AlbumsByArtist = byArtist,
            AlbumsByTitle = byTitle,
            AlbumsByYear = byYear,
            Artists = artistCards,
            Folders = outNodes,
            RootKeys = rootKeys,
            Insights = insights,
        };
    }

    /// <summary>How many albums a Home shelf holds (it scrolls sideways; "See all" opens the wall).</summary>
    public const int ShelfMax = 24;

    static string DirOf(string rel)
    {
        int i = rel.LastIndexOf('\\');
        return i < 0 ? "" : rel[..i];
    }

    /// <summary>The album's folder: the file's, or its parent when the file sits in a disc folder (CD1, Disc 2, Disk 3; §3.6a's rule).</summary>
    static string AlbumFolderOf(string rel)
    {
        string dir = DirOf(rel);
        string leaf = dir[(dir.LastIndexOf('\\') + 1)..];
        bool disc = (leaf.StartsWith("CD", StringComparison.OrdinalIgnoreCase) && leaf.Length > 2 && char.IsAsciiDigit(leaf[^1]))
                    || ((leaf.StartsWith("Disc", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("Disk", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(leaf[^1]));
        return disc && dir.Contains('\\') ? DirOf(dir) : dir;
    }

    static string KeyOf(int root, string rel) => root.ToString(CultureInfo.InvariantCulture) + "|" + rel;

    static FolderAcc NodeOf(Dictionary<string, FolderAcc> nodes, Dictionary<int, string> rootKeyOf, Dictionary<int, string> rootPath, int root, string rel)
    {
        string key = KeyOf(root, rel);
        if (nodes.TryGetValue(key, out var n)) return n;
        string name = rel.Length == 0 ? (rootPath.TryGetValue(root, out string? p) ? p : "?") : rel[(rel.LastIndexOf('\\') + 1)..];
        n = new FolderAcc { Key = key, Name = name, Root = root, Rel = rel };
        nodes[key] = n;
        if (rel.Length == 0) rootKeyOf[root] = key;
        else
        {
            var parent = NodeOf(nodes, rootKeyOf, rootPath, root, DirOf(rel));
            n.Parent = parent.Key;
            parent.Children.Add(key);
        }
        return n;
    }

    static void CollectCovers(FolderAcc n, Dictionary<string, FolderAcc> nodes, Dictionary<string, string?> coverOf, List<string> into)
    {
        foreach (string a in n.AlbumKeys)
        {
            if (into.Count == 4) return;
            if (coverOf.GetValueOrDefault(a) is { } c && !into.Contains(c)) into.Add(c);
        }
        foreach (string k in n.Children)
        {
            if (into.Count == 4) return;
            if (nodes[k].Image is { } img && !into.Contains(img)) into.Add(img);
            else CollectCovers(nodes[k], nodes, coverOf, into);
        }
    }

    static string[] CoversOf(List<LocalAlbumCard> l, int max)
    {
        var o = new List<string>(max);
        foreach (var c in l) { if (c.Cover is { } u) o.Add(u); if (o.Count == max) break; }
        return o.ToArray();
    }

    static LocalShare[] Shares(Dictionary<string, int> d)
    {
        var a = new LocalShare[d.Count];
        int i = 0;
        foreach (var (k, v) in d) a[i++] = new LocalShare(k, v);
        Array.Sort(a, static (x, y) => y.Count != x.Count ? y.Count.CompareTo(x.Count) : string.CompareOrdinal(x.Label, y.Label));
        return a;
    }
}
