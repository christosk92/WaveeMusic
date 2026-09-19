// ── Wavee.Tests/ShellHostTests.cs — the three documents the shell persists ────────────────────────────────────────
//
// Wave 4's gate for `Shell/Shell.Host.cs`'s pure half: the pinned-tab codec, `history.json`'s retention + its
// dead-seed purge, and `play-log.json` + its `play-recency.json` sidecar. Ported from
// _old/Wavee.Tests/WorkspaceTabsPersistenceTests.cs (G6); everything else here is NEW, because ch 16 §8 records the
// history store's cap, its purge and its persistence shape as untested in 0.2.9 and asks for the tests with the port.
//
// NO WINDOW AND NO ENGINE LOOP. These facts drive the stores over a temp directory and read the IN-MEMORY result; the
// debounced write is a background concern and is deliberately not raced here.
//
// The three that each describe a real failure:
//
//   A DEAD-SEED PURGE THAT DOES NOT REWRITE THE FILE RESURRECTS ITSELF. 0.2.9 wrote a demo history seed under
//   `pl:local:` on a fresh install; those uris address NOTHING, so every such row is a dead destination the page would
//   still offer. Filtering at render time leaves the log carrying them forever.
//
//   A TOO-NEW DOCUMENT BLOCKS WRITES. A newer build owns `session.json`; a corrupt or future file must never be
//   replaced by an empty one.
//
//   THE RECENCY SIDECAR IS MAX-MERGE. A stamp never moves backwards, so a server history older than a local play
//   cannot demote an artist you just listened to.

using Xunit;

namespace Wavee.Tests;

[CollectionDefinition(ShellHostCollection.Name, DisableParallelization = true)]
public sealed class ShellHostCollection
{
    public const string Name = "shell-host";
}

public class WorkspaceTabsPersistenceTests
{
    [Fact]
    public void An_empty_or_garbage_value_decodes_to_an_empty_set()
    {
        Assert.Empty(Shell.WorkspaceTabs.Decode(null).Tabs);
        Assert.Empty(Shell.WorkspaceTabs.Decode("").Tabs);
        Assert.Empty(Shell.WorkspaceTabs.Decode("not json").Tabs);
        Assert.Equal(-1, Shell.WorkspaceTabs.Decode(null).LastSelected);
    }

    [Fact]
    public void A_round_trip_preserves_order_and_the_selection()
    {
        Shell.PersistedTab[] tabs =
        [
            new("home", null),
            new("album:spotify:album:1TSZDcvlPtAnekTaItI3qO", "RAM"),
        ];
        var decoded = Shell.WorkspaceTabs.Decode(Shell.WorkspaceTabs.Encode(tabs, 1));
        Assert.Equal(2, decoded.Tabs.Length);
        Assert.Equal("home", decoded.Tabs[0].Route);
        Assert.Equal("RAM", decoded.Tabs[1].Arg);
        Assert.Equal(1, decoded.LastSelected);
    }

    [Fact]
    public void A_document_from_a_different_version_decodes_to_nothing()
    {
        string json = Shell.WorkspaceTabs.Encode([new Shell.PersistedTab("home", null)], 0)
            .Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal);
        Assert.Empty(Shell.WorkspaceTabs.Decode(json).Tabs);
    }

    [Fact]
    public void The_tab_cap_bounds_both_directions()
    {
        var many = new Shell.PersistedTab[Shell.WorkspaceTabs.MaxTabs + 40];
        for (int i = 0; i < many.Length; i++) many[i] = new Shell.PersistedTab("search", "q" + i);
        var decoded = Shell.WorkspaceTabs.Decode(Shell.WorkspaceTabs.Encode(many, 0));
        Assert.Equal(Shell.WorkspaceTabs.MaxTabs, decoded.Tabs.Length);
    }

    [Fact]
    public void An_over_long_route_or_arg_is_dropped_rather_than_persisted()
    {
        Shell.PersistedTab[] tabs =
        [
            new(new string('r', Shell.WorkspaceTabs.MaxRouteChars + 1), null),
            new("home", new string('a', Shell.WorkspaceTabs.MaxArgChars + 1)),
            new("browse", null),
        ];
        var decoded = Shell.WorkspaceTabs.Decode(Shell.WorkspaceTabs.Encode(tabs, 0));
        Assert.Single(decoded.Tabs);
        Assert.Equal("browse", decoded.Tabs[0].Route);
    }

    [Fact]
    public void A_selection_past_the_end_falls_back_to_the_first_pin()
    {
        var decoded = Shell.WorkspaceTabs.Decode(Shell.WorkspaceTabs.Encode([new Shell.PersistedTab("home", null)], 7));
        Assert.Equal(0, decoded.LastSelected);
    }

    [Fact]
    public void An_empty_set_has_no_selection()
        => Assert.Equal(-1, Shell.WorkspaceTabs.Decode(Shell.WorkspaceTabs.Encode([], 0)).LastSelected);
}

