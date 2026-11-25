// ── Playback/LocalLink.cs ───────────────────────────────────────────────────────────────────────────────────────────
// "Your files and Spotify", phase 1 (the read side, show-only): the opt-in, the decisions the user makes in the review, what an accepted
// match shows in Wavee, and where it is kept (#240 after 0.4)
//
// Role: SHELL (UI thread: the signals, the verbs, the queries a row or a card asks; the save goes to a pool thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-spotify-linking-concept.md §4 US1-US5 and US10, §5 F-A, F-B and F-G, §6, §7;
//       board https://claude.ai/artifact/QA2Vq8BHDhuJKBnvd6GfwE frames 28-30, 32, 36, 37
//
// OPT-IN (F-A, §7). Nothing is sent to Spotify until the user turns matching on (`Platform.Keys`-style setting `local.link.on`, off by
// default). Before that the On this PC page may offer it once ("Wavee can add covers and correct names from Spotify to 42 albums"); "Not
// now" hides the offer for good (`local.link.offer`), and the switch stays in Settings › Storage › Music on this PC. At the opt-in the user
// may tick "Accept exact matches automatically" (`local.link.exact`, unticked): then an Exact group (the same ISRC on every song) is accepted
// as matching finds it; everything else waits for the review.
//
// WHAT IS SENT. The matcher (`LocalLink.Match.cs`) asks Spotify's own search and album reads through the entity layer, as the search page and
// an album page do: the album's (or song's) title and artist from the tags, then the candidate albums' track lists and the tracks' ISRCs.
// Paths, folder names and the files themselves never leave the PC.
//
// DECISIONS (sticky). Per group (an album key, or "song:<LocalId>" for a song with no album tag): Accepted (with the candidate it accepted and
// the file → track map), "Not this" (the rejected candidate never comes back), "Not on Spotify" (never matched again), and whose info wins
// (Spotify's, the default once accepted, or the files'). They are kept in `Cache\locallinks.tsv` beside the local index, written whole after
// every decision; a file's link is keyed by its LocalId, which survives moves and renames. Nothing is ever written to a music file.
//
// WHAT AN ACCEPTED MATCH SHOWS (show-only, US3). With Spotify's info winning: the file's row in the Songs table (title, artists, album, the
// cover), the album's card on the page (name, artist, year, cover), and the file's drawer, whose "Versions and formats" offers the Spotify
// version beside the file; read backwards, the Spotify song's own "Versions and formats" lists the file ("Your file · MP3 320": play it, queue
// it, show it in its folder, Remove link) and its row wears the file mark. Playback stays Spotify's: which source plays is the user's click
// ("Prefer my files" is a later phase). The matching title of the file (`LocalEntry.MatchTitle`) never changes: a playlist's `spotify:local:` item still
// finds the file by its own name. Unlinking (US10, F-G) returns the file to its own info at once.

using System.Globalization;
using System.Text;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>"Your files and Spotify" (see the file header).</summary>
public static partial class LocalLink
{
    static readonly SettingKey<bool> s_on = new("local.link.on", false);
    static readonly SettingKey<bool> s_exact = new("local.link.exact", false);
    static readonly SettingKey<bool> s_offer = new("local.link.offer", true);

    /// <summary>The opt-in's setting (Settings › Storage › Music on this PC reads it).</summary>
    public static SettingKey<bool> OnKey => s_on;

    /// <summary>One group's decision as kept on disk.</summary>
    sealed record Decided(LinkDecision Decision, string Chosen, bool SpotifyWins, HashSet<string> Rejected);

    static readonly Dictionary<string, Decided> s_decided = new(StringComparer.Ordinal);
    /// <summary>LocalId → the Spotify track its accepted group maps it to.</summary>
    static readonly Dictionary<long, LinkTrack> s_links = new();
    /// <summary>LocalId → the group that owns its link (for whose info wins and Unlink).</summary>
    static readonly Dictionary<long, string> s_linkGroup = new();
    /// <summary>Album key → the accepted Spotify album's face (name, artist, year, cover).</summary>
    static readonly Dictionary<string, LinkAlbum> s_albumFaces = new(StringComparer.Ordinal);
    /// <summary>Groups the matcher answered, by key (the review's model; rebuilt per run).</summary>
    static readonly Dictionary<string, LinkGroup> s_groups = new(StringComparer.Ordinal);

