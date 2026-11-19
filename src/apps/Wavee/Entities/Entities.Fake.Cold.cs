// ── Entities/Entities.Fake.Cold.cs — the inspector's COLD playlist: a list that loads the way a real one does (P6 playlist-load lane) ──────────────
//
// Every --fake list is seeded whole at boot, so no fake page ever shows its loading choreography. The inspector stages one (`state kind=cold-list`,
// `cold-rows`, `cold-faces`), one step per command, so an agent can trace each frame of the transition:
//
//   · cold-list   the playlist's title and cover are known, its length and membership are NOT (the meta line loading, the rows' shimmer);
//   · cold-head   (instead of cold-list) the HEADER is not known either: no title, description or owner, only the cover (a deep link's first
//                 frames); the page shows its pending head until cold-rows / cold-warm land the header with the membership (one answer);
//   · cold-rows   the membership lands: 50 rows, of which rows 1 and 4 already know their face (cached elsewhere) and the rest are still in flight
//                 (the owner's recording: "Smart" alone, rows 1 and 4 with a hole between);
//   · cold-warm   (instead of cold-rows) the membership lands with every row restored from disk: faces known, credits in flight;
//   · cold-faces  the rows' own fields land (titles, artists, covers, durations: the meta line gains its duration).
//   · regen       a regenerated Mix's fresh answer (the owner's recording, 2026-11-15, Heartbreak Mix): open the Mix first (`goto` pl6: a seeded list
//                 nobody revalidated this session: the page paints it and revalidates behind it, and the fake never answers), then `regen` lands
//                 what the /diff would: a new revision with other rows (a re-cut: most rows leave, a few glide, most arrive), and a new cover and
//                 a description that alternates per regen between a short line and one that wraps to three lines in a 216 DIP rail;
//   · regen-edit  a SMALL remote edit over the list the page shows (another device): display rows 2, 5 and 6 removed, row 9 moved to row 3, new
//                 tracks inserted at rows 1, 4 and 30 (the last off the first screen); a new cover, the alternating description and a new
//                 revision. A pure function of the current membership and an epoch, so it repeats; the count stays.
//
// Inspector-only (`Shell.SetInspectState`); every value is a pure function of the row index and a per-staging epoch, so a second `cold-list` is cold again.

using FluentGpu.Foundation;

namespace Wavee;

public static partial class Entities
{
    /// <summary>The playlist the cold stages act on (a seeded 50-row list, so its header has a title, a cover and a count).</summary>
    public static string FakeColdPlaylistUri => PlaylistUri(6);

    const int ColdRows = 50;
    static int s_coldEpoch;
    static readonly int[] s_coldWarm = [0, 3];   // the rows that already know their face when the membership lands

    static string ColdTrackUri(int k) => "spotify:track:cold" + s_coldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Stage 0: the membership forgets its rows (Unknown, nothing resident); the header stays known.</summary>
    public static void StageColdList()
    {
        s_coldEpoch++;
        int list = ResolveSeedSlot(EntityKind.Playlist, FakeColdPlaylistUri);
        if (list == Table.None) return;
        Current.Edges.PlaylistTracks.Replace(list, ReadOnlySpan<int>.Empty, ReadOnlySpan<PlaylistTrackEdge>.Empty, EdgeState.Unknown, 0);
        // The header knows its title and cover but not its length or saves yet (a sidebar row's thin header): the meta line is LOADING, as in the
        // owner's recording, until the membership states the count.
        Current.Playlists.Known[list] &= ~(uint)(PlaylistFields.TrackCount | PlaylistFields.Saves);
        Current.Playlists.TrackCount[list] = 0;
        Current.PublishAll();
    }

    // The header cold-head took away (title, description, owner and the Identity bit), put back by the stage that lands the membership.
    static (StringId Title, StringId Description, int Owner)? s_coldHead;

