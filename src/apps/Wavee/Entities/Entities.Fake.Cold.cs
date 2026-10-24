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

    // ── any detail page's header, unknown in place (`state kind=head uri=<uri>`, then `state kind=head-land`) ──
    // An album's, a list's, a show's or an episode's title is taken away with the bit that says it is known, so the page draws its pending head; the
    // landing puts both back on the page that shows it, so a trace sees the head land as the real client's first read does (audit B7 r2).

    /// <summary>The header <see cref="StageHead"/> took away: the table, the slot, the title and the known bit.</summary>
    static (Table Table, Column<StringId> Title, int Slot, StringId Value, uint Bit)? s_head;

    /// <summary>Take the header of the album, playlist, show or episode <paramref name="uri"/> away (its title and the known bit the page's
    /// HeaderPending reads). False for another kind or an unknown uri.</summary>
    public static bool StageHead(string uri)
    {
        RestoreHead();
        var kind = EntityUri.KindOf(uri.AsSpan());
        int slot = ResolveSeedSlot(kind, uri);
        if (slot == Table.None) return false;
        Scope s = Current;
        (Table Table, Column<StringId> Title, uint Bit)? head = kind switch
        {
            EntityKind.Album => (s.Albums, s.Albums.Title, (uint)AlbumFields.Title),
            EntityKind.Playlist => (s.Playlists, s.Playlists.Title, (uint)PlaylistFields.Identity),
            EntityKind.Show => (s.Shows, s.Shows.Title, (uint)ShowFields.Title),
            EntityKind.Episode => (s.Episodes, s.Episodes.Title, (uint)EpisodeFields.Title),
            _ => null,
        };
        if (head is not { } h) return false;
        s_head = (h.Table, h.Title, slot, h.Title[slot], h.Bit);
        h.Title[slot] = StringId.Empty;
        h.Table.Known[slot] &= ~h.Bit;
        h.Table.Version[slot]++;
        s.PublishAll();
        return true;
    }

    /// <summary>The header <see cref="StageHead"/> took away, back in place (the landing); nothing when none is staged.</summary>
    public static void RestoreHead()
    {
        if (s_head is not { } h) return;
        s_head = null;
        h.Title[h.Slot] = h.Value;
        h.Table.Known[h.Slot] |= h.Bit;
        h.Table.Version[h.Slot]++;
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
            if (!warm && targets[k] != Table.None) Current.Tracks.Inflight[targets[k]] = stamp;
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
            if (slot != Table.None) Current.Tracks.Inflight[slot] = stamp;
        }
        Current.PublishAll();
    }

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
            Current.Tracks.Inflight[slot] = 0;
            credit[0] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(Wrap(k + 7, s_artistNames.Length)));
            if (credit[0] != Table.None) Current.Edges.TrackArtists.ReplaceRun(slot, credit, default);
        }
        Current.PublishAll();
    }
}