    static volatile Dictionary<long, LinkTrack> s_shown = new();
    static volatile Dictionary<string, LinkAlbum> s_shownAlbums = new(StringComparer.Ordinal);
    static int s_shownVersion;

    /// <summary>LocalId → the Spotify info its row shows (accepted, Spotify's info winning). Immutable; ANY thread (the restate's pass).</summary>
    public static IReadOnlyDictionary<long, LinkTrack> Shown => s_shown;

    /// <summary>Album key → the Spotify album its card shows (accepted, Spotify's info winning). Immutable; ANY thread (the collection's fold).</summary>
    public static IReadOnlyDictionary<string, LinkAlbum> ShownAlbums => s_shownAlbums;

    /// <summary>Moves with every change of <see cref="Shown"/> / <see cref="ShownAlbums"/>. Any thread.</summary>
    public static int ShownVersion => Volatile.Read(ref s_shownVersion);

    static readonly Signal<LinkView> s_view = new(LinkView.Empty);
    static readonly Signal<int> s_version = new(0);
    static readonly Signal<string?> s_review = new(null);
    static readonly Signal<bool> s_enabled = new(false);
    static readonly Signal<bool> s_offerShown = new(true);
    static string? s_path;
    static bool s_loaded, s_memoryOnly;

    /// <summary>The review's model (UI thread).</summary>
    public static IReadSignal<LinkView> View => s_view;

    /// <summary>Moves (UI thread) whenever what a row or a card shows may change: a decision, an unlink, whose info wins. The Local Files
    /// restate and the collection's fold read it.</summary>
    public static IReadSignal<int> Version => s_version;

    /// <summary>The review page: null closed, "" every group, else the key of the one group it was opened for ("Find on Spotify…" on a song).</summary>
    public static IReadSignal<string?> Reviewing => s_review;

    /// <summary>Matching is on (the user opted in).</summary>
    public static IReadSignal<bool> Enabled => s_enabled;

    /// <summary>The offer may still show ("Not now" was never pressed).</summary>
    public static IReadSignal<bool> OfferOpen => s_offerShown;

    /// <summary>Accept Exact groups as matching finds them (ticked at the opt-in).</summary>
    public static bool AutoExact => Platform.Settings.Get(s_exact);

    // ── life ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>App start (beside the local index): read the settings and the kept decisions (one small file). UI thread.</summary>
    public static void Use(string? cacheDir)
    {
        if (s_loaded) return;
        s_loaded = true;
        s_enabled.Value = Platform.Settings.Get(s_on);
        s_offerShown.Value = Platform.Settings.Get(s_offer);
        s_preferFilesOn.Value = Platform.Settings.Get(s_preferFiles);
        if (cacheDir is null) { s_memoryOnly = true; return; }
        s_path = Path.Combine(cacheDir, "locallinks.tsv");
        try { if (File.Exists(s_path)) Read(File.ReadAllLines(s_path, Encoding.UTF8)); }
        catch (Exception ex) { Log.Warn("local.link", "local.link.load failed", ex); }
        Publish();
        Video.LinkedVideos.MigrateLegacy();   // once: the old attached videos become linked video versions (Shell/Video.LinkedVideos.cs)
        if (s_enabled.Peek()) StartMatching();
    }

    // ── what a row, a card or the drawer asks (UI thread) ────────────────────────────────────────────────────────────

    /// <summary>The Spotify track an accepted match links this file to, or null.</summary>
    public static LinkTrack? LinkOf(long localId) => s_links.TryGetValue(localId, out LinkTrack? t) ? t : null;

    /// <summary>A row uri of an indexed file that has an accepted match (tracked: reads <see cref="Version"/>).</summary>
    public static bool HasLink(string uri)
    {
        _ = s_version.Value;
        return LocalLibrary.TryLocalIdOf(uri, out long id) && s_links.ContainsKey(id);
    }

