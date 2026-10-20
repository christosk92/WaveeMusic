// ── Entities/Entities.Fake.Cold.cs — the inspector's COLD playlist: a list that loads the way a real one does (P6 playlist-load lane) ──────────────
//
// Every --fake list is seeded whole at boot, so no fake page ever shows its loading choreography. The inspector stages one (`state kind=cold-list`,
// `cold-rows`, `cold-faces`), one step per command, so an agent can trace each frame of the transition:
//
//   · cold-list   the playlist's title and cover are known, its length and membership are NOT (the meta line loading, the rows' shimmer);
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
        for (int k = 0; k < ColdRows; k++)
        {
            if (Array.IndexOf(s_coldWarm, k) >= 0) continue;
            int slot = Current.Tracks.Slot(ColdTrackUri(k).AsSpan());
            if (slot != Table.None) Current.Tracks.Inflight[slot] = 0;
        }
        Current.PublishAll();
    }
}
