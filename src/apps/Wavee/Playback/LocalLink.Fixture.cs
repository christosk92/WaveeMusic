// ── Playback/LocalLink.Fixture.cs ───────────────────────────────────────────────────────────────────────────────────
// the inspector's "Your files and Spotify" fixture: files that the fake catalogue can answer, at every rung of the ladder (#240 phase 1)
//
// Role: DIAGNOSTICS (UI thread, inspector only; in memory: no disk, no setting, no network)
// Spec: 240-spotify-linking-concept.md §5 F-A, F-B; board frames 28-30
//
// `state kind=spotify-match value=…` seeds P1b's small index (`LocalLibrary.InspectFixture("ready")`) and, for `offer`, the On this PC collection
// on top; every other value adds albums and songs built from the `--fake` catalogue's own albums and tracks, so the real matcher, asking the
// real search and album reads, finds them:
//   Exact   two albums whose files carry the catalogue tracks' ISRCs and lengths;
//   Strong  an album with the same track list, every length 0.7 s off, no ISRC, its tags left thin ("low tide letters"-style: a lower-case
//           album name, no album artist, no year, no track numbers): "6 fields improve";
//   Likely  an album with half its lengths 9 s off;
//   Check   an album whose songs carry other titles (the name and artist agree, nothing else), an untagged "untitled demo 3.mp3" nothing on
//           the catalogue looks like, and the index's own voice memos;
//   a song  an untagged "Artist - Title.mp3" (the name split; title, artist and length agree: Likely).
// Values: off · offer (matching off, the offer on Home) · matching (half the groups done, the chip counting) · review (done, the review open)
// · focus (the review opened on the untagged song, as "Spotify match…" opens it) · accepted (Exact and Strong accepted, the review closed).

using System.Globalization;

namespace Wavee;

public static partial class LocalLink
{
    static readonly Dictionary<string, string> s_fixtureCovers = new(StringComparer.Ordinal);

    /// <summary>A fixture album's own cover (a file uri the fake decoder paints as a gradient), else null.</summary>
    static string? FixtureCoverOf(string artist, string album) => s_fixtureCovers.TryGetValue(LocalCollection.AlbumKey(artist, album), out string? c) ? c : null;

    /// <summary>The words the inspector accepts.</summary>
    public const string FixtureValues = "off|offer|matching|review|focus|accepted|versions|versions-spotify|link";

    /// <summary>The versions fixture's renamed copy (the inspector's `dialog kind=rename` opens on it); 0 outside it.</summary>
    internal static long FixtureCopy { get; private set; }

    /// <summary>Seed the fixture (see the file header). Returns "" or why it could not.</summary>
    internal static string InspectFixture(string value)
    {
        StopMatching();
        s_memoryOnly = true;
        s_loaded = true;
        s_path = null;
        s_rank.Clear(); s_decided.Clear(); s_links.Clear(); s_renames.Clear(); s_defaults.Clear(); s_linkGroup.Clear(); s_albumFaces.Clear(); s_groups.Clear(); s_searches.Clear(); s_now.Clear(); s_fixtureCovers.Clear();
        s_review.Value = null;
        s_requests.Clear();
        s_enabled.Value = false;
        s_offerShown.Value = true;
        s_quiet = true;
        FixtureCopy = 0;
        // `link`: the index as it is (the real start-up's, `music-folders value=startup`): every file linked to the fake catalogue's song of its
        // title (the one closest in length), the first whose name says "remix" set to play by default. Nothing is reset.
        if (value == "link") return LinkIndexed();
        LocalLibrary.InspectFixture("ready");
        if (value is "versions" or "versions-spotify") return SeedVersions(value == "versions");
        if (value is "off" or "offer")
        {
            LocalCollectionFixture.Fill("ready");
            Publish();
            return "";
        }
        string? song = SeedLinkable();
        if (song is null) return "the fake catalogue has no albums to build the fixture from (run under --fake)";
        LocalLibrary.InspectRestate();
        s_enabled.Value = true;
        s_offerShown.Value = false;
        StartMatching();
        if (value == "matching") { RunNow(Math.Max(1, s_total / 2)); Publish(); return ""; }
        RunNow();
        switch (value)
        {
            case "review": s_review.Value = ""; break;
            case "focus": s_review.Value = song; break;
            case "accepted":
                foreach (LinkGroup g in s_groups.Values.ToArray())
                    if (g.Best is { Confidence: LinkConfidence.Exact or LinkConfidence.Strong } best) Apply(g, best, publish: false);
                Publish();
                break;
        }
        return "";
    }

