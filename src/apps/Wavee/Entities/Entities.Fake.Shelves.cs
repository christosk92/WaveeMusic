// ── Entities/Entities.Fake.Shelves.cs — the inspector's staged LIBRARY shelves: a library tab cold (loading) or empty ─────────────────────
//
// Every --fake library relation (saved albums, followed artists, saved shows, likes) is seeded whole at boot, so the Library page never
// shows its loading skeleton or its empty state. The inspector stages one (`state kind=loading|empty page=albums|artists|podcasts|
// audiobooks|library`, Screens/Diagnostics.Inspect.cs): the account's relations behind that tab are written back to Unknown (nothing
// answered: the navigator's skeleton, as on a cold start) or to Complete with no rows (the tab's empty state), and the next mount puts the
// boot's rows back (`RestoreStagedShelves`). The Artists tab is three relations (followed artists, and the artists of the saved albums and
// the likes), so it stages all three; Podcasts and Audiobooks split one relation (saved shows) and stage it whole.
//
// Inspector-only. Under --fake an Unknown relation the page asks for again stays in flight (the wire refuses, the ask never answers), as the
// cold playlist does (Entities.Fake.Cold.cs).

using FluentGpu.Foundation;

namespace Wavee;

public static partial class Entities
{
    /// <summary>One relation as the boot left it, so the next mount puts it back.</summary>
    sealed record StagedShelf(EdgeTable<LibraryEdge> Table, int Parent, int[] Targets, LibraryEdge[] Payload, EdgeState State, int Total);

    static readonly List<StagedShelf> s_stagedShelves = [];

    /// <summary>The account relations behind a library tab (`albums`, `artists`, `podcasts`, `audiobooks`), or empty for any other word.</summary>
    static EdgeTable<LibraryEdge>[] ShelvesOf(string tab)
    {
        Edges e = Current.Edges;
        return tab switch
        {
            "albums" => [e.SavedAlbums],
            "artists" => [e.FollowedArtists, e.SavedAlbums, e.Liked],
            "podcasts" or "audiobooks" => [e.SavedShows],
            _ => [],
        };
    }

    /// <summary>Stage the library tab <paramref name="tab"/>: its relations Unknown (<paramref name="loading"/>) or Complete with no rows.
    /// False for a word that is not a library tab or with no account row.</summary>
    public static bool StageLibraryShelf(string tab, bool loading)
    {
        int me = Current.MeSlot;
        EdgeTable<LibraryEdge>[] tables = ShelvesOf(tab);
        if (me <= Table.None || tables.Length == 0) return false;
        foreach (EdgeTable<LibraryEdge> t in tables)
        {
            bool remembered = false;
            foreach (StagedShelf s in s_stagedShelves) remembered |= ReferenceEquals(s.Table, t) && s.Parent == me;
            if (!remembered)
                s_stagedShelves.Add(new StagedShelf(t, me, t.Targets(me).ToArray(), t.Payload(me).ToArray(), t.State(me), t.Total(me)));
            t.Replace(me, ReadOnlySpan<int>.Empty, ReadOnlySpan<LibraryEdge>.Empty, loading ? EdgeState.Unknown : EdgeState.Complete, 0);
        }
        Current.PublishAll();
        return true;
    }

    /// <summary>Every staged shelf back as the boot left it (the next mount). Nothing when none was staged.</summary>
    public static void RestoreStagedShelves()
    {
        if (s_stagedShelves.Count == 0) return;
        foreach (StagedShelf s in s_stagedShelves) s.Table.Replace(s.Parent, s.Targets, s.Payload, s.State, s.Total);
        s_stagedShelves.Clear();
        Current.PublishAll();
    }
}