    /// <summary>Stage 0b, instead of <see cref="StageColdList"/> (`state cold-head`): the membership AND the header are unknown (a deep link's
    /// first frames): title, description and owner are empty and Identity is not known, so the page draws its pending head. The cover stays (a
    /// card that led here had it).</summary>
    public static void StageColdHead()
    {
        RestoreColdHead();
        StageColdList();
        int list = ResolveSeedSlot(EntityKind.Playlist, FakeColdPlaylistUri);
        if (list == Table.None) return;
        var t = Current.Playlists;
        s_coldHead = (t.Title[list], t.Description[list], t.Owner[list]);
        t.Title[list] = StringId.Empty;
        t.Description[list] = StringId.Empty;
        t.Owner[list] = 0;
        t.Known[list] &= ~(uint)PlaylistFields.Identity;
        t.Version[list]++;
        Current.PublishAll();
    }

    /// <summary>The header <see cref="StageColdHead"/> took away, back (Identity known again); nothing when no cold-head ran. The stages that land
    /// the membership call it (the header and the rows are one answer), and so does the inspector's restore on the next mount.</summary>
    public static void RestoreColdHead()
    {
        if (s_coldHead is not { } head) return;
        s_coldHead = null;
        int list = ResolveSeedSlot(EntityKind.Playlist, FakeColdPlaylistUri);
        if (list == Table.None) return;
        var t = Current.Playlists;
        t.Title[list] = head.Title;
        t.Description[list] = head.Description;
        t.Owner[list] = head.Owner;
        t.Known[list] |= (uint)PlaylistFields.Identity;
        t.Version[list]++;
    }

    // ── a BOOK that loads the way a real one does (`state kind=cold-book`, then `cold-book-chapters`, then `cold-book-facts`) ──
    // The owner's recording (2026-11-18, Designing Data-Intensive Applications, 2nd Edition): the page opened on the show's own answer (the title and
    // the subtitle joined in one string, no chapters, no length), the membership landed a tenth of a second later (the chapters' names, no lengths),
    // and the book's credits and the chapters' lengths two seconds after that. One stage per answer, on sh9 (The Long Book):
    //   · cold-book           the membership is unknown and asked, the credits are not known, the title is the joined one (`goto` sh9 after it);
    //   · cold-book-chapters  the membership lands whole; no chapter knows its length yet;
    //   · cold-book-facts     the credits land (the title and the subtitle apart, the book's length) and so does every chapter's length.

    /// <summary>The book the cold-book stages act on (sh9, a hundred chapters).</summary>
    public static string FakeColdBookUri => AlbumSeed.ColdBookUri;

    static bool s_bookCold;

    /// <summary>`state kind=cold-book`: sh9 as the show's own answer leaves it (no membership, no credits, no lengths).</summary>
    public static void StageColdBook()
    {
        RestoreColdBook();
        s_bookCold = true;
        AlbumSeed.ColdBook();
    }

    /// <summary>`state kind=cold-book-chapters`: the membership lands (names, no lengths).</summary>
    public static void StageColdBookChapters()
    {
        if (s_bookCold) AlbumSeed.ColdBookChapters();
    }

    /// <summary>`state kind=cold-book-facts`: the credits and the chapters' lengths land.</summary>
    public static void StageColdBookFacts()
    {
        if (s_bookCold) AlbumSeed.ColdBookFacts();
    }

    /// <summary>The book as the boot seeded it; nothing when no cold-book ran. The inspector's restore on the next mount calls it.</summary>
    public static void RestoreColdBook()
    {
        if (!s_bookCold) return;
        s_bookCold = false;
        AlbumSeed.RestoreBook();
    }

    static partial class AlbumSeed
    {
        public static string ColdBookUri => ShowUri(LongBook);

        const string ColdBookTitle = "The Long Book", ColdBookSubtitle = "A Voyage in One Hundred Chapters, Told by the Keepers of the Light at Vell";

        /// <summary>What the cold stage took: each chapter's length, and the fields the show knew.</summary>
        static int[]? s_bookLengths;
        static uint s_bookKnown;