    static bool s_quiet;

    static string LinkIndexed()
    {
        var tracks = Entities.Current.Tracks;
        int linked = 0;
        foreach (LocalEntry e in LocalLibrary.Index.Snapshot())
        {
            FileFacts f = FactsOf(e);
            Track best = default;
            for (int slot = Table.None + 1; slot < tracks.Count; slot++)
            {
                var t = new Track(slot);
                if (!t.IsValid || t.IsLocal || !t.Uri.Text.StartsWith("spotify:track:", StringComparison.Ordinal) || !SameTitle(f.Title.Replace(" (hardstyle remix)", ""), t.Title)) continue;
                if (!best.IsValid || Math.Abs(t.DurationMs - f.DurationMs) < Math.Abs(best.DurationMs - f.DurationMs)) best = t;
            }
            if (!best.IsValid) continue;
            var track = TrackOf(best, default);
            string key = SongKey(e.LocalId);
            s_decided[key] = new Decided(LinkDecision.Accepted, track.Uri, true, new HashSet<string>(StringComparer.Ordinal));
            s_links[e.LocalId] = track;
            s_linkGroup[e.LocalId] = key;
            if (e.MatchTitle.Contains("remix", StringComparison.OrdinalIgnoreCase)) { s_defaults[track.Uri] = e.LocalId; FixtureCopy = e.LocalId; }
            linked++;
        }
        if (linked == 0) return "no indexed file has a song of its title in the fake catalogue (run music-folders value=startup under --fake first)";
        s_enabled.Value = true;
        s_offerShown.Value = false;
        Publish();
        return "";
    }

    /// <summary>Phase 2: the owner's file and a copy of it, both linked to the fake "Savin' Me" (3:39, All the Right Reasons), the copy renamed
    /// "Savin' Me (hardstyle remix)" and, with <paramref name="withDefault"/>, set to play by default.</summary>
    static string SeedVersions(bool withDefault)
    {
        var song = Entities.Track(EntityUri.Parse("spotify:track:nb0n3"));
        if (!song.IsValid) return "the fake catalogue has no Savin' Me (run under --fake)";
        long now = DateTime.UtcNow.Ticks;
        var batch = new LocalScanBatch(1, "Downloads") { Folder = new LocalFolder(1, "Downloads", 6998, 0x6667, [], true) };
        long original = 6100, copy = 6101;
        batch.Upserts.Add(Untagged(original, @"Downloads\Nickelback - Savin' Me.mp3", "Nickelback - Savin' Me", 252_000, now));
        batch.Upserts.Add(Untagged(copy, @"Downloads\Nickelback - Savin' Me (copy).mp3", "Nickelback - Savin' Me (copy)", 252_400, now));
        LocalLibrary.Index.Apply(batch);
        LocalLibrary.InspectRestate();
        var track = TrackOf(song, new AlbumTrackEdge(1, 4));
        foreach (long id in new[] { original, copy })
        {
            string key = SongKey(id);
            s_decided[key] = new Decided(LinkDecision.Accepted, track.Uri, true, new HashSet<string>(StringComparer.Ordinal));
            s_links[id] = track;
            s_linkGroup[id] = key;
        }
        s_renames[copy] = new LocalRename("Savin' Me (hardstyle remix)", "", "");
        if (withDefault) s_defaults[track.Uri] = copy;
        FixtureCopy = copy;
        s_enabled.Value = true;
        s_offerShown.Value = false;
        Publish();
        return "";

        static LocalEntry Untagged(long id, string rel, string name, int ms, long now)
            => new(id, 1, rel, 0, (UInt128)(9000 + id), 7_000_000, now, now, 0x20, Spotify.Audio.Format.Mp3,
                new LocalTagSet(LocalKind.Audio, "", [], "", "", 0, 0, 0, ms, false, 320, LocalCover.None, 0, 0, true, 44_100, 0, 2), name, LocalState.Present, now, true, "", now);
    }

