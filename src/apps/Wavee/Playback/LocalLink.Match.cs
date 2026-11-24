// ── Playback/LocalLink.Match.cs ─────────────────────────────────────────────────────────────────────────────────────
// the matcher: one group at a time, Spotify's search and album reads asked through the entity layer, the answers scored by the ladder
//
// Role: SHELL (UI thread: a pump that runs one small step every 60 ms while matching is on; nothing per frame)
// Spec: 240-spotify-linking-concept.md §5 F-A steps 3-5, §6.3 (album first); board frame 28 (the chip, the toast), 37 (the ladder)
//
// THE GROUPS. The index is folded into album groups (the On this PC page's key: album artist or first artist, album) and song groups (a file
// with no album tag, "song:<LocalId>"). A group that was decided (accepted, Not on Spotify) is not asked again; a missing or unplayable file is
// left out of its group. An untagged file is matched by its file name's split ("Nickelback - Savin' Me": "Savin' Me" by "Nickelback").
//
// ONE GROUP (the steps; each waits for its answer, at most 10 s, then goes on with what landed):
//   album  search Albums for "<album> <artist>" (again for "<album>" alone when nothing of that name came back) · read the track lists of the
//          (at most three) albums of that name · ask their tracks' ISRCs · score each album (LocalLink.Score.cs) · keep them best first.
//   song   search Tracks for "<artist> <title>" (again for "<title>" alone) · ask the first six tracks' ISRCs · score each.
// These are the same asks the search page and an album page make (pathfinder `searchAlbums` / `searchTracks`, extended metadata for the album
// and the tracks), so `--fake` answers them from its catalogue and the planner batches and caches them as it does for a page.
//
// THE END. When the last group is scored the chip leaves "Matching · 12 of 48 albums" and one toast says what was found, with Review.
// "Accept exact matches automatically" accepts each Exact group as it is scored.

using FluentGpu.Localization;

namespace Wavee;

public static partial class LocalLink
{
    /// <summary>How long one step waits for Spotify before it goes on with what landed.</summary>
    const long StepWaitMs = 10_000;
    const int AlbumCandidates = 3, SongCandidates = 6;

    sealed class Job(string key, bool album, string title, string artist, string albumArtist, int year, string? cover, string detail, List<FileFacts> files)
    {
        public readonly string Key = key, Title = title, Artist = artist, AlbumArtist = albumArtist, Detail = detail;
        public readonly bool Album = album;
        /// <summary>What the review calls the group: the album's name, or a song's file name when it has no title tag.</summary>
        public string Name = title;
        public readonly int Year = year;
        public readonly string? Cover = cover;
        public readonly List<FileFacts> Files = files;
        public int Stage;
        public bool Narrowed;
        /// <summary>The user's own words (the review's search box): asked as they are, every hit a candidate.</summary>
        public string? Query;
        public long Since;
        public Search Row;
        public List<int> Albums = [];
        public List<int> Tracks = [];
    }

    static readonly Queue<Job> s_queue = new();
    static Job? s_job;
    static Timer? s_pump;
    static bool s_running, s_stepQueued;
    static int s_done, s_total, s_skipped;

    /// <summary>Matching runs (the chip's "Matching · 12 of 48 albums").</summary>
    public static bool Matching => s_running;

    /// <summary>Groups done and the total, while matching.</summary>
    public static (int Done, int Total) Progress => (s_done, s_total);

    /// <summary>Fold the index into the groups that still need an answer and start the pump. UI thread.</summary>
    static void StartMatching()
    {
        StopMatching();
        s_now.Clear();
        foreach (Job j in GroupsToMatch()) s_queue.Enqueue(j);
        s_total = s_queue.Count;
        s_done = s_skipped = 0;
        s_running = s_total > 0;
        Publish();
        if (!s_running) return;
        s_pump = new Timer(static _ =>
        {
            if (Volatile.Read(ref s_stepQueued)) return;
            Volatile.Write(ref s_stepQueued, true);
            Playback.ToUi(static () => { s_stepQueued = false; Step(sync: false); });
        }, null, 0, 60);
    }