[Collection(ShellHostCollection.Name)]
public class ShellHistoryStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-hist-" + Guid.NewGuid().ToString("N")[..8]);

    public ShellHistoryStoreTests()
    {
        Directory.CreateDirectory(_dir);
        Shell.History.Store.UsePath(Path.Combine(_dir, "history.json"));
        Shell.History.Store.Load();
        Shell.History.Store.Clear();
    }

    public void Dispose()
    {
        Shell.History.Store.Clear();
        // Clear deletes the file on the pool: a delete still pending there answers access-denied, not an IOException.
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_ring_is_capped_fifo()
    {
        for (int i = 0; i < Shell.History.MaxEntries + 25; i++) Shell.History.Store.Add(Shell.Parse("search", "q" + i));
        Assert.Equal(Shell.History.MaxEntries, Shell.History.Store.Entries.Count);
        // The OLDEST were evicted, so the first surviving row is q25.
        Assert.Equal("q25", Shell.ArgOf(Shell.History.Store.Entries[0].Route));
    }

    [Fact]
    public void A_not_found_route_is_never_logged()
    {
        Shell.History.Store.Add(Shell.Parse("nonsense"));
        Assert.Empty(Shell.History.Store.Entries);
    }

    [Fact]
    public void The_version_bumps_on_every_mutation_so_the_page_rebuilds()
    {
        int before = Shell.History.Store.Version.Peek();
        Shell.History.Store.Add(Shell.Parse("home"));
        Assert.True(Shell.History.Store.Version.Peek() > before);
    }

    [Fact]
    public void The_dead_seed_is_purged_on_load_so_the_log_stops_carrying_it()
    {
        // The removed 0.2.9 demo seed: `pl:local:…` addresses nothing, so those rows are dead destinations the page
        // would still offer.
        string path = Path.Combine(_dir, "history.json");
        long ticks = DateTime.UtcNow.Ticks;
        File.WriteAllText(path,
            "[{\"name\":\"pl:local:demo\",\"arg\":\"Demo\",\"ticksUtc\":" + ticks + "}," +
            "{\"name\":\"home\",\"ticksUtc\":" + ticks + "}]");

        Shell.History.Store.UsePath(path);
        Shell.History.Store.Load();
        Assert.Single(Shell.History.Store.Entries);
        Assert.Equal(Shell.RouteKind.Home, Shell.History.Store.Entries[0].Route.Kind);
    }

    [Fact]
    public void A_corrupt_file_starts_empty_rather_than_throwing()
    {
        string path = Path.Combine(_dir, "history.json");
        File.WriteAllText(path, "{ this is not an array");
        Shell.History.Store.UsePath(path);
        Shell.History.Store.Load();
        Assert.Empty(Shell.History.Store.Entries);
    }

    [Fact]
    public void A_persisted_row_round_trips_through_utc_ticks()
    {
        // The kind does not round-trip on a DateTime, and a local time written on one side of a DST boundary and read
        // on the other moves an entry into the wrong day group.
        string path = Path.Combine(_dir, "history.json");
        var utc = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(path,
            "[{\"name\":\"album:spotify:album:1TSZDcvlPtAnekTaItI3qO\",\"arg\":\"RAM\",\"ticksUtc\":" + utc.Ticks + "}]");
        Shell.History.Store.UsePath(path);
        Shell.History.Store.Load();

        var entry = Assert.Single(Shell.History.Store.Entries);
        Assert.Equal(Shell.RouteKind.Album, entry.Route.Kind);
        Assert.Equal("RAM", Shell.ArgOf(entry.Route));
        Assert.Equal(utc, entry.VisitedAt.ToUniversalTime());
        Assert.Equal(DateTimeKind.Local, entry.VisitedAt.Kind);
    }
}