        static int BookSlot => SlotOf(Current.Shows, ShowUri(LongBook));

        public static void ColdBook()
        {
            Scope s = Current;
            var t = s.Shows;
            int show = BookSlot;
            s_bookKnown = t.Known[show];
            RetainText(ref t.Title[show], Intern(Utf8(ColdBookTitle + ": " + ColdBookSubtitle)));
            t.Known[show] &= ~(uint)ShowFields.Credits;
            t.Version[show]++;
            t.MarkDirty();
            int n = EpisodesIn(LongBook);
            s_bookLengths = new int[n];
            for (int i = 0; i < n; i++)
            {
                int ep = SlotOf(s.Episodes, EpisodeUri(LongBook, i));
                s_bookLengths[i] = s.Episodes.DurationMs[ep];
                s.Episodes.DurationMs[ep] = 0;
                s.Episodes.Version[ep]++;
            }
            s.Episodes.MarkDirty();
            s.Edges.ShowEpisodes.Replace(show, ReadOnlySpan<int>.Empty, default, EdgeState.Unknown, 0);
            s.Edges.ShowEpisodes.MarkAsked(show, 0);
            s.PublishAll();
        }

        public static void ColdBookChapters()
        {
            Scope s = Current;
            int n = EpisodesIn(LongBook);
            var targets = new int[n];
            for (int i = 0; i < n; i++) targets[i] = SlotOf(s.Episodes, EpisodeUri(LongBook, i));
            s.Edges.ShowEpisodes.Replace(BookSlot, targets, default, EdgeState.Complete, n);
            s.PublishAll();
        }

        public static void ColdBookFacts()
        {
            Scope s = Current;
            var t = s.Shows;
            int show = BookSlot;
            long total = Lengths();
            RetainText(ref t.BookTitle[show], Intern(Utf8(ColdBookTitle)));
            RetainText(ref t.BookSubtitle[show], Intern(Utf8(ColdBookSubtitle)));
            t.BookDurationMs[show] = (int)Math.Min(int.MaxValue, total);
            t.Known[show] |= (uint)ShowFields.Credits;
            t.Version[show]++;
            t.MarkDirty();
            s.PublishAll();
        }

        /// <summary>Every chapter's length back (once); the sum.</summary>
        static long Lengths()
        {
            Scope s = Current;
            long total = 0;
            int n = EpisodesIn(LongBook);
            for (int i = 0; i < n; i++)
            {
                int ep = SlotOf(s.Episodes, EpisodeUri(LongBook, i));
                if (s_bookLengths is { } held && s.Episodes.DurationMs[ep] == 0)
                {
                    s.Episodes.DurationMs[ep] = held[i];
                    s.Episodes.Version[ep]++;
                }
                total += s.Episodes.DurationMs[ep];
            }
            s_bookLengths = null;
            s.Episodes.MarkDirty();
            return total;
        }

        public static void RestoreBook()
        {
            Scope s = Current;
            var t = s.Shows;
            int show = BookSlot;
            Lengths();
            RetainText(ref t.Title[show], Intern(Utf8(ColdBookTitle)));
            RetainText(ref t.BookTitle[show], StringId.Empty);
            RetainText(ref t.BookSubtitle[show], StringId.Empty);
            t.BookDurationMs[show] = 0;
            t.Known[show] = s_bookKnown;
            t.Version[show]++;
            t.MarkDirty();
            ColdBookChapters();
        }
    }

    // ── any detail page's header, unknown in place (`state kind=head uri=<uri>`, then `state kind=head-land`) ──
    // An album's, a list's, a show's or an episode's title is taken away with every field it knows (a cold open: no title, publisher, rating, cover), so
    // the page draws its pending head; the landing puts them back on the page that shows it, so a trace sees the head land as the real client's first
    // read does (audit B7 r2).