    /// <summary>The files in the music folders an accepted match links to this Spotify track (the link read backwards: the Spotify song's
    /// "Versions and formats" lists them, its row wears the file mark). Empty for most. Tracked: reads <see cref="Version"/>.</summary>
    public static IReadOnlyList<long> FilesFor(string spotifyUri)
    {
        _ = s_version.Value;
        return s_byTrack.TryGetValue(spotifyUri, out long[]? ids) ? ids : [];
    }

    /// <summary>Spotify track uri → the accepted files linked to it (rebuilt with every decision).</summary>
    static Dictionary<string, long[]> s_byTrack = new(StringComparer.Ordinal);

    /// <summary>A song group's key.</summary>
    public static string SongKey(long localId) => "song:" + localId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The group a file belongs to: its album's key, or its own song key when it has no album tag. From the file's own names
    /// (<see cref="LocalCollection.TagArtistOf"/>): what the collection shows for it (a link, a rename) never moves it to another group.</summary>
    public static string GroupKeyOf(LocalEntry e)
        => e.Tags.Album.Length > 0 ? LocalCollection.AlbumKey(LocalCollection.TagArtistOf(e), e.Tags.Album) : SongKey(e.LocalId);

    /// <summary>The offer's counts: of <paramref name="albums"/> and <paramref name="songs"/> (the collection's albums, and its songs with no album
    /// tag, each its own group), those no decision covers yet.</summary>
    public static (int Albums, int Songs) OfferCount(int albums, int songs) => (Math.Max(0, albums - s_decidedAlbums), Math.Max(0, songs - s_decidedSongs));

    static int s_decidedAlbums, s_decidedSongs;

    /// <summary>The plain evidence of the match a file's link came from, while the review holds its group ("Title, artist and length"); else "".</summary>
    public static string EvidenceOf(long localId)
        => s_linkGroup.TryGetValue(localId, out string? key) && s_groups.TryGetValue(key, out LinkGroup? g) && g.Best is { } best
           && s_decided.TryGetValue(key, out Decided? d) && d.Decision == LinkDecision.Accepted && best.Uri == d.Chosen
            ? LocalLinkWords.Evidence(best, g.IsAlbum) : "";

    /// <summary>The file's group was marked "Not on Spotify".</summary>
    public static bool IsNotOnSpotify(long localId)
        => LocalLibrary.Index.TryGet(localId, out LocalEntry? e) && s_decided.TryGetValue(GroupKeyOf(e), out Decided? d) && d.Decision == LinkDecision.NotOnSpotify;

    // ── the verbs (UI thread) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Turn on matching" (the opt-in dialog): remember it and whether Exact is accepted for the user, then match.</summary>
    public static void TurnOn(bool acceptExact)
    {
        Platform.Settings.Set(s_on, true);
        Platform.Settings.Set(s_exact, acceptExact);
        Platform.Settings.Set(s_offer, false);
        s_offerShown.Value = false;
        s_enabled.Value = true;
        StartMatching();
    }

    /// <summary>Settings' switch: off stops matching and keeps every decision (accepted matches keep showing); on matches again.</summary>
    public static void SetEnabled(bool on)
    {
        if (on) { TurnOn(AutoExact); return; }
        Platform.Settings.Set(s_on, false);
        s_enabled.Value = false;
        StopMatching();
        Publish();
    }

    /// <summary>"Not now" on the offer: it never shows again (the switch stays in Settings).</summary>
    public static void DismissOffer()
    {
        Platform.Settings.Set(s_offer, false);
        s_offerShown.Value = false;
    }

    /// <summary>Open the review: every group (<paramref name="focus"/> null or ""), or the one group a song belongs to.</summary>
    public static void OpenReview(string? focus = null)
    {
        s_review.Value = focus ?? "";
        if (Shell.Current.Peek().Kind != Shell.RouteKind.Local) Shell.GoTo(new Shell.Route(Shell.RouteKind.Local));
    }

    /// <summary>Back to the page from the review.</summary>
    public static void CloseReview() => s_review.Value = null;