    static void StopMatching()
    {
        s_pump?.Dispose();
        s_pump = null;
        s_queue.Clear();
        s_job = null;
        s_running = false;
    }

    /// <summary>Match the next groups now, without waiting (the inspector's fixtures, whose catalogue answers at once): until
    /// <paramref name="upTo"/> groups are done (all when negative). UI thread.</summary>
    internal static void RunNow(int upTo = -1)
    {
        s_pump?.Dispose();
        s_pump = null;
        while (s_running && (upTo < 0 || s_done < upTo)) Step(sync: true);
    }

    /// <summary>The groups the index holds that no decision covers yet, albums first.</summary>
    static List<Job> GroupsToMatch() => GroupsFrom(static _ => true, keepDecided: false);

    static List<Job> GroupsFrom(Func<LocalEntry, bool> keep, bool keepDecided)
    {
        var albums = new Dictionary<string, List<LocalEntry>>(StringComparer.Ordinal);
        var songs = new List<LocalEntry>();
        foreach (LocalEntry e in LocalLibrary.Index.Snapshot())
        {
            if (e.Tags.Kind != LocalKind.Audio || e.State == LocalState.Missing || !e.Playable || !keep(e)) continue;
            string key = GroupKeyOf(e);
            if (!keepDecided && s_decided.TryGetValue(key, out Decided? d) && d.Decision is LinkDecision.Accepted or LinkDecision.NotOnSpotify) continue;
            if (e.Tags.Album.Length == 0) { songs.Add(e); continue; }
            if (!albums.TryGetValue(key, out var list)) albums[key] = list = [];
            list.Add(e);
        }
        var jobs = new List<Job>(albums.Count + songs.Count);
        foreach (var (key, list) in albums)
        {
            list.Sort(static (a, b) => a.Tags.DiscNumber != b.Tags.DiscNumber ? a.Tags.DiscNumber.CompareTo(b.Tags.DiscNumber)
                : a.Tags.TrackNumber != b.Tags.TrackNumber ? a.Tags.TrackNumber.CompareTo(b.Tags.TrackNumber) : string.CompareOrdinal(a.MatchTitle, b.MatchTitle));
            LocalTagSet t = list[0].Tags;
            string artist = LocalCollection.ArtistOf(list[0]);
            int year = 0;
            foreach (LocalEntry e in list) year = Math.Max(year, e.Tags.Year);
            string quality = LocalCollection.QualityOf(list[0].Format, list[0].Tags.BitrateKbps, list[0].Tags.SampleRate, list[0].Tags.BitsPerSample);
            string detail = Strings.LocalFiles.Link.AlbumDetail(list.Count, list.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture))
                            + (quality.Length > 0 ? " · " + quality : "");
            jobs.Add(new Job(key, true, t.Album, artist, t.AlbumArtist, year, LocalArt.AlbumCoverUri(artist, t.Album) ?? LocalCollectionFixture.CoverOf(artist, t.Album) ?? FixtureCoverOf(artist, t.Album), detail,
                list.ConvertAll(FactsOf)));
        }
        foreach (LocalEntry e in songs)
        {
            FileFacts f = FactsOf(e);
            string quality = LocalCollection.QualityOf(e.Format, e.Tags.BitrateKbps, e.Tags.SampleRate, e.Tags.BitsPerSample);
            string folder = Path.GetFileName(Path.GetDirectoryName(e.RelativePath) ?? "");
            string detail = Loc.Get(Strings.LocalFiles.Link.SingleSong) + " · " + Clock(e.Tags.DurationMs) + (quality.Length > 0 ? " · " + quality : "")
                            + (folder.Length > 0 ? " · " + Strings.LocalFiles.Link.InFolder(folder) : "");
            string file = Path.GetFileName(e.RelativePath);
            jobs.Add(new Job(SongKey(e.LocalId), false, f.Title, f.Artist, "", e.Tags.Year, LocalArt.CoverUri(e.LocalId), detail, [f]) { Name = e.Tags.Title.Length > 0 ? f.Title : file });
        }
        return jobs;
    }

    static FileFacts FactsOf(LocalEntry e)
    {
        // The user's names when the file was renamed ("Savin' Me (hardstyle remix)" searches for the remix), else the display split.
        (string title, string artist) = LocalNames.Display(e.Tags, e.MatchTitle);
        if (RenameOf(e.LocalId) is { } named)
        {
            if (named.Title.Length > 0) title = named.Title;
            if (named.Artist.Length > 0) artist = named.Artist;
        }
        return new FileFacts(e.LocalId, title, artist, e.Tags.Album, e.Tags.DurationMs, e.Tags.Isrc, e.Tags.DiscNumber, e.Tags.TrackNumber, e.Tags.Year);
    }

    // ── the pump ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One step of the matching queue. <paramref name="sync"/>: an ask that has not answered counts as answered (empty).</summary>
    static void Step(bool sync)
    {
        if (!s_running) return;
        if (s_job is null)
        {
            if (!s_queue.TryDequeue(out s_job)) { Finish(); return; }
            // "Find on Spotify…" matched it ahead of the queue, or the user decided it meanwhile: it is not asked twice.
            if (s_now.Contains(s_job.Key) || s_decided.TryGetValue(s_job.Key, out Decided? d) && d.Decision is LinkDecision.Accepted or LinkDecision.NotOnSpotify)
            {
                s_job = null;
                s_done++;
                s_skipped++;
                return;
            }
            s_job.Since = Environment.TickCount64;
        }
        if (!Advance(s_job, sync)) return;
        s_job = null;
        s_done++;
        Publish();
    }

    /// <summary>Move one group on by as many steps as have their answers; true once it is scored.</summary>
    static bool Advance(Job j, bool sync)
    {
        var scope = Entities.Current;
        while (true)
        {
            bool late = sync || Environment.TickCount64 - j.Since > StepWaitMs;
            switch (j.Stage)
            {
                case 0:
                    string query = j.Query ?? (!j.Narrowed && j.Artist.Length > 0 ? (j.Album ? j.Title + " " + j.Artist : j.Artist + " " + j.Title) : j.Title);
                    var facet = j.Album ? SearchFacet.Albums : SearchFacet.Tracks;
                    j.Row = Entities.Search(query.AsSpan(), facet);
                    Entities.Ensure(j.Row, SearchFields.Results);
                    Note(facet, query);
                    Next(j, 1);
                    continue;
                case 1:
                    if (!Answered(scope.Searches, j.Row.Slot, (uint)SearchFields.Results, late)) return false;
                    if (j.Album) PickAlbums(j); else PickTracks(j);
                    bool named = j.Album ? j.Albums.Count > 0 && SameAlbum(j.Title, new Album(j.Albums[0]).Title)
                                         : j.Tracks.Count > 0 && SameTitle(j.Title, new Track(j.Tracks[0]).Title);
                    if (!named && !j.Narrowed && j.Query is null && j.Artist.Length > 0) { j.Narrowed = true; Next(j, 0); continue; }
                    if (j.Album)
                    {
                        foreach (int a in j.Albums)
                        {
                            Entities.Ensure(new Album(a), AlbumFields.Card);
                            Entities.EnsureEdge(FetchEdge.AlbumTracks, a);
                            Note("album " + new Album(a).Uri.Text + " · extended metadata kind 9 (the album, its discs and track list with lengths)");
                        }
                        Next(j, 2);
                    }
                    else
                    {
                        EnsureTracks(j.Tracks);
                        Next(j, 3);
                    }
                    continue;
                case 2:
                    foreach (int a in j.Albums)
                        if (scope.Edges.AlbumTracks.State(a) is EdgeState.Unknown or EdgeState.Partial && !late) return false;
                    j.Tracks.Clear();
                    foreach (int a in j.Albums) foreach (int t in new Album(a).TrackSlots) j.Tracks.Add(t);
                    EnsureTracks(j.Tracks);
                    Next(j, 3);
                    continue;
                default:
                    foreach (int t in j.Tracks) if (!Answered(scope.Tracks, t, (uint)(TrackFields.Identity | TrackFields.Isrc), late)) return false;
                    Score(j);
                    return true;
            }
        }
    }

    // ── what is asked (the inspector's dump of the requests matching sends to Spotify) ─────────────────────────────────

    static readonly List<string> s_requests = [];
    const int RequestsKept = 400;

    /// <summary>The asks of the last runs, oldest first: each search with its pathfinder body (the bytes `Spotify.Api.Search` posts), each album
    /// and track read. Kept for the inspector (`state kind=spotify-match` lists them); nothing else reads them.</summary>
    internal static IReadOnlyList<string> Requests => s_requests;

    static void Note(SearchFacet facet, string query)
    {
        var wire = Spotify.Api.FacetOf(facet);
        string body = System.Text.Encoding.UTF8.GetString(Spotify.Api.SearchBody(wire, query, 0, Spotify.Api.SearchPageSize(wire)));
        Note("search " + wire + " \"" + query + "\" · POST pathfinder " + body);
    }

    static void Note(string line)
    {
        if (s_requests.Count >= RequestsKept) s_requests.RemoveAt(0);
        s_requests.Add(line);
    }

    static bool IsCatalogue(string uri, string prefix) => uri.StartsWith(prefix, StringComparison.Ordinal);

    static void Next(Job j, int stage)
    {
        j.Stage = stage;
        j.Since = Environment.TickCount64;
    }

    static bool Answered(Table table, int slot, uint groups, bool late)
        => late || table.StateOf(slot, groups) is GroupState.Known or GroupState.Absent or GroupState.Failed;

    static void EnsureTracks(List<int> tracks)
    {
        if (tracks.Count == 0) return;
        Note("tracks ×" + tracks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · extended metadata kind 10 (identity and ISRC), batched by the planner");
        var rows = new Track[tracks.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = new Track(tracks[i]);
        Entities.Ensure(rows, TrackFields.Identity | TrackFields.Isrc);
    }

    /// <summary>The albums a search answered, those of the group's name first, at most <see cref="AlbumCandidates"/>.</summary>
    static void PickAlbums(Job j)
    {
        j.Albums.Clear();
        var hits = j.Row.ResultSlots;
        var others = new List<int>();
        for (int i = 0; i < hits.Length && i < 10; i++)
        {
            var hit = j.Row.ResultRef(i);
            if (hit.Kind != EntityKind.Album || hit.Slot == Table.None || !IsCatalogue(new Album(hit.Slot).Uri.Text, "spotify:album:")) continue;
            if (j.Query is not null || SameAlbum(j.Title, new Album(hit.Slot).Title)) { if (j.Albums.Count < AlbumCandidates) j.Albums.Add(hit.Slot); }
            else others.Add(hit.Slot);
        }
        // Nothing of that name: the first two still go to the review as a Check ("Pick the right album").
        if (j.Albums.Count == 0) for (int i = 0; i < others.Count && i < 2; i++) j.Albums.Add(others[i]);
    }

    /// <summary>The tracks a search answered, those of the song's title first, at most <see cref="SongCandidates"/>.</summary>
    static void PickTracks(Job j)
    {
        j.Tracks.Clear();
        var named = new List<int>();
        var others = new List<int>();
        int n = j.Row.ResultSlots.Length;
        for (int i = 0; i < n && i < 20; i++)
        {
            var hit = j.Row.ResultRef(i);
            // Spotify's own tracks only: a search also answers with the user's local rows (their titles match by construction).
            if (hit.Kind != EntityKind.Track || hit.Slot == Table.None || !IsCatalogue(new Track(hit.Slot).Uri.Text, "spotify:track:")) continue;
            (j.Query is not null || SameTitle(j.Title, new Track(hit.Slot).Title) ? named : others).Add(hit.Slot);
        }
        foreach (int t in named) if (j.Tracks.Count < SongCandidates) j.Tracks.Add(t);
        foreach (int t in others) if (j.Tracks.Count < 3) j.Tracks.Add(t);
    }

    /// <summary>Score the group's candidates, keep them best first (rejected ones left out), and accept an Exact one when the user chose that.</summary>
    static void Score(Job j)
    {
        Decided d = DecidedOf(j.Key);
        var list = new List<LinkCandidate>();
        if (j.Album)
        {
            foreach (int a in j.Albums)
            {
                LinkAlbum album = AlbumOf(new Album(a));
                if (d.Rejected.Contains(album.Uri)) continue;
                list.Add(ScoreAlbum(j.Files, j.Title, j.Artist, album));
            }
        }
        else
        {
            foreach (int t in j.Tracks)
            {
                LinkTrack track = TrackOf(new Track(t), default);
                if (d.Rejected.Contains(track.Uri)) continue;
                list.Add(ScoreSong(j.Files[0], track));
            }
        }
        Rank(list, song: !j.Album);
        var files = j.Files.ConvertAll(static f => f.Id);
        LinkField[] fields = list.Count > 0 ? FieldsOf(j.Files, j.Title, j.Artist, j.AlbumArtist, j.Year, j.Cover, list[0], j.Album) : [];
        // A group the user decided keeps its decision ("Find on Spotify…" on a linked song scores it again): the chosen candidate leads.
        if (d.Decision == LinkDecision.Accepted && list.FindIndex(c => c.Uri == d.Chosen) is > 0 and var at)
        {
            LinkCandidate chosen = list[at];
            list.RemoveAt(at);
            list.Insert(0, chosen);
        }
        // An untagged song is called by its file name, which already names the artist its split found.
        var group = new LinkGroup(j.Key, j.Album, j.Name, j.Name != j.Title && !j.Album ? "" : j.Artist, j.Detail, j.Cover, files, list, d.Decision,
            d.SpotifyWins, fields);
        s_groups[j.Key] = group;
        s_rank.TryAdd(j.Key, group.Decision == LinkDecision.NotThis ? (int)LinkConfidence.Check : (int)group.Confidence);
        if (AutoExact && group.Decision == LinkDecision.None && group.Best is { Confidence: LinkConfidence.Exact } best)
        {
            Apply(group, best, publish: false);
            Save();
        }
    }

    /// <summary>"Not here? Search Spotify": candidates for one group from the user's own words (the review's search box), scored as the
    /// matcher scores them; they replace the group's list as the search lands (the review re-reads the view). UI thread.</summary>
    public static void SearchFor(string key, string text)
    {
        text = text.Trim();
        if (text.Length == 0 || JobFor(key) is not { } job) return;
        job.Query = text;
        job.Since = Environment.TickCount64;
        s_searches[key] = job;
        PumpSearch(key, job, 0);
    }

    static readonly Dictionary<string, Job> s_searches = new(StringComparer.Ordinal);

    /// <summary>Groups "Find on Spotify…" matched ahead of the background queue this run (the queue skips them).</summary>
    static readonly HashSet<string> s_now = new(StringComparer.Ordinal);

    /// <summary>"Find on Spotify…": match one group now, on its own pump (as the review's search does), unless an answer is in or on its way.
    /// The review shows "Looking for it on Spotify…" until it lands. UI thread.</summary>
    static void MatchNow(string key)
    {
        if (s_groups.ContainsKey(key) || s_searches.ContainsKey(key) || JobFor(key) is not { } job) return;
        job.Since = Environment.TickCount64;
        s_searches[key] = job;
        s_now.Add(key);
        Publish();
        PumpSearch(key, job, 0);
    }

    /// <summary>A search's own pump: the group's steps, again every 100 ms until scored (or a newer search for the group replaced it).</summary>
    static void PumpSearch(string key, Job job, int tries)
    {
        if (!s_searches.TryGetValue(key, out Job? current) || !ReferenceEquals(current, job)) return;
        if (Advance(job, sync: false))
        {
            s_searches.Remove(key);
            Publish();
            return;
        }
        if (tries > 300) { s_searches.Remove(key); Publish(); return; }
        Task.Delay(100).ContinueWith(_ => Playback.ToUi(() => PumpSearch(key, job, tries + 1)), TaskScheduler.Default);
    }

    /// <summary>One group's job, rebuilt from the index (the review's search). Null when its files are gone.</summary>
    static Job? JobFor(string key)
    {
        foreach (Job j in GroupsFrom(e => GroupKeyOf(e) == key, keepDecided: true)) if (j.Key == key) return j;
        return null;
    }

    static void Finish()
    {
        s_pump?.Dispose();
        s_pump = null;
        bool had = s_running;
        s_running = false;
        Publish();
        // Nothing left to say when "Find on Spotify…" already answered every group of the run (the user is on its candidates).
        if (!had || s_total == 0 || s_quiet || s_skipped == s_total) return;
        var v = s_view.Peek();
        if (v.Pending == 0 && v.Accepted == 0) return;
        Notify.Say(Strings.LocalFiles.Link.Finished(Count(v.Exact + v.Accepted), Count(v.Strong), Count(v.Likely + v.Check)),
            title: Loc.Get(Strings.LocalFiles.Link.FinishedTitle), actionLabel: Loc.Get(Strings.LocalFiles.Link.Review),
            onAction: static () => OpenReview(), dedupeKey: "local.link.finished", durationMs: 12_000f);
    }

    // ── reading an answer ────────────────────────────────────────────────────────────────────────────────────────────

    static LinkAlbum AlbumOf(Album a)
    {
        var tracks = new List<LinkTrack>(a.TrackSlots.Length);
        var slots = a.TrackSlots;
        var edges = Entities.Current.Edges.AlbumTracks.Payload(a.Slot);
        string cover = Controls.ArtUrl(a.ImageId) ?? "";
        for (int i = 0; i < slots.Length; i++)
        {
            AlbumTrackEdge e = i < edges.Length ? edges[i] : default;
            tracks.Add(TrackOf(new Track(slots[i]), e, a, cover));
        }
        return new LinkAlbum(a.Uri.Text, a.Title, ArtistOf(a), a.Year, cover.Length > 0 ? cover : null, tracks);
    }

    static LinkTrack TrackOf(Track t, AlbumTrackEdge e, Album album = default, string? albumCover = null)
    {
        Album a = album.IsValid ? album : t.Album;
        string? cover = albumCover is { Length: > 0 } ? albumCover : Controls.ArtUrl(t.ImageId) ?? (a.IsValid ? Controls.ArtUrl(a.ImageId) : null);
        return new LinkTrack(t.Uri.Text, t.Title, Entities.Strings.Resolve(t.ArtistLineId), a.IsValid ? a.Title : "", a.IsValid ? a.Uri.Text : "", t.DurationMs,
            Entities.Strings.Resolve(t.IsrcId), e.Disc, e.Number, cover, t.Year > 0 ? t.Year : a.IsValid ? a.Year : 0);
    }

    static string ArtistOf(Album a)
    {
        var artists = a.ArtistSlots;
        return artists.Length > 0 ? new Artist(artists[0]).Name : "";
    }
}