    /// <summary>The header <see cref="StageHead"/> took away: the table, the slot, the title and the known fields.</summary>
    static (Table Table, Column<StringId> Title, int Slot, StringId Value, uint Known)? s_head;

    /// <summary>Take the header of the album, playlist, show or episode <paramref name="uri"/> away (its title and every known field, the one the
    /// page's HeaderPending reads among them). False for another kind or an unknown uri.</summary>
    public static bool StageHead(string uri, bool artistOverview = false)
    {
        RestoreHead();
        var kind = EntityUri.KindOf(uri.AsSpan());
        int slot = ResolveSeedSlot(kind, uri);
        if (slot == Table.None) return false;
        if (kind == EntityKind.Artist) return artistOverview ? StageArtistOverview(slot) : StageChartLate(slot);
        Scope s = Current;
        (Table Table, Column<StringId> Title)? head = kind switch
        {
            EntityKind.Album => (s.Albums, s.Albums.Title),
            EntityKind.Playlist => (s.Playlists, s.Playlists.Title),
            EntityKind.Show => (s.Shows, s.Shows.Title),
            EntityKind.Episode => (s.Episodes, s.Episodes.Title),
            _ => null,
        };
        if (head is not { } h) return false;
        s_head = (h.Table, h.Title, slot, h.Title[slot], h.Table.Known[slot]);
        h.Title[slot] = StringId.Empty;
        h.Table.Known[slot] = 0;
        h.Table.Version[slot]++;
        h.Table.MarkDirty();   // a publication carries only the tables marked
        s.PublishAll();
        return true;
    }

    // ── an artist page's chart rows still in flight after everything else is known (`state kind=head uri=<artist>`, then `head-land`) ──
    // The owner's endless shimmer (2026-11-07, Fly By Midnight): the overview, the chart bit and the popular list were all warm (a now-playing
    // prefetch), so the only answers still to come were the charted rows' own fields, and they were the last thing to land. The stage takes those
    // fields away from the showcase artist's charted rows, asked and in flight; the landing puts them back the way an answer does (known, the
    // in-flight mark cleared, the row's version bumped, the track table published) and nothing else moves.

    /// <summary>The charted rows <see cref="StageChartLate"/> took the fields of, and what each knew.</summary>
    static (int[] Slots, uint[] Known)? s_chartLate;

    /// <summary>The artist <see cref="StageArtistOverview"/> took the overview of, and what it knew.</summary>
    static (int Slot, uint Known)? s_artistOverview;

    /// <summary>`state kind=head uri=<artist> answer=hold`: the artist's overview and chart bit are not known (a cold open: the page asks for them, and
    /// the scripted answer holds the ask). The landing puts the known bits back.</summary>
    static bool StageArtistOverview(int artist)
    {
        var t = Current.Artists;
        s_artistOverview = (artist, t.Known[artist]);
        t.Known[artist] &= ~(uint)(ArtistFields.Overview | ArtistFields.Chart);
        t.Version[artist]++;
        t.MarkDirty();
        StageArtistCards(artist);
        Current.PublishAll();
        return true;
    }

    // ── the release cards of an artist page, landing after its overview (`head-land part=overview`, then `head-land part=cards`) ──
    // The real client's order (the owner's recording, 2026-11-15): the overview and the chart land first, and the release cards beside them (the latest
    // release, the discography preview's covers) land a few frames later, as their own album rows' answers do. The hold stages those rows' card fields
    // unknown, asked and in flight with the overview; `part=overview` lands the overview alone and `part=cards` the cards (no `part`: both at once).

    /// <summary>The album rows <see cref="StageArtistCards"/> took the card fields of, and what each knew.</summary>
    static (int[] Slots, uint[] Known)? s_artistCards;

    /// <summary>The request stamp of the staged card rows' fetch (cleared by the landing).</summary>
    const uint ArtistCardsStamp = 0xCA4Du;