    /// <summary>Add the linkable albums and songs to the fixture index; the untagged song's group key, or null without a fake catalogue.</summary>
    static string? SeedLinkable()
    {
        var albums = Entities.Current.Albums;
        var picked = new List<Album>(5);
        for (int slot = Table.None + 1; slot < albums.Count && picked.Count < 5; slot++)
        {
            var a = new Album(slot);
            if (!a.IsValid || a.TrackSlots.Length < 5 || a.Title.Length == 0 || a.ArtistSlots.Length == 0) continue;
            bool clash = false;
            foreach (Album p in picked) if (p.Title == a.Title) clash = true;
            if (!clash) picked.Add(a);
        }
        if (picked.Count < 5) return null;
        long now = DateTime.UtcNow.Ticks;
        long id = 5000;
        var index = LocalLibrary.Index;
        for (int k = 0; k < picked.Count; k++)
        {
            Album a = picked[k];
            string artist = ArtistOf(a);
            bool thin = k == 2;   // the Strong album's tags are thin (board 29's "low tide letters")
            string albumTag = thin ? a.Title.ToLowerInvariant() : a.Title;
            string folder = @"Spotify Match\" + artist + @"\" + a.Title;
            var batch = new LocalScanBatch(1, folder) { Folder = new LocalFolder(1, folder, (UInt128)(7000 + id), (ulong)id, ["cover.jpg"], true) };
            s_fixtureCovers[LocalCollection.AlbumKey(artist, albumTag)] = new Uri(Path.Combine(@"C:\Users\Listener\Music", folder, "cover.jpg")).AbsoluteUri;
            var tracks = a.TrackSlots;
            var edges = Entities.Current.Edges.AlbumTracks.Payload(a.Slot);
            for (int i = 0; i < tracks.Length; i++)
            {
                var t = new Track(tracks[i]);
                int number = i < edges.Length ? edges[i].Number : i + 1;
                string title = k == 4 ? "Session Take " + (i + 1).ToString(CultureInfo.InvariantCulture) : t.Title;
                int ms = t.DurationMs + k switch { 2 => 700, 3 when i % 2 == 0 => 9_000, _ => 0 };
                string isrc = k <= 1 ? Entities.Strings.Resolve(t.IsrcId) : "";
                var tags = new LocalTagSet(LocalKind.Audio, title, [artist], albumTag, thin ? "" : artist, thin ? 0 : number, 1, thin ? 0 : a.Year, ms, false,
                    k % 2 == 0 ? 1_050 : 320, LocalCover.None, 0, 0, true, k % 2 == 0 ? 96_000 : 44_100, k % 2 == 0 ? 24 : 0, 2, isrc);
                var format = k % 2 == 0 ? Spotify.Audio.Format.Flac : Spotify.Audio.Format.Mp3;
                string name = (i + 1).ToString("00", CultureInfo.InvariantCulture) + " " + title + (k % 2 == 0 ? ".flac" : ".mp3");
                batch.Upserts.Add(new LocalEntry(id, 1, folder + @"\" + name, 0, (UInt128)(9000 + id), 30_000_000, now, now, 0x20, format, tags, title, LocalState.Present, now, true, "",
                    now - TimeSpan.TicksPerDay * (k + 1)));
                id++;
            }
            index.Apply(batch);
        }
        // The loose songs: an untagged "Artist - Title" the catalogue has (0.4 s off), and an untagged demo it does not.
        var tracksTable = Entities.Current.Tracks;
        Track known = default;
        for (int slot = Table.None + 1; slot < tracksTable.Count; slot++)
        {
            var t = new Track(slot);
            if (t.IsValid && !t.IsLocal && t.Uri.Text.StartsWith("spotify:track:", StringComparison.Ordinal) && t.Title.Length > 0 && t.DurationMs > 0
                && Entities.Strings.Resolve(t.ArtistLineId) is { Length: > 0 } line && !line.Contains(',', StringComparison.Ordinal)) { known = t; break; }
        }
        var loose = new LocalScanBatch(1, "Downloads") { Folder = new LocalFolder(1, "Downloads", 6999, 0x6666, [], true) };
        long songId = id;
        if (known.IsValid)
        {
            string file = Entities.Strings.Resolve(known.ArtistLineId) + " - " + known.Title;
            loose.Upserts.Add(Untagged(id++, @"Downloads\" + file + ".mp3", file, known.DurationMs + 400, now));
        }
        loose.Upserts.Add(Untagged(id++, @"Downloads\untitled demo 3.mp3", "untitled demo 3", 192_000, now));
        index.Apply(loose);
        return SongKey(songId);

        static LocalEntry Untagged(long id, string rel, string name, int ms, long now)
            => new(id, 1, rel, 0, (UInt128)(9000 + id), 7_000_000, now, now, 0x20, Spotify.Audio.Format.Mp3,
                new LocalTagSet(LocalKind.Audio, "", [], "", "", 0, 0, 0, ms, false, 320, LocalCover.None, 0, 0, true, 44_100, 0, 2), name, LocalState.Present, now, true, "", now);
    }
}