[Collection(ShellHostCollection.Name)]
public class ShellPlayLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-playlog-" + Guid.NewGuid().ToString("N")[..8]);

    static EntityUri Track(string id) => EntityUri.Parse("spotify:track:" + id.PadRight(22, 'a'));
    static EntityUri Album(string id) => EntityUri.Parse("spotify:album:" + id.PadRight(22, 'b'));

    public ShellPlayLogTests()
    {
        Directory.CreateDirectory(_dir);
        Shell.PlayLog.UsePath(Path.Combine(_dir, "play-log.json"));
        Shell.PlayLog.Clear();
    }

    public void Dispose()
    {
        Shell.PlayLog.Clear();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_push_storm_at_a_track_edge_does_not_fill_the_ring()
    {
        // Idempotent at the boundary: the SAME (track, context) pair within one second is the same play.
        Assert.True(Shell.PlayLog.Append(Track("x"), Album("a"), atMs: 10_000));
        Assert.False(Shell.PlayLog.Append(Track("x"), Album("a"), atMs: 10_500));
        Assert.True(Shell.PlayLog.Append(Track("x"), Album("a"), atMs: 11_500));
        Assert.Equal(2, Shell.PlayLog.Entries.Count);
    }

    [Fact]
    public void The_ring_is_capped_fifo()
    {
        for (int i = 0; i < Shell.PlayLog.MaxEntries + 10; i++)
            Shell.PlayLog.Append(Track("t" + i), default, atMs: 1000L + i * 2000L);
        Assert.Equal(Shell.PlayLog.MaxEntries, Shell.PlayLog.Entries.Count);
    }

    [Fact]
    public void A_track_with_no_uri_is_refused()
        => Assert.False(Shell.PlayLog.Append(default, default, atMs: 1000));

    [Fact]
    public void One_play_stamps_everything_it_names()
    {
        var track = Track("x");
        var album = Album("a");
        var artist = EntityUri.Parse("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi");
        Shell.PlayLog.Append(track, album, atMs: 5000, artists: [artist]);

        Assert.Equal(5000L, Shell.PlayLog.Recency[track.Text]);
        Assert.Equal(5000L, Shell.PlayLog.Recency[album.Text]);
        Assert.Equal(5000L, Shell.PlayLog.Recency[artist.Text]);
    }

    [Fact]
    public void The_recency_index_is_max_merge_so_a_stale_server_history_cannot_demote_a_local_play()
    {
        var track = Track("x");
        Shell.PlayLog.Append(track, default, atMs: 9000);
        Assert.False(Shell.PlayLog.MergeRecency(new Dictionary<string, long> { [track.Text] = 1000 }));
        Assert.Equal(9000L, Shell.PlayLog.Recency[track.Text]);

        Assert.True(Shell.PlayLog.MergeRecency(new Dictionary<string, long> { [track.Text] = 12_000 }));
        Assert.Equal(12_000L, Shell.PlayLog.Recency[track.Text]);
    }

    [Fact]
    public void Recent_contexts_collapse_to_the_context_newest_first_and_deduped()
    {
        Shell.PlayLog.Append(Track("t1"), Album("a"), atMs: 1000);
        Shell.PlayLog.Append(Track("t2"), Album("b"), atMs: 3000);
        Shell.PlayLog.Append(Track("t3"), Album("a"), atMs: 5000);

        var rows = Shell.PlayLog.RecentContexts(8);
        Assert.Equal(2, rows.Length);
        Assert.Equal(Shell.NameOf(Shell.For(Album("a"))), rows[0].Route);   // newest first
        Assert.Equal(Shell.NameOf(Shell.For(Album("b"))), rows[1].Route);
    }

    [Fact]
    public void The_two_jump_list_halves_share_one_key_space()
    {
        // A surface both PLAYED and VISITED must appear once, and the whole dedupe rests on the two halves composing
        // the SAME route string.
        var album = Album("a");
        Shell.PlayLog.Append(Track("t1"), album, atMs: 1000);
        var played = Shell.PlayLog.RecentContexts(6);

        var visited = Shell.History.RecentSurfaces(
            [new Shell.HistoryEntry(Shell.For(album, "A"), DateTime.Now)], 6);

        Assert.Equal(played[0].Route, visited[0].Route);
    }

    [Fact]
    public void Clear_drops_both_halves_because_they_can_diverge()
    {
        Shell.PlayLog.Append(Track("x"), default, atMs: 1000);
        Shell.PlayLog.MergeRecency(new Dictionary<string, long> { ["spotify:artist:4tZwfgrHOc3mvqYlEYSvVi"] = 2000 });
        Shell.PlayLog.Clear();
        Assert.Empty(Shell.PlayLog.Entries);
        Assert.Empty(Shell.PlayLog.Recency);
    }
}