    /// <summary>Take <see cref="AlbumFields.DiscoCard"/> away from <paramref name="artist"/>'s latest release and its discography's releases: unknown,
    /// asked and in flight, so the side column's cards read "still coming".</summary>
    static void StageArtistCards(int artist)
    {
        RestoreArtistCards();
        var a = new Artist(artist);
        var slots = new List<int>();
        if (a.Latest.IsValid) slots.Add(a.Latest.Slot);
        foreach (int s in a.AlbumSlots) if (!slots.Contains(s)) slots.Add(s);
        foreach (int s in a.SingleSlots) if (!slots.Contains(s)) slots.Add(s);
        foreach (int s in a.CompilationSlots) if (!slots.Contains(s)) slots.Add(s);
        if (slots.Count == 0) return;
        var t = Current.Albums;
        uint group = (uint)AlbumFields.DiscoCard;
        var known = new uint[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            int slot = slots[i];
            known[i] = t.Known[slot];
            t.Known[slot] &= ~group;
            t.Asked[slot] |= group;
            StageSeat(t, slot, ArtistCardsStamp);
            t.Version[slot]++;
        }
        s_artistCards = (slots.ToArray(), known);
        t.MarkDirty();
    }

    /// <summary>The landing of <see cref="StageArtistCards"/>: the card fields back, as the rows' answers land them. Nothing when none is staged.</summary>
    public static void RestoreArtistCards()
    {
        if (s_artistCards is not { } c) return;
        s_artistCards = null;
        var t = Current.Albums;
        for (int i = 0; i < c.Slots.Length; i++)
        {
            int slot = c.Slots[i];
            if (slot <= Table.None || slot >= t.Count) continue;
            t.Known[slot] = c.Known[i];
            LandSeat(t, slot);
            t.Version[slot]++;
        }
        t.MarkDirty();
        Current.PublishAll();
    }

    /// <summary>The landing of <see cref="StageArtistOverview"/>'s overview alone: the artist's known bits back (the cards stay staged).</summary>
    public static void RestoreArtistOverview()
    {
        if (s_artistOverview is not { } o) return;
        s_artistOverview = null;
        var t = Current.Artists;
        t.Known[o.Slot] = o.Known;
        t.Version[o.Slot]++;
        t.MarkDirty();
        Current.PublishAll();
    }

    /// <summary>The request stamp of the staged chart rows' fetch (cleared by the landing).</summary>
    const uint ChartLateStamp = 0xC4A7u;

    /// <summary>Take the charted rows' <see cref="ArtistReadiness.ChartRowGroup"/> away from <paramref name="artist"/>'s popular list: unknown,
    /// asked and in flight, so the chart gate reads "still coming". False when the artist has no chart.</summary>
    static bool StageChartLate(int artist)
    {
        var targets = Current.Edges.ArtistPopular.Targets(artist);
        if (targets.Length == 0) return false;
        var t = Current.Tracks;
        uint group = ArtistReadiness.ChartRowGroup;
        var slots = targets.ToArray();
        var known = new uint[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            known[i] = t.Known[slot];
            t.Known[slot] &= ~group;
            t.Asked[slot] |= group;
            StageSeat(t, slot, ChartLateStamp);
            t.Version[slot]++;
        }
        s_chartLate = (slots, known);
        t.MarkDirty();
        Current.PublishAll();
        return true;
    }

    /// <summary>The landing of <see cref="StageChartLate"/>: the rows' fields back, as an answer lands them. Nothing when none is staged.</summary>
    static void RestoreChartLate()
    {
        if (s_chartLate is not { } c) return;
        s_chartLate = null;
        var t = Current.Tracks;
        for (int i = 0; i < c.Slots.Length; i++)
        {
            int slot = c.Slots[i];
            if (slot <= Table.None || slot >= t.Count) continue;
            t.Known[slot] = c.Known[i];
            LandSeat(t, slot);
            t.Version[slot]++;
        }
        t.MarkDirty();
        Current.PublishAll();
    }