    /// <summary>"Find on Spotify…" on a song (its row menu, its drawer) once matching is on: the song's group is matched now, ahead of the
    /// background queue, and the review opens on it (its candidates, a search, Not on Spotify). Off, the caller asks first
    /// (<c>LocalLinkPage.FindOnSpotify</c>).</summary>
    public static void FindNow(long localId)
    {
        if (!LocalLibrary.Index.TryGet(localId, out LocalEntry? e)) return;
        string key = GroupKeyOf(e);
        MatchNow(key);
        OpenReview(key);
    }

    /// <summary>Accept the group's best candidate.</summary>
    public static void Accept(string key)
    {
        if (!s_groups.TryGetValue(key, out LinkGroup? g) || g.Best is not { } best) return;
        Apply(g, best);
        Save();
    }

    /// <summary>Accept one of the candidates the review lists ("Pick").</summary>
    public static void Pick(string key, int candidate)
    {
        if (!s_groups.TryGetValue(key, out LinkGroup? g) || (uint)candidate >= (uint)g.Candidates.Count) return;
        Apply(g, g.Candidates[candidate]);
        Save();
    }

    /// <summary>"Accept all exact": every undecided Exact group.</summary>
    public static void AcceptAllExact()
    {
        bool any = false;
        foreach (LinkGroup g in s_groups.Values.ToArray())
            if (g.Decision == LinkDecision.None && g.Best is { Confidence: LinkConfidence.Exact } best) { Apply(g, best, publish: false); any = true; }
        if (!any) return;
        Publish();
        Save();
    }

    /// <summary>"Not this album" / "Not this song": the shown candidate is rejected for good; the others (and a search) are offered.</summary>
    public static void NotThis(string key)
    {
        if (!s_groups.TryGetValue(key, out LinkGroup? g)) return;
        Decided d = DecidedOf(key);
        string current = d.Decision == LinkDecision.Accepted ? d.Chosen : g.Best?.Uri ?? "";
        if (current.Length > 0) d.Rejected.Add(current);
        Unapply(key);
        s_decided[key] = d with { Decision = LinkDecision.NotThis, Chosen = "" };
        s_groups[key] = g with { Decision = LinkDecision.NotThis, Candidates = Without(g.Candidates, d.Rejected) };
        Publish();
        Save();
    }

    /// <summary>"Not on Spotify": the group keeps its own info and is never matched again.</summary>
    public static void MarkNotOnSpotify(string key)
    {
        Decided d = DecidedOf(key);
        Unapply(key);
        s_decided[key] = d with { Decision = LinkDecision.NotOnSpotify, Chosen = "" };
        if (s_groups.TryGetValue(key, out LinkGroup? g)) s_groups[key] = g with { Decision = LinkDecision.NotOnSpotify };
        Publish();
        Save();
    }

    /// <summary>Undo a decision (Undo on the review, "Remove link" in the drawer): the group is proposed again; nothing on disk changes.</summary>
    public static void Undecide(string key)
    {
        if (!s_decided.TryGetValue(key, out Decided? d)) return;
        Unapply(key);
        s_decided[key] = d with { Decision = LinkDecision.None, Chosen = "" };
        if (s_groups.TryGetValue(key, out LinkGroup? g)) s_groups[key] = g with { Decision = LinkDecision.None, Candidates = Without(g.Candidates, d.Rejected) };
        Publish();
        Save();
    }

    /// <summary>"Remove link" on a linked file (F-G): its group's match is undone; the file shows its own info at once.</summary>
    public static void Unlink(long localId)
    {
        if (s_linkGroup.TryGetValue(localId, out string? key)) Undecide(key);
    }

    /// <summary>"Whose info wins for this album": Spotify's (true) or the files' (false).</summary>
    public static void SetSpotifyWins(string key, bool spotify)
    {
        Decided d = DecidedOf(key);
        if (d.SpotifyWins == spotify && s_decided.ContainsKey(key)) return;
        s_decided[key] = d with { SpotifyWins = spotify };
        if (s_groups.TryGetValue(key, out LinkGroup? g)) s_groups[key] = g with { SpotifyWins = spotify };
        Publish();
        Save();
    }