[Collection(ShellHostCollection.Name)]
public class ShellSessionDocumentTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-session-" + Guid.NewGuid().ToString("N")[..8]);

    public ShellSessionDocumentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_missing_file_is_a_first_run_and_never_throws()
    {
        Shell.Session.UsePath(Path.Combine(_dir, "nothing-here.json"));
        Shell.Session.Load();
        Assert.False(Shell.Session.WritesBlocked);
    }

    [Fact]
    public void A_corrupt_file_is_left_in_place_and_writes_stay_enabled()
    {
        string path = Path.Combine(_dir, "session.json");
        File.WriteAllText(path, "{{{ not json");
        Shell.Session.UsePath(path);
        Shell.Session.Load();
        Assert.False(Shell.Session.WritesBlocked);   // the first successful save replaces it
        Assert.True(File.Exists(path));              // …and nothing deleted the user's file in the meantime
    }

    [Fact]
    public void The_shell_section_round_trips_and_is_equality_gated()
    {
        Shell.Session.UsePath(Path.Combine(_dir, "session.json"));
        Shell.Session.Load();
        Shell.Session.CaptureShell(railOpen: true, railMode: (int)Shell.RailMode.Queue);
        var section = Shell.Session.ShellSection;
        Assert.NotNull(section);
        Assert.True(section!.RailOpen);
        Assert.Equal((int)Shell.RailMode.Queue, section.RailMode);

        // The shell's own effect also runs once at mount after adopting the saved values, so an identical write must
        // be a no-op rather than a second debounced save.
        Shell.Session.CaptureShell(railOpen: true, railMode: (int)Shell.RailMode.Queue);
        Assert.Same(section, Shell.Session.ShellSection);
    }

    [Fact]
    public void Both_stacks_are_capped_in_the_document_too()
        => Assert.Equal(50, Shell.Session.MaxStack);
}

public class ShellUiStateTests
{
    [Fact]
    public void Clicking_the_showing_mode_closes_the_rail()
    {
        Shell.Ui.RailOpen.Value = false;
        Shell.Ui.Mode.Value = Shell.RailMode.Lyrics;

        Shell.Ui.Toggle(Shell.RailMode.Queue);
        Assert.True(Shell.Ui.RailOpen.Peek());
        Assert.Equal(Shell.RailMode.Queue, Shell.Ui.Mode.Peek());

        Shell.Ui.Toggle(Shell.RailMode.Queue);
        Assert.False(Shell.Ui.RailOpen.Peek());

        // A DIFFERENT mode while open switches rather than closing.
        Shell.Ui.Toggle(Shell.RailMode.Queue);
        Shell.Ui.Toggle(Shell.RailMode.Lyrics);
        Assert.True(Shell.Ui.RailOpen.Peek());
        Assert.Equal(Shell.RailMode.Lyrics, Shell.Ui.Mode.Peek());
    }

    [Fact]
    public void The_resting_active_stage_playable_is_the_empty_string_and_not_null()
        => Assert.Equal("", Shell.Ui.ActiveStagePlayable.Peek());

    [Fact]
    public void The_docked_cap_starts_at_the_sixteen_by_nine_floor_of_the_default_rail()
        => Assert.Equal(Shell.DockedVideoNaturalH(Shell.RailDefaultW), Shell.Ui.DockedVideoHeight.Peek(), 3);
}