    /// <summary>A staged request naming <paramref name="slot"/>: the fixture seats the row as a real request does (<see cref="Table.Seat"/>, the in-flight
    /// stamp's only writer), once — a stage run twice does not stack seats.</summary>
    static void StageSeat(Table t, int slot, uint stamp)
    {
        if (t.Outstanding[slot] == 0) t.Seat(slot, stamp);
    }

    /// <summary>The staged request's landing: every seat it took given back (<see cref="Table.Unseat"/>: the stamp clears and the table publishes).</summary>
    static void LandSeat(Table t, int slot)
    {
        while (t.Outstanding[slot] > 0) t.Unseat(slot);
    }

    /// <summary>The header <see cref="StageHead"/> took away, back in place (the landing); nothing when none is staged.</summary>
    public static void RestoreHead()
    {
        RestoreChartLate();
        RestoreArtistCards();
        RestoreArtistOverview();
        if (s_head is not { } h) return;
        s_head = null;
        h.Title[h.Slot] = h.Value;
        h.Table.Known[h.Slot] = h.Known;
        h.Table.Version[h.Slot]++;
        h.Table.MarkDirty();
        Current.PublishAll();
    }

    /// <summary>Stage 1: the membership lands, Complete; rows 1 and 4 are pool tracks that know their face, the rest fresh rows still in flight.</summary>
    public static void StageColdRows()
    {
        int list = ResolveSeedSlot(EntityKind.Playlist, FakeColdPlaylistUri);
        if (list == Table.None) return;
        var targets = new int[ColdRows];
        var payload = new PlaylistTrackEdge[ColdRows];
        uint stamp = 0xC01Du;
        for (int k = 0; k < ColdRows; k++)
        {
            bool warm = Array.IndexOf(s_coldWarm, k) >= 0;
            targets[k] = ResolveSeedSlot(EntityKind.Track, warm ? TrackUri(k + 7) : ColdTrackUri(k));
            payload[k] = new PlaylistTrackEdge(StringId.Empty, 0, Table.None, 0, 0, 0, 0);
            if (!warm && targets[k] != Table.None) StageSeat(Current.Tracks, targets[k], stamp);
        }
        RestoreColdHead();
        Current.Edges.PlaylistTracks.Replace(list, targets, payload, EdgeState.Complete, ColdRows);
        Current.PublishAll();
    }

    /// <summary>Stage 1b, instead of <see cref="StageColdRows"/> (`state cold-warm`): the membership lands with every row restored from the
    /// disk cache: each knows its FACE (title, album, cover, duration) but not its credits (<c>TrackShape.PersistedIdentity</c> withholds
    /// Artists), and the re-ask for them is in flight. `cold-faces` then lands the credits.</summary>
    public static void StageColdWarm()
    {
        StageColdRows();
        var staging = Staging.Rent();
        try
        {
            for (int k = 0; k < ColdRows; k++)
            {
                if (Array.IndexOf(s_coldWarm, k) >= 0) continue;
                int i = k + 7;
                ref var row = ref staging.Tracks.RowFor(staging.AddText(Utf8(ColdTrackUri(k))), Authority.Seed,
                    (uint)(TrackFields.Face & ~TrackFields.Artists));
                row.Title = staging.AddText(Utf8(s_titles[Wrap(i, s_titles.Length)]));
                row.Image = staging.AddText(Utf8(Cover(i)));
                row.AlbumUri = staging.AddText(Utf8(AlbumUri(i % AlbumCount)));
                row.DurationMs = 138_000 + (i * 37 % 150) * 1000;
            }
            Commit(staging);
        }
        finally
        {
            Staging.Return(staging);
        }
        uint stamp = 0xC01Eu;
        for (int k = 0; k < ColdRows; k++)
        {
            if (Array.IndexOf(s_coldWarm, k) >= 0) continue;
            int slot = Current.Tracks.Slot(ColdTrackUri(k).AsSpan());
            if (slot != Table.None) StageSeat(Current.Tracks, slot, stamp);
        }
        Current.PublishAll();
    }