    /// <summary>The group's decision record (a fresh one when it has none).</summary>
    static Decided DecidedOf(string key)
        => s_decided.TryGetValue(key, out Decided? d) ? d : new Decided(LinkDecision.None, "", true, new HashSet<string>(StringComparer.Ordinal));

    static IReadOnlyList<LinkCandidate> Without(IReadOnlyList<LinkCandidate> list, HashSet<string> rejected)
    {
        if (rejected.Count == 0) return list;
        var kept = new List<LinkCandidate>(list.Count);
        foreach (LinkCandidate c in list) if (!rejected.Contains(c.Uri)) kept.Add(c);
        return kept;
    }

    /// <summary>Accept <paramref name="c"/> for <paramref name="g"/>: the file → track links, the album's face, the decision.</summary>
    static void Apply(LinkGroup g, LinkCandidate c, bool publish = true)
    {
        Unapply(g.Key);
        Decided d = DecidedOf(g.Key);
        s_decided[g.Key] = d with { Decision = LinkDecision.Accepted, Chosen = c.Uri };
        for (int i = 0; i < g.Files.Count && i < c.Tracks.Count; i++)
        {
            if (c.Tracks[i] is not { } t) continue;
            // A file the user linked on its own (a version: LinkVersion, an attached video) keeps its link when its album is matched.
            if (s_linkGroup.TryGetValue(g.Files[i], out string? own) && own != g.Key && s_decided.TryGetValue(own, out Decided? od) && od.Decision == LinkDecision.Accepted) continue;
            s_links[g.Files[i]] = t;
            s_linkGroup[g.Files[i]] = g.Key;
        }
        if (g.IsAlbum) s_albumFaces[g.Key] = new LinkAlbum(c.Uri, c.Title, c.Artist, c.Year, c.Cover, []);
        s_groups[g.Key] = g with { Decision = LinkDecision.Accepted, Candidates = MoveFirst(g.Candidates, c) };
        if (publish) Publish();
    }

    static IReadOnlyList<LinkCandidate> MoveFirst(IReadOnlyList<LinkCandidate> list, LinkCandidate c)
    {
        if (list.Count > 0 && ReferenceEquals(list[0], c)) return list;
        var moved = new List<LinkCandidate>(list.Count) { c };
        foreach (LinkCandidate x in list) if (!ReferenceEquals(x, c)) moved.Add(x);
        return moved;
    }

    static void Unapply(string key)
    {
        var drop = new List<long>();
        foreach (var (id, k) in s_linkGroup) if (k == key) drop.Add(id);
        foreach (long id in drop) { s_links.Remove(id); s_linkGroup.Remove(id); }
        s_albumFaces.Remove(key);
    }

    // ── the view ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuild the review's model from the groups and move <see cref="Version"/> (UI thread).</summary>
    static void Publish()
    {
        var all = new List<LinkGroup>(s_groups.Values);
        all.Sort(static (a, b) =>
        {
            if (a.IsAlbum != b.IsAlbum) return a.IsAlbum ? -1 : 1;
            int c = RankOf(a.Key).CompareTo(RankOf(b.Key));
            return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });
        int exact = 0, strong = 0, likely = 0, check = 0, accepted = 0, albums = 0, songs = 0;
        foreach (LinkGroup g in all)
        {
            if (g.Decision is LinkDecision.Accepted or LinkDecision.NotOnSpotify)
            {
                if (g.Decision == LinkDecision.Accepted) accepted++;
                if (g.IsAlbum) albums++; else songs++;
                continue;
            }
            switch (g.Confidence)
            {
                case LinkConfidence.Exact: exact++; break;
                case LinkConfidence.Strong: strong++; break;
                case LinkConfidence.Likely: likely++; break;
                default: check++; break;
            }
        }
        foreach (var (key, d) in s_decided)
            if (!s_groups.ContainsKey(key) && d.Decision is LinkDecision.Accepted or LinkDecision.NotOnSpotify)
            {
                if (key.StartsWith("song:", StringComparison.Ordinal)) songs++; else albums++;
            }
        s_decidedAlbums = albums;
        s_decidedSongs = songs;
        var finding = new List<string>(s_searches.Count);
        foreach (string key in s_searches.Keys) if (!s_groups.ContainsKey(key)) finding.Add(key);
        var old = s_view.Peek();
        s_view.Value = new LinkView
        {
            Version = old.Version + 1, Phase = s_enabled.Peek() ? (s_running ? LinkPhase.Matching : LinkPhase.Ready) : LinkPhase.Off,
            Done = s_done, Total = s_total, Groups = all, Exact = exact, Strong = strong, Likely = likely, Check = check, Accepted = accepted, Finding = finding,
        };
        RebuildShown();
        s_version.Value++;
    }