    /// <summary>`state regen`: the Mix's fresh answer lands over the copy the page holds: another cover and description, and a new revision whose
    /// rows are other tracks (each knows its face). The membership's version moves, so the open's revalidation sees it MOVED and lets go.</summary>
    public static void StageRegen()
    {
        int list = ResolveSeedSlot(EntityKind.Playlist, FakeColdPlaylistUri);
        if (list == Table.None) return;
        s_regenEpoch++;
        StageRegenHead(list, "regen");
        var targets = new int[ColdRows];
        var payload = new PlaylistTrackEdge[ColdRows];
        for (int k = 0; k < ColdRows; k++)
        {
            targets[k] = ResolveSeedSlot(EntityKind.Track, TrackUri(Wrap(k * 7 + 3 + s_regenEpoch, 60)));
            payload[k] = new PlaylistTrackEdge(StringId.Empty, 0, Table.None, 0, 0, 0, 0);
        }
        Current.Edges.PlaylistTracks.Replace(list, targets, payload, EdgeState.Complete, ColdRows);
        Current.PublishAll();
    }

    static int s_regenEpoch;

    /// <summary>A regen's head: another cover, the description alternating per epoch (the short line, then one long enough to wrap to three
    /// lines in a 216 DIP rail, so every regen exercises the description's hand-over and its height) and a new revision.</summary>
    static void StageRegenHead(int list, string tag)
    {
        var t = Current.Playlists;
        RetainText(ref t.Image[list], Intern(Utf8(Cover(17 + s_regenEpoch * 5))));
        RetainText(ref t.Description[list], Intern(Utf8(s_regenEpoch % 2 == 1
            ? "Suzan & Freek, sombr, Conan Gray and more"
            : "The songs you keep coming back to, from Suzan & Freek, sombr and Conan Gray to the quieter ones in between.")));
        RetainText(ref t.Revision[list], Intern(Utf8("9" + s_regenEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + tag)));
        t.Version[list]++;
        t.MarkDirty();
    }

    /// <summary>`state regen-edit`: a small remote edit over the membership the page shows (another device): display rows 2, 5 and 6 leave, row 9
    /// moves to row 3, and new tracks land at rows 1, 4 and 30 (the last below the first screen), taken from `tr70`..`tr130` (neither pl6's seed
    /// nor `regen` uses them); the count stays. A new cover, the alternating description and a new revision ride it.</summary>
    public static void StageRegenEdit()
    {
        int list = ResolveSeedSlot(EntityKind.Playlist, FakeColdPlaylistUri);
        if (list == Table.None) return;
        var current = Current.Edges.PlaylistTracks.Targets(list);
        if (current.Length < 30) return;
        s_regenEpoch++;
        StageRegenHead(list, "edit");
        // Rows 2, 5 and 6 leave and row 9 moves; the rest keep their order around the four placed rows (final rows 1, 3, 4 and 30).
        var rest = new List<int>(current.Length);
        for (int i = 0; i < current.Length; i++)
            if (i != 1 && i != 4 && i != 5 && i != 8) rest.Add(current[i]);
        int moved = current[8];
        var rows = new int[rest.Count + 4];
        for (int pos = 0, next = 0; pos < rows.Length; pos++)
            rows[pos] = pos switch
            {
                0 => Fresh(0),        // a new row 1
                2 => moved,           // row 9 → row 3
                3 => Fresh(1),        // a new row 4
                29 => Fresh(2),       // a new row 30
                _ => rest[next++],
            };
        var payload = new PlaylistTrackEdge[rows.Length];
        for (int k = 0; k < payload.Length; k++) payload[k] = new PlaylistTrackEdge(StringId.Empty, 0, Table.None, 0, 0, 0, 0);
        Current.Edges.PlaylistTracks.Replace(list, rows, payload, EdgeState.Complete, rows.Length);
        Current.PublishAll();

        static int Fresh(int j) => ResolveSeedSlot(EntityKind.Track, TrackUri(70 + Wrap(s_regenEpoch * 3 + j, 61)));
    }