    /// <summary>Rebuild the immutable maps a row and a card read, and restate the rows when they moved.</summary>
    static void RebuildShown()
    {
        var byTrack = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var (id, t) in s_links)
            if (s_linkGroup.TryGetValue(id, out string? key) && s_decided.TryGetValue(key, out Decided? d) && d.Decision == LinkDecision.Accepted)
            {
                if (!byTrack.TryGetValue(t.Uri, out var ids)) byTrack[t.Uri] = ids = [];
                ids.Add(id);
            }
        var reversed = new Dictionary<string, long[]>(byTrack.Count, StringComparer.Ordinal);
        foreach (var (uri, ids) in byTrack) reversed[uri] = [.. ids];
        s_byTrack = reversed;
        FeedVideos();   // a linked video version is its track's video (LocalLink.Video.cs)
        var tracks = new Dictionary<long, LinkTrack>();
        foreach (var (id, t) in s_links)
            if (s_linkGroup.TryGetValue(id, out string? key) && s_decided.TryGetValue(key, out Decided? d) && d.Decision == LinkDecision.Accepted && d.SpotifyWins)
                tracks[id] = t;
        var albums = new Dictionary<string, LinkAlbum>(StringComparer.Ordinal);
        foreach (var (key, a) in s_albumFaces)
            if (s_decided.TryGetValue(key, out Decided? d) && d.Decision == LinkDecision.Accepted && d.SpotifyWins) albums[key] = a;
        bool versions = RebuildVersions();   // phase 2: the renames and which version plays
        if (Same(tracks, s_shown) && Same(albums, s_shownAlbums) && !versions) return;
        s_shown = tracks;
        s_shownAlbums = albums;
        WatchArtistArt();                     // a linked song's artist card wears its Spotify picture (LocalLink.ArtistArt.cs)
        LocalLibrary.NotePlayAs(s_playAs);
        Interlocked.Increment(ref s_shownVersion);
        LocalLibrary.RestateForLinks();
        LocalCollection.Request();

        static bool Same<TKey, TValue>(Dictionary<TKey, TValue> a, Dictionary<TKey, TValue> b) where TKey : notnull where TValue : class
        {
            if (a.Count != b.Count) return false;
            foreach (var (k, v) in a) if (!b.TryGetValue(k, out TValue? w) || !ReferenceEquals(v, w)) return false;
            return true;
        }
    }

    /// <summary>The review's order: the confidence a group had when it was scored (Exact, Strong, Likely, Check). It never moves for a
    /// decision: an accepted card, or one the user said "Not this" to, changes its form in place instead of leaving from under the pointer.</summary>
    static int RankOf(string key) => s_rank.TryGetValue(key, out int r) ? r : (int)LinkConfidence.Check;

    static readonly Dictionary<string, int> s_rank = new(StringComparer.Ordinal);

    // ── the file ─────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // One line per record, tab-separated, a tab or a line break inside a value becomes a space:
    //   G  key  decision  chosen-uri  spotify-wins(1|0)  rejected-uri rejected-uri …
    //   L  local-id  group-key  track-uri  title  artists  album  album-uri  duration-ms  isrc  disc  number  cover  year  album-artist
    //   A  group-key  album-uri  title  artist  year  cover
    //   R, D, T  a rename, a song's default version and a video's picture choice (LocalLink.Versions.cs, LocalLink.Attach.cs)
    // An unknown line is skipped, so a later version can add records an older one ignores.

    static void Read(string[] lines)
    {
        foreach (string line in lines)
        {
            string[] f = line.Split('\t');
            if (ReadVersions(f)) continue;
            switch (f[0])
            {
                case "G" when f.Length >= 5 && byte.TryParse(f[2], out byte decision) && decision <= (byte)LinkDecision.NotOnSpotify:
                    var rejected = new HashSet<string>(StringComparer.Ordinal);
                    if (f.Length > 5) foreach (string r in f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries)) rejected.Add(r);
                    s_decided[f[1]] = new Decided((LinkDecision)decision, f[3], f[4] == "1", rejected);
                    break;
                case "L" when f.Length >= 14 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long id):
                    s_links[id] = new LinkTrack(f[3], f[4], f[5], f[6], f[7], Int(f[8]), f[9], Int(f[10]), Int(f[11]), f[12].Length > 0 ? f[12] : null, Int(f[13]), f.Length > 14 ? f[14] : "");
                    s_linkGroup[id] = f[2];
                    break;
                case "A" when f.Length >= 7:
                    s_albumFaces[f[1]] = new LinkAlbum(f[2], f[3], f[4], Int(f[5]), f[6].Length > 0 ? f[6] : null, []);
                    break;
            }
        }
        // A link whose group is no longer accepted is dropped (the file is only ever written whole, so this is a guard, not a repair).
        var stale = new List<long>();
        foreach (var (id, key) in s_linkGroup) if (!s_decided.TryGetValue(key, out Decided? d) || d.Decision != LinkDecision.Accepted) stale.Add(id);
        foreach (long id in stale) { s_links.Remove(id); s_linkGroup.Remove(id); }

        static int Int(string s) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : 0;
    }

    static int s_saving;

    /// <summary>Write the decisions whole (a pool thread; the lines are built here on the UI thread). Inspector fixtures never write.</summary>
    static void Save()
    {
        if (s_memoryOnly || s_path is not { } path) return;
        var sb = new StringBuilder();
        foreach (var (key, d) in s_decided)
            sb.Append('G').Append('\t').Append(Clean(key)).Append('\t').Append((byte)d.Decision).Append('\t').Append(Clean(d.Chosen)).Append('\t')
              .Append(d.SpotifyWins ? '1' : '0').Append('\t').Append(string.Join(' ', d.Rejected)).Append('\n');
        foreach (var (id, t) in s_links)
        {
            string key = s_linkGroup.TryGetValue(id, out string? k) ? k : "";
            sb.Append('L').Append('\t').Append(id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(key)).Append('\t').Append(Clean(t.Uri)).Append('\t')
              .Append(Clean(t.Title)).Append('\t').Append(Clean(t.Artists)).Append('\t').Append(Clean(t.Album)).Append('\t').Append(Clean(t.AlbumUri)).Append('\t')
              .Append(t.DurationMs.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(t.Isrc)).Append('\t').Append(t.Disc.ToString(CultureInfo.InvariantCulture))
              .Append('\t').Append(t.Number.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(t.Cover ?? "")).Append('\t')
              .Append(t.Year.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(t.AlbumArtist)).Append('\n');
        }
        foreach (var (key, a) in s_albumFaces)
            sb.Append('A').Append('\t').Append(Clean(key)).Append('\t').Append(Clean(a.Uri)).Append('\t').Append(Clean(a.Title)).Append('\t').Append(Clean(a.Artist)).Append('\t')
              .Append(a.Year.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(a.Cover ?? "")).Append('\n');
        WriteVersions(sb, Clean);
        string text = sb.ToString();
        int generation = Interlocked.Increment(ref s_saving);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            if (Volatile.Read(ref s_saving) != generation) return;   // a newer save is queued: it writes the newer whole
            try
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) { Log.Warn("local.link", "local.link.save failed", ex); }
        }, null);

        static string Clean(string s) => s.IndexOfAny(['\t', '\n', '\r']) < 0 ? s : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