    /// <summary>`state rescope`: a login's scope switch (<c>Spotify.Session.AdoptWelcome</c>: the welcome names another market or tier than the
    /// scope the app booted on). A new, EMPTY table set replaces the one every page is bound to, the freshness stamps of the old one are
    /// forgotten (the library's publish hook does that in a real session) and the deck re-binds: a page showing a list now has an unknown row
    /// with nothing asked. `rescope-land` then lands the seed in the new set as the disk warm-up lands a restored list: whole, and never
    /// revalidated in this scope. The owner's log, 2026-11-15: a playlist restored at launch was opened before the session was online, the
    /// welcome switched the scope under it, and the page held its list on the loading arm for minutes.</summary>
    public static void StageRescope()
    {
        // Switch's own steps but one: the retired set's text is NOT handed back (the inspector leaks it for the life of the process). The
        // page's last frames still draw the old set's ids, and a Debug build asserts on an id taken again inside its quarantine.
        var key = Current.Key;
        uint epoch = Current.Epoch + 1;
        s_dirty.Clear();
        Current = new Scope(key with { Market = key.Market == "ZZ" ? "US" : "ZZ" }) { Epoch = epoch };
        ResolveMe(Current);
        StoreWarm(Current);
        BumpScopeEpoch();
        ListStamps.ForgetAll();
        Playback.Rebind();
        Current.PublishAll();
    }

    /// <summary>`state rescope-land`, a few frames after `rescope`: the seed lands in the new table set, the restored list Complete as from
    /// disk.</summary>
    public static void StageRescopeLand() => SeedFake(Platform.Clock.SeedEpoch);

    /// <summary>Stage 2: every cold row's own fields land at once (one batch answer), the in-flight stamps clear.</summary>
    public static void StageColdFaces()
    {
        var staging = Staging.Rent();
        try
        {
            for (int k = 0; k < ColdRows; k++)
            {
                if (Array.IndexOf(s_coldWarm, k) >= 0) continue;
                int i = k + 7;
                ref var row = ref staging.Tracks.RowFor(staging.AddText(Utf8(ColdTrackUri(k))), Authority.Seed,
                    (uint)(TrackFields.Identity | TrackFields.Availability | TrackFields.Audio));
                row.Title = staging.AddText(Utf8(s_titles[Wrap(i, s_titles.Length)]));
                row.ArtistLine = staging.AddText(Utf8(s_artistNames[Wrap(i, s_artistNames.Length)]));
                row.Image = staging.AddText(Utf8(Cover(i)));
                row.AlbumUri = staging.AddText(Utf8(AlbumUri(i % AlbumCount)));
                row.DurationMs = 138_000 + (i * 37 % 150) * 1000;
                row.Tempo = (ushort)((70 + i % 90) * 10);
                row.Camelot = (byte)(1 + Wrap(i, 12));
                row.CamelotColor = s_camelotColors[Wrap(i, 12)];
            }
            Commit(staging);
        }
        finally
        {
            Staging.Return(staging);
        }
        // The credits behind the line (the TrackArtists run a real Identity answer carries): the row's artist links, one artist per row,
        // the one its ArtistLine names.
        Span<int> credit = stackalloc int[1];
        for (int k = 0; k < ColdRows; k++)
        {
            if (Array.IndexOf(s_coldWarm, k) >= 0) continue;
            int slot = Current.Tracks.Slot(ColdTrackUri(k).AsSpan());
            if (slot == Table.None) continue;
            LandSeat(Current.Tracks, slot);
            credit[0] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(Wrap(k + 7, s_artistNames.Length)));
            if (credit[0] != Table.None) Current.Edges.TrackArtists.ReplaceRun(slot, credit, default);
        }
        Current.PublishAll();
    }
}
