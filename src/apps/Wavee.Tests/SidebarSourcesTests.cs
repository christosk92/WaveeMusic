// ── Wavee.Tests/SidebarSourcesTests.cs — the data-source CONTRACT, plus the nine first-party edge-read sources ──────
//
// Ported from `src/apps/_old/Wavee.Tests/SidebarDataSourceTests.cs` (0.2.9) onto 0.3's `Shell/Sidebar.cs` (the
// contract) and `Shell/Sidebar.Host.cs` (the nine sources). Three 0.3 changes drive every diff from the old file:
//
//   1. A source is an EDGE READ, not a fetching service. 0.2.9's `LibraryStore` / `PlaybackBridge` /
//      `IWhatsNewService` / `IConcertService` are gone. `SidebarQueueSource` reads `Queue.UpNext` off the live
//      `Entities.Current` queue edges; `SidebarNowPlayingSource` reads `Playback.Snap()`; new releases and concerts
//      take an injected delegate seam this file now owns (`SidebarFeedFetch` / `SidebarConcertsFetch`).
//   2. `SidebarSourceMap.FromFeedState` takes a `SidebarFeedState` (0.2.9's `NotificationFeedState`) — same members,
//      same Offline→Ready rule, new name because the vocabulary now lives beside the sources it feeds.
//   3. `SidebarSourceMap.FromEvent` and `.NewReleases` (plus `NewReleaseNotification`/`NewReleaseKind`) are TRIMMED —
//      the file's own header says so. `SidebarConcertsSource` builds its rows straight off the real `Concert` handle
//      via `SidebarLibraryEntry.ForRoute`, and `SidebarNewReleasesSource` resolves its own seam's uris against the
//      projection's `SidebarSourceIndex` — both covered here as source-level facts instead of mapper-level ones.
//
// Facts that only exercise the CONTRACT (schemas, ids, filters, the source map's pure folds, plain
// `SidebarLibraryEntry` values) construct no `Entities` handle and call no `TestScope.Fresh()`. Facts that construct
// a `Track`, `Artist` or `Concert` handle — the mapper facts, and every fact against `SidebarQueueSource`,
// `SidebarArtistTopTracksSource` and `SidebarConcertsSource` — need a live scope, so the whole class joins
// `EntitiesCollection` (D17's fake-scope contract) the way `UserTests`/`TrackTests`/`ConcertTests` do, and only the
// individual facts that need one call `TestScope.Fresh()`.
//
// GAP (documented, not weakened): `SidebarNowPlayingSource.Fill` reads the live, process-static `Playback.Snap()`.
// 0.3's `Playback` exposes no public seam to plant a "current track" short of `Playback.Post(Input.Play(...))` + a
// drain, which runs the real reducer's effects — `Audio.Load`, a PUT-state `Announce` — exactly the network/engine
// touch this assembly must never make (D17, CLAUDE.md). Only the honest empty state ("nothing plays ⇒ zero rows")
// is asserted here; the "exactly one row while something plays" behaviour could not be ported without weakening it
// into a fake that no longer drives the real class, so it is dropped — see the port report.

using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SidebarSourcesTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSourceConfig Config(string json) => new(JsonDocument.Parse(json).RootElement);

    /// <summary>A plain, entity-free row for the CONTRACT-level facts (no scope needed).</summary>
    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string name, SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None)
        => new(id, kind, id, name, "", default, null, 0, 0, 0, 0, 0, 0, false, flavor);

    /// <summary>A live Track handle with a title and up to three named artists — the 0.3 replacement for 0.2.9's
    /// plain `Track` DTO fixture, since <see cref="SidebarSourceMap.FromTrack"/> now takes an Entities handle.
    /// Requires a fresh scope (<see cref="TestScope.Fresh"/>): the handle is a slot into it.</summary>
    static Track Tr(string id, string title, params string[] artistNames)
    {
        var s = Staging.Rent();
        ref var trow = ref s.Tracks.Add();
        trow.Id = s.Text("spotify:track:" + id);
        trow.Title = s.Text(title);
        trow.Known = (uint)TrackFields.Identity;
        trow.Authority = Authority.Full;

        var artistUris = new string[artistNames.Length];
        for (int i = 0; i < artistNames.Length; i++)
        {
            artistUris[i] = "spotify:artist:" + id + "-" + i;
            ref var arow = ref s.Artists.Add();
            arow.Id = s.Text(artistUris[i]);
            arow.Name = s.Text(artistNames[i]);
            arow.Known = (uint)ArtistFields.Identity;
            arow.Authority = Authority.Full;
        }
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:" + id));
        if (artistUris.Length > 0)
        {
            var artistSlots = new int[artistUris.Length];
            for (int i = 0; i < artistUris.Length; i++) artistSlots[i] = Entities.Artist(EntityUri.Parse(artistUris[i])).Slot;
            Entities.Current.Edges.TrackArtists.Replace(track.Slot, artistSlots, [], EdgeState.Complete, artistSlots.Length);
        }
        return track;
    }

    /// <summary>A minimal source: proves the published interface + base class are implementable from outside, and
    /// gives the resolution tests something with configurable health.</summary>
    sealed class StubSource : SidebarDataSourceBase
    {
        readonly List<SidebarLibraryEntry> _rows = new();

        public StubSource(string id) : base(id) { }

        public void SetRows(params string[] ids)
        {
            _rows.Clear();
            for (int i = 0; i < ids.Length; i++) _rows.Add(SidebarLibraryEntry.ForRoute(ids[i], ids[i], i));
        }

        public void Publish(SidebarSourceState state, bool prompt = false) => SetHealth(state, null, prompt);

        public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
        {
            for (int i = 0; i < _rows.Count; i++) into.Add(_rows[i]);
            return _rows.Count;
        }
    }

    /// <summary>A settable double for <see cref="ISidebarProjectionSnapshot"/> — the edge-read sources' one seam.</summary>
    sealed class FakeProjectionSnapshot : ISidebarProjectionSnapshot
    {
        public readonly List<SidebarLibraryEntry> AllList = new();
        public readonly List<SidebarLibraryEntry> TreeList = new();
        public SidebarSourceIndex IndexValue = new();
        public SidebarSourceState LibraryStateValue = SidebarSourceState.Ready;
        public SidebarSourceState TreeStateValue = SidebarSourceState.Ready;
        public readonly List<SidebarVisit> VisitsList = new();
        public readonly List<SidebarPlayedContext> PlayedList = new();

        public IReadOnlyList<SidebarLibraryEntry> All => AllList;
        public IReadOnlyList<SidebarLibraryEntry> Tree => TreeList;
        public SidebarSourceIndex Index => IndexValue;
        public SidebarSourceState LibraryState => LibraryStateValue;
        public SidebarSourceState TreeState => TreeStateValue;
        public IReadOnlyList<SidebarVisit> Visits => VisitsList;
        public IReadOnlyList<SidebarPlayedContext> Played => PlayedList;
    }

    // ── opaque config ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Config_reads_typed_values()
    {
        var cfg = Config("""{"artistUri":"spotify:artist:x","maxItems":7,"descending":false}""");
        Assert.True(cfg.IsObject);
        Assert.Equal("spotify:artist:x", cfg.Str("artistUri"));
        Assert.Equal(7, cfg.Int("maxItems"));
        Assert.False(cfg.Bool("descending", true));
    }

    [Fact]
    public void Config_falls_back_on_absent_or_wrong_typed_values()
    {
        var cfg = Config("""{"maxItems":"five"}""");
        Assert.Equal(3, cfg.Int("maxItems", 3));          // wrong type ⇒ the fallback, never a throw
        Assert.Null(cfg.Str("artistUri"));
        Assert.True(cfg.Bool("missing", true));
    }

    [Fact]
    public void Config_default_element_is_never_an_exception()
    {
        var cfg = SidebarSourceConfig.Empty;              // a section that carries no config at all
        Assert.False(cfg.IsObject);
        Assert.Null(cfg.Str("anything"));
        Assert.Equal(4, cfg.Int("anything", 4));
        var into = new List<string>();
        Assert.Equal(0, cfg.Strings("includeUris", into));
    }

    [Fact]
    public void Config_reads_uri_lists_and_skips_non_strings()
    {
        var cfg = Config("""{"includeUris":["spotify:artist:a",3,"spotify:artist:b"]}""");
        var into = new List<string>();
        Assert.Equal(2, cfg.Strings("includeUris", into));
        Assert.Equal("spotify:artist:a", into[0]);
        Assert.Equal("spotify:artist:b", into[1]);
    }

    // ── contribution ids ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SourceId_composes_extension_and_contribution()
    {
        Assert.Equal("wavee.artist.topTracks", SidebarContributions.SourceId("wavee", "artist.topTracks"));
        Assert.Equal(SidebarContributions.ArtistTopTracks, SidebarContributions.SourceId("wavee", "artist.topTracks"));
        Assert.Equal("artist.topTracks", SidebarContributions.ContributionOf(SidebarContributions.ArtistTopTracks));
    }

    [Fact]
    public void SourceId_is_empty_when_either_half_is_missing()
    {
        Assert.Equal("", SidebarContributions.SourceId(null, "library"));
        Assert.Equal("", SidebarContributions.SourceId("wavee", ""));
    }

    [Fact]
    public void SourceId_does_not_double_prefix_an_already_qualified_contribution()
        => Assert.Equal("wavee.library", SidebarContributions.SourceId("wavee", "wavee.library"));

    [Fact]
    public void All_nine_first_party_ids_are_declared_and_unique()
    {
        Assert.Equal(9, SidebarContributions.FirstParty.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in SidebarContributions.FirstParty)
        {
            Assert.True(seen.Add(id), id);
            Assert.True(SidebarContributions.IsFirstParty(id));
            Assert.StartsWith(SidebarContributions.WaveeExtensionId + ".", id, StringComparison.Ordinal);
        }
        Assert.False(SidebarContributions.IsFirstParty("acme.charts"));
    }

    // ── the host / registry ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Table_resolves_registered_sources_as_live()
    {
        var table = new SidebarDataSourceTable();
        var source = new StubSource(SidebarContributions.Library);
        table.Add(source);

        var resolved = table.Resolve(SidebarContributions.Library, out var availability);
        Assert.Same(source, resolved);
        Assert.Equal(SidebarContributionAvailability.Live, availability);
    }

    [Fact]
    public void Table_reports_missing_and_disabled_distinctly()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Queue));

        Assert.Null(table.Resolve("acme.charts", out var missing));
        Assert.Equal(SidebarContributionAvailability.Missing, missing);

        table.SetEnabled(SidebarContributions.Queue, false);
        Assert.Null(table.Resolve(SidebarContributions.Queue, out var disabled));
        Assert.Equal(SidebarContributionAvailability.Disabled, disabled);
        Assert.False(table.IsEnabled(SidebarContributions.Queue));

        table.SetEnabled(SidebarContributions.Queue, true);
        Assert.NotNull(table.Resolve(SidebarContributions.Queue, out var live));
        Assert.Equal(SidebarContributionAvailability.Live, live);
    }

    [Fact]
    public void Table_state_of_unregistered_source_is_error()
    {
        var table = new SidebarDataSourceTable();
        Assert.Equal(SidebarSourceState.Error, table.StateOf("acme.charts"));
        var source = new StubSource(SidebarContributions.Concerts);
        table.Add(source);
        source.Publish(SidebarSourceState.Pending);
        Assert.Equal(SidebarSourceState.Pending, table.StateOf(SidebarContributions.Concerts));
    }

    [Fact]
    public void Base_class_raises_changed_only_on_a_real_health_move()
    {
        var source = new StubSource("acme.charts");
        int raised = 0;
        source.Changed += () => raised++;

        source.Publish(SidebarSourceState.Pending);
        Assert.Equal(1, raised);
        source.Publish(SidebarSourceState.Pending);      // identical verdict ⇒ no notify (a poll cannot spin the binder)
        Assert.Equal(1, raised);
        source.Publish(SidebarSourceState.Ready, prompt: true);
        Assert.Equal(2, raised);
        Assert.True(source.NeedsPrompt);
    }

    [Fact]
    public void RegisterAll_wires_exactly_the_nine_first_party_sources_as_live()
    {
        // The persisted-key regression bar in one fact: a rename or a dropped registration here orphans a user's
        // stored section (sidebar-layout.json keys on the source id).
        var snapshot = new FakeProjectionSnapshot();
        var table = WaveeBuiltInDataSources.RegisterAll(snapshot);
        Assert.Equal(9, table.Count);
        foreach (string id in SidebarContributions.FirstParty)
        {
            var resolved = table.Resolve(id, out var availability);
            Assert.NotNull(resolved);
            Assert.Equal(SidebarContributionAvailability.Live, availability);
        }
    }

    // ── health translation (the "offline ⇒ Ready, never broken" rule) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarFeedState.Idle, SidebarSourceState.Pending)]
    [InlineData(SidebarFeedState.Loading, SidebarSourceState.Pending)]
    [InlineData(SidebarFeedState.Populated, SidebarSourceState.Ready)]
    [InlineData(SidebarFeedState.Empty, SidebarSourceState.Ready)]
    [InlineData(SidebarFeedState.Offline, SidebarSourceState.Ready)]
    [InlineData(SidebarFeedState.Error, SidebarSourceState.Error)]
    public void Feed_state_maps_offline_to_ready_and_only_error_to_error(SidebarFeedState feed, SidebarSourceState expected)
        => Assert.Equal(expected, SidebarSourceMap.FromFeedState(feed));

    // ── mappers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Track_rows_are_unpinnable_and_never_navigate()
    {
        TestScope.Fresh();
        var track = Tr("1", "Song", "A");
        var e = SidebarSourceMap.FromTrack(track, order: 0);

        Assert.Equal(SidebarEntryKind.Track, e.Kind);
        Assert.True(e.IsTrack);
        Assert.True(e.IsPlayable);
        Assert.Null(e.RouteKey);
        Assert.Null(SidebarPinId.FromEntry(in e));
        Assert.Equal("spotify:track:1", e.Id);
        Assert.Equal("A", e.Creator);
    }

    [Fact]
    public void Track_creator_joins_at_most_three_artists()
    {
        TestScope.Fresh();
        var track = Tr("2", "Song", "A", "B", "C", "D");
        var e = SidebarSourceMap.FromTrack(track, 0);

        Assert.Equal("A, B, C…", e.Creator);
        Assert.Equal("A", e.FirstArtistName);
    }

    [Fact]
    public void Tracks_dedupes_by_uri_and_honours_max()
    {
        TestScope.Fresh();
        var into = new List<SidebarLibraryEntry>();
        var tracks = new List<Track> { Tr("1", "One"), Tr("1", "One again"), Tr("2", "Two"), Tr("3", "Three") };

        Assert.Equal(2, SidebarSourceMap.Tracks(tracks, into, max: 2));
        Assert.Equal("spotify:track:1", into[0].Id);
        Assert.Equal("spotify:track:2", into[1].Id);
    }

    [Fact]
    public void Played_contexts_resolve_and_carry_the_play_time_not_the_visit_time()
    {
        var playlist = new SidebarLibraryEntry(
            SidebarPinId.PlaylistPrefix + "spotify:playlist:1", SidebarEntryKind.Playlist, "spotify:playlist:1",
            "Mix", "Me", default, null, 3, 0, 500L, LastVisitedTicksUtc: 999L, 0, 0, false, SidebarPlaylistFlavor.ByYou);
        var index = new SidebarSourceIndex();
        index.Rebuild([playlist]);

        var contexts = new List<SidebarPlayedContext>
        {
            new("spotify:playlist:1", SidebarEntryKind.Playlist, 3_000L),
            new("spotify:album:2", SidebarEntryKind.Album, 2_000L),
            new("spotify:track:7", SidebarEntryKind.Track, 1_000L),
        };
        var into = new List<SidebarLibraryEntry>();
        Assert.Equal(3, SidebarSourceMap.Played(contexts, index, into, max: 10));

        Assert.Equal("Mix", into[0].Name);
        Assert.Equal(3_000L, into[0].SortStamp);
        Assert.Equal(999L, into[0].LastVisitedTicksUtc);                       // untouched: this is a PLAYED feed
        // An unresolved context is still emitted (an editorial playlist is not in your library) — with an empty Name,
        // the surface's "render dimmed from the uri" signal.
        Assert.Equal(SidebarPinId.AlbumPrefix + "spotify:album:2", into[1].Id);
        Assert.Equal("", into[1].Name);
        Assert.Equal(SidebarEntryKind.Track, into[2].Kind);                    // a bare track play stays a track
    }

    [Fact]
    public void Visited_walks_newest_first_and_falls_back_to_a_route_row()
    {
        var album = new SidebarLibraryEntry(
            SidebarPinId.AlbumPrefix + "spotify:album:5", SidebarEntryKind.Album, "spotify:album:5", "Album",
            "Artist", default, null, 0, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);
        var index = new SidebarSourceIndex();
        index.Rebuild([album]);

        // HistoryStore order: OLDEST first, and the same key visited twice must produce ONE row (the newest).
        var log = new List<SidebarVisit>
        {
            new(SidebarPinId.AlbumPrefix + "spotify:album:5", 100L),
            new("home", 200L),
            new(SidebarPinId.AlbumPrefix + "spotify:album:5", 300L),
        };
        var into = new List<SidebarLibraryEntry>();
        Assert.Equal(2, SidebarSourceMap.Visited(log, static v => v.RouteKey, static v => v.TicksUtc, index, into, 10));

        Assert.Equal(SidebarPinId.AlbumPrefix + "spotify:album:5", into[0].Id);
        Assert.Equal(300L, into[0].LastVisitedTicksUtc);
        Assert.Equal("Album", into[0].Name);
        Assert.Equal("home", into[1].Id);
        Assert.Equal(SidebarEntryKind.AppRoute, into[1].Kind);
    }

    // ── the shared index ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Index_keys_both_the_entry_id_and_the_bare_uri()
    {
        var e = new SidebarLibraryEntry(
            SidebarPinId.PlaylistPrefix + "spotify:playlist:1", SidebarEntryKind.Playlist, "spotify:playlist:1",
            "Mix", "Me", default, null, 0, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);
        var index = new SidebarSourceIndex();
        index.Rebuild([e]);

        Assert.True(index.TryGet(SidebarPinId.PlaylistPrefix + "spotify:playlist:1", out _));
        Assert.True(index.TryGet("spotify:playlist:1", out var byUri));
        Assert.Equal("Mix", byUri.Name);
        Assert.False(index.TryGet("spotify:playlist:nope", out _));

        // The planner's ByUri face is the same map, and the same instance every call (no per-rebuild allocation).
        var lookup = index.AsLookup();
        Assert.Same(lookup, index.AsLookup());
        Assert.True(lookup.TryGetValue("spotify:playlist:1", out var viaLookup));
        Assert.Equal("Mix", viaLookup.Name);
    }

    [Fact]
    public void Index_rebuild_replaces_the_previous_pass()
    {
        var index = new SidebarSourceIndex();
        index.Rebuild([SidebarLibraryEntry.ForRoute("home", "Home")]);
        index.Rebuild([SidebarLibraryEntry.ForRoute("search", "Search")]);
        Assert.False(index.TryGet("home", out _));
        Assert.True(index.TryGet("search", out _));
    }

    // ── the last-good snapshot seam ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Contribution_cache_stores_and_replays_a_slice()
    {
        var cache = new SidebarContributionCache();
        var pool = new List<SidebarLibraryEntry>
        {
            SidebarLibraryEntry.ForRoute("a", "A"),
            SidebarLibraryEntry.ForRoute("b", "B"),
            SidebarLibraryEntry.ForRoute("c", "C"),
        };
        cache.Store("acme.charts", pool, start: 1, count: 2);
        Assert.True(cache.Has("acme.charts"));

        var into = new List<SidebarLibraryEntry>();
        Assert.Equal(2, cache.TryReplay("acme.charts", into));
        Assert.Equal("b", into[0].Id);

        cache.Forget("acme.charts");
        Assert.False(cache.Has("acme.charts"));
        Assert.Equal(0, cache.TryReplay("acme.charts", into));
    }

    [Fact]
    public void Contribution_cache_ignores_an_out_of_range_window()
    {
        var cache = new SidebarContributionCache();
        cache.Store("acme.charts", [SidebarLibraryEntry.ForRoute("a", "A")], start: 0, count: 5);
        Assert.False(cache.Has("acme.charts"));
    }

    // ── wavee.library ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Library_source_declares_the_full_seven_field_customizer_schema()
    {
        var schema = new SidebarLibrarySource(new FakeProjectionSnapshot()).ConfigSchema;
        Assert.Equal(7, schema.Fields.Count);

        Assert.Equal("kinds", schema.Fields[0].Key);
        Assert.Equal(SidebarConfigFieldKind.Enum, schema.Fields[0].Kind);
        Assert.Equal(new[] { "all", "playlists", "albums", "artists", "shows" }, schema.Fields[0].EnumValues);

        Assert.Equal("sort", schema.Fields[1].Key);
        Assert.Equal(new[] { "recents", "added", "alphabetical", "creator" }, schema.Fields[1].EnumValues);

        Assert.Equal("descending", schema.Fields[2].Key);
        Assert.Equal(SidebarConfigFieldKind.Bool, schema.Fields[2].Kind);
        Assert.Equal("true", schema.Fields[2].DefaultJson);

        Assert.Equal("qualifier", schema.Fields[3].Key);
        Assert.Equal(new[] { "any", "byYou", "bySpotify", "mixed" }, schema.Fields[3].EnumValues);

        Assert.Equal("includeUris", schema.Fields[4].Key);
        Assert.Equal(SidebarConfigFieldKind.UriList, schema.Fields[4].Kind);
        Assert.Equal("excludeUris", schema.Fields[5].Key);
        Assert.Equal(SidebarConfigFieldKind.UriList, schema.Fields[5].Kind);

        Assert.Equal("maxItems", schema.Fields[6].Key);
        Assert.Equal(SidebarConfigFieldKind.Int, schema.Fields[6].Kind);
        Assert.Equal(0, schema.Fields[6].Min);
        Assert.Equal(500, schema.Fields[6].Max);
    }

    [Fact]
    public void Library_source_filters_by_kind_and_caps_at_the_requested_max()
    {
        var snapshot = new FakeProjectionSnapshot();
        snapshot.AllList.AddRange(new[]
        {
            Entry("spotify:playlist:1", SidebarEntryKind.Playlist, "Mix"),
            Entry("spotify:album:1", SidebarEntryKind.Album, "Album A"),
            Entry("spotify:album:2", SidebarEntryKind.Album, "Album B"),
            Entry("spotify:artist:1", SidebarEntryKind.Artist, "Artist A"),
        });
        var source = new SidebarLibrarySource(snapshot);
        Assert.Equal(SidebarContributions.Library, source.Id);

        var into = new List<SidebarLibraryEntry>();
        int n = source.Fill(into, new SidebarSourceRequest(Config("""{"kinds":"albums","maxItems":1}""")));

        Assert.Equal(1, n);
        Assert.Equal(SidebarEntryKind.Album, into[0].Kind);
        Assert.Equal(SidebarSourceState.Ready, source.State);
    }

    [Fact]
    public void Library_source_surfaces_the_snapshots_own_pending_state_only_while_empty()
    {
        var snapshot = new FakeProjectionSnapshot { LibraryStateValue = SidebarSourceState.Pending };
        var source = new SidebarLibrarySource(snapshot);
        var into = new List<SidebarLibraryEntry>();

        source.Fill(into, SidebarSourceRequest.Default);
        Assert.Equal(SidebarSourceState.Pending, source.State);      // nothing in yet: honour the snapshot's own verdict

        snapshot.AllList.Add(Entry("spotify:album:1", SidebarEntryKind.Album, "Album"));
        into.Clear();
        source.Fill(into, SidebarSourceRequest.Default);
        Assert.Equal(SidebarSourceState.Ready, source.State);         // rows landed: Ready regardless of the snapshot's claim
    }

    // ── wavee.playlistTree ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Playlist_tree_source_has_no_configuration_and_flattens_search_to_matching_leaves()
    {
        var snapshot = new FakeProjectionSnapshot();
        snapshot.TreeList.Add(Entry("folder:f1", SidebarEntryKind.Folder, "Chill"));
        snapshot.TreeList.Add(Entry("spotify:playlist:1", SidebarEntryKind.Playlist, "Chill vibes"));
        snapshot.TreeList.Add(Entry("spotify:playlist:2", SidebarEntryKind.Playlist, "Workout"));

        var source = new SidebarPlaylistTreeSource(snapshot);
        Assert.Equal(SidebarContributions.PlaylistTree, source.Id);
        Assert.Equal(1, source.ConfigSchema.Version);
        Assert.Empty(source.ConfigSchema.Fields);
        Assert.Equal(SidebarSourceFilters.Search, source.SupportedFilters);
        Assert.Equal(SidebarSourceSorts.SourceOrder, source.SupportedSorts);

        var into = new List<SidebarLibraryEntry>();
        int n = source.Fill(into, new SidebarSourceRequest(SidebarSourceConfig.Empty, Search: "chill"));

        Assert.Equal(1, n);
        Assert.Equal(SidebarEntryKind.Playlist, into[0].Kind);        // the folder container never matches a search
        Assert.Equal("spotify:playlist:1", into[0].Id);
    }

    // ── wavee.history.visited / wavee.history.played ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Visited_source_declares_wavee_history_visited_with_a_default_cap_of_six()
    {
        var source = new SidebarVisitedSource(new FakeProjectionSnapshot());
        Assert.Equal(SidebarContributions.HistoryVisited, source.Id);
        Assert.Equal(SidebarSourceItemType.Mixed, source.ItemType);

        var field = Assert.Single(source.ConfigSchema.Fields);
        Assert.Equal("maxItems", field.Key);
        Assert.Equal(SidebarConfigFieldKind.Int, field.Kind);
        Assert.Equal("6", field.DefaultJson);
        Assert.Equal(1, field.Min);
        Assert.Equal(40, field.Max);
    }

    [Fact]
    public void Played_source_declares_wavee_history_played_with_a_default_cap_of_six()
    {
        var source = new SidebarPlayedSource(new FakeProjectionSnapshot());
        Assert.Equal(SidebarContributions.HistoryPlayed, source.Id);
        Assert.Equal(SidebarSourceItemType.Mixed, source.ItemType);

        var field = Assert.Single(source.ConfigSchema.Fields);
        Assert.Equal("maxItems", field.Key);
        Assert.Equal("6", field.DefaultJson);
        Assert.Equal(1, field.Min);
        Assert.Equal(40, field.Max);
    }

    // ── wavee.queue ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Queue_source_declares_a_five_item_default_cap_schema()
    {
        var source = new SidebarQueueSource();
        Assert.Equal(SidebarContributions.Queue, source.Id);
        Assert.Equal(SidebarSourceItemType.Track, source.ItemType);

        var field = Assert.Single(source.ConfigSchema.Fields);
        Assert.Equal("maxItems", field.Key);
        Assert.Equal("5", field.DefaultJson);
        Assert.Equal(1, field.Min);
        Assert.Equal(50, field.Max);
    }

    [Fact]
    public void Queue_source_reads_only_the_up_next_slice_never_the_now_playing_head()
    {
        TestScope.Fresh();
        var head = Entities.Track(EntityUri.Parse("spotify:track:head"));
        var next1 = Entities.Track(EntityUri.Parse("spotify:track:next1"));
        var next2 = Entities.Track(EntityUri.Parse("spotify:track:next2"));

        EntityRef[] refs = [new(EntityKind.Track, head.Slot), new(EntityKind.Track, next1.Slot), new(EntityKind.Track, next2.Slot)];
        QueueEdge[] rows =
        [
            new(0, (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying),
            new(0, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue),
            new(0, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
        ];
        Queue.Replace(refs, rows);

        var source = new SidebarQueueSource();
        var into = new List<SidebarLibraryEntry>();
        int n = source.Fill(into, SidebarSourceRequest.Default);

        Assert.Equal(2, n);
        Assert.DoesNotContain(into, e => string.Equals(e.Id, "spotify:track:head", StringComparison.Ordinal));
        Assert.Equal(SidebarSourceState.Ready, source.State);
    }

    [Fact]
    public void Queue_source_dedupes_a_repeated_track_and_caps_at_the_default_five()
    {
        TestScope.Fresh();
        var repeated = Entities.Track(EntityUri.Parse("spotify:track:dup"));

        var refs = new List<EntityRef> { new(EntityKind.Track, repeated.Slot), new(EntityKind.Track, repeated.Slot) };
        var rows = new List<QueueEdge>
        {
            new(0, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
            new(0, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
        };
        for (int i = 0; i < 6; i++)
        {
            var t = Entities.Track(EntityUri.Parse("spotify:track:extra" + i));
            refs.Add(new EntityRef(EntityKind.Track, t.Slot));
            rows.Add(new QueueEdge(0, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp));
        }
        Queue.Replace(refs.ToArray(), rows.ToArray());

        var source = new SidebarQueueSource();
        var into = new List<SidebarLibraryEntry>();
        int n = source.Fill(into, SidebarSourceRequest.Default);

        Assert.Equal(5, n);                                   // the schema's own default cap, even with 7 rows queued
        Assert.Equal("spotify:track:dup", into[0].Id);         // the repeat collapses into ONE row
    }

    // ── wavee.nowPlaying ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Now_playing_source_is_a_single_row_section_with_no_configuration()
    {
        var source = new SidebarNowPlayingSource();
        Assert.Equal(SidebarContributions.NowPlaying, source.Id);
        Assert.Equal(SidebarSourceItemType.Track, source.ItemType);
        Assert.Equal(SidebarSourcePaging.None, source.Paging);
        Assert.Empty(source.ConfigSchema.Fields);
    }

    [Fact]
    public void Now_playing_source_is_empty_while_nothing_plays()
    {
        // The honest empty state — see the file header GAP note on why the populated case is not exercised here.
        Playback.ResetForTests();
        var source = new SidebarNowPlayingSource();
        var into = new List<SidebarLibraryEntry>();

        int n = source.Fill(into, SidebarSourceRequest.Default);

        Assert.Equal(0, n);
        Assert.Empty(into);
        Assert.Equal(SidebarSourceState.Ready, source.State);
    }

    // ── wavee.artist.topTracks ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Artist_top_tracks_source_declares_the_artist_and_maxItems_schema()
    {
        var source = new SidebarArtistTopTracksSource();
        Assert.Equal(SidebarContributions.ArtistTopTracks, source.Id);
        Assert.Equal(2, source.ConfigSchema.Fields.Count);

        var artistField = source.ConfigSchema.Fields[0];
        Assert.Equal("artistUri", artistField.Key);
        Assert.Equal(SidebarConfigFieldKind.EntityUri, artistField.Kind);
        Assert.True(artistField.Required);

        var maxField = source.ConfigSchema.Fields[1];
        Assert.Equal("maxItems", maxField.Key);
        Assert.Equal("5", maxField.DefaultJson);
        Assert.Equal(1, maxField.Min);
        Assert.Equal(50, maxField.Max);
    }

    [Fact]
    public void Artist_top_tracks_source_prompts_unset_before_a_customizer_configures_it()
    {
        var source = new SidebarArtistTopTracksSource();
        var into = new List<SidebarLibraryEntry>();

        int n = source.Fill(into, SidebarSourceRequest.Default);

        Assert.Equal(0, n);
        Assert.Equal(SidebarSourceState.Ready, source.State);         // unconfigured is not broken, just waiting
        Assert.Equal("sidebar.source.artistTopTracks.unset", source.StateDetailLocKey);
    }

    [Fact]
    public void Artist_top_tracks_source_is_pending_until_the_catalogue_answers()
    {
        TestScope.Fresh();
        var source = new SidebarArtistTopTracksSource();
        var into = new List<SidebarLibraryEntry>();

        int n = source.Fill(into, new SidebarSourceRequest(Config("""{"artistUri":"spotify:artist:cold"}""")));

        Assert.Equal(0, n);
        Assert.Equal(SidebarSourceState.Pending, source.State);
    }

    [Fact]
    public void Artist_top_tracks_source_reads_the_chart_and_caps_at_the_default_five()
    {
        TestScope.Fresh();
        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:popular"));
        var slots = new int[7];
        for (int i = 0; i < slots.Length; i++) slots[i] = Entities.Track(EntityUri.Parse("spotify:track:pop" + i)).Slot;
        Entities.Current.Edges.ArtistPopular.Replace(artist.Slot, slots, [], EdgeState.Complete, slots.Length);

        var source = new SidebarArtistTopTracksSource();
        var into = new List<SidebarLibraryEntry>();
        int n = source.Fill(into, new SidebarSourceRequest(Config("""{"artistUri":"spotify:artist:popular"}""")));

        Assert.Equal(5, n);                                           // the schema's own default, no maxItems override
        Assert.Equal(SidebarSourceState.Ready, source.State);
    }

    // ── wavee.newReleases ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void New_releases_source_declares_a_four_item_default_cap_schema()
    {
        var source = new SidebarNewReleasesSource(fetch: null, new FakeProjectionSnapshot());
        Assert.Equal(SidebarContributions.NewReleases, source.Id);

        var field = Assert.Single(source.ConfigSchema.Fields);
        Assert.Equal("maxItems", field.Key);
        Assert.Equal("4", field.DefaultJson);
        Assert.Equal(1, field.Min);
        Assert.Equal(20, field.Max);
    }

    [Fact]
    public void New_releases_source_is_permanently_empty_without_a_fetch_seam()
    {
        var source = new SidebarNewReleasesSource(fetch: null, new FakeProjectionSnapshot());
        Assert.Equal(SidebarSourceState.Ready, source.State);         // a missing seam is empty, never broken

        source.EnsureFresh(SidebarSourceRequest.Default);             // must never throw with nothing to call
        var into = new List<SidebarLibraryEntry>();
        Assert.Equal(0, source.Fill(into, SidebarSourceRequest.Default));
    }

    [Fact]
    public void New_releases_source_resolves_reported_rows_and_drops_the_unresolved_one()
    {
        var album = new SidebarLibraryEntry(
            SidebarPinId.AlbumPrefix + "spotify:album:5", SidebarEntryKind.Album, "spotify:album:5", "Real Album",
            "Real Artist", default, null, 10, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);
        var index = new SidebarSourceIndex();
        index.Rebuild([album]);
        var snapshot = new FakeProjectionSnapshot { IndexValue = index };

        int fetchCalls = 0;
        void Fetch(in SidebarSourceRequest r) => fetchCalls++;
        var source = new SidebarNewReleasesSource(Fetch, snapshot);
        Assert.Equal(SidebarSourceState.Pending, source.State);      // a real seam starts pending, not ready

        source.EnsureFresh(SidebarSourceRequest.Default);
        Assert.Equal(1, fetchCalls);

        source.Report(SidebarSourceState.Ready,
        [
            new SidebarNewReleaseRow("spotify:album:5", 1_700_000_000_000L),
            new SidebarNewReleaseRow("spotify:album:unknown", 1L),
        ]);

        var into = new List<SidebarLibraryEntry>();
        int n = source.Fill(into, SidebarSourceRequest.Default);

        Assert.Equal(1, n);                                           // the unresolved uri is dropped, not stubbed
        Assert.Equal("Real Album", into[0].Name);
        Assert.Equal(1_700_000_000_000L, into[0].SortStamp);
        Assert.Equal(SidebarSourceState.Ready, source.State);
    }

    // ── wavee.concerts ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Concerts_source_declares_maxItems_and_radius_with_the_documented_defaults()
    {
        var source = new SidebarConcertsSource(fetch: null);
        Assert.Equal(SidebarContributions.Concerts, source.Id);
        Assert.Equal(30, SidebarConcertsSource.RefreshMinutes);
        Assert.Equal(20, SidebarConcertsSource.SnapshotCap);
        Assert.Equal(2, source.ConfigSchema.Fields.Count);

        var maxField = source.ConfigSchema.Fields[0];
        Assert.Equal("maxItems", maxField.Key);
        Assert.Equal("3", maxField.DefaultJson);

        var radiusField = source.ConfigSchema.Fields[1];
        Assert.Equal("radiusKm", radiusField.Key);
        Assert.Equal("100", radiusField.DefaultJson);
        Assert.Equal(1, radiusField.Min);
        Assert.Equal(500, radiusField.Max);
    }

    [Fact]
    public void Concerts_source_prompts_for_a_location_before_asking_for_anything()
    {
        TestScope.Fresh();
        var source = new SidebarConcertsSource(fetch: null);
        var into = new List<SidebarLibraryEntry>();

        int n = source.Fill(into, SidebarSourceRequest.Default);

        Assert.Equal(0, n);
        Assert.Equal(SidebarSourceState.Ready, source.State);          // no location is EMPTY-but-actionable, not broken
        Assert.True(source.NeedsPrompt);
        Assert.Equal("sidebar.concerts.setLocation", source.StateDetailLocKey);
    }

    [Fact]
    public void Concerts_source_refreshes_at_most_once_per_thirty_minute_window()
    {
        TestScope.Fresh();
        var places = Entities.Current.Places;
        int place = places.Slot(Entities.Strings.Intern("place:manchester"));
        places.Row[place].Id = Entities.Strings.Intern("place:manchester");
        Entities.Current.SavedPlace = place;

        int calls = 0;
        void Fetch(int p, int r, int f) => calls++;
        var source = new SidebarConcertsSource(Fetch);

        source.EnsureFresh(SidebarSourceRequest.Default);
        source.EnsureFresh(SidebarSourceRequest.Default);

        Assert.Equal(1, calls);        // the second ask lands inside the same 30-minute window and is skipped
    }

    [Fact]
    public void Concerts_source_reads_the_real_feed_sorted_soonest_first_and_capped_at_the_default_three()
    {
        TestScope.Fresh();
        var places = Entities.Current.Places;
        int place = places.Slot(Entities.Strings.Intern("place:manchester"));
        places.Row[place].Id = Entities.Strings.Intern("place:manchester");
        Entities.Current.SavedPlace = place;

        string[] uris = ["spotify:concert:a", "spotify:concert:b", "spotify:concert:c", "spotify:concert:d"];
        var s = Staging.Rent();
        long baseDate = 1_800_000_000_000L;
        for (int i = 0; i < uris.Length; i++)
        {
            ref var row = ref s.Concerts.Add();
            row.Id = s.Text(uris[i]);
            row.Title = s.Text("Show " + i);
            row.Date = baseDate - i * 1_000L;              // reverse order on purpose: "d" is soonest
            row.Known = (uint)ConcertFields.Identity;
            row.Authority = Authority.Full;
        }
        TestScope.CommitAndPublish(s);

        var slots = new int[uris.Length];
        for (int i = 0; i < uris.Length; i++) slots[i] = Entities.Concert(EntityUri.Parse(uris[i])).Slot;

        int feedSlot = Entities.Current.ConcertFeeds.Slot(Entities.Strings.Intern("place:manchester|100"));
        Entities.Current.Edges.FeedSection.Replace(feedSlot, slots, [], EdgeState.Complete, slots.Length);

        var source = new SidebarConcertsSource(fetch: null);
        var into = new List<SidebarLibraryEntry>();
        int n = source.Fill(into, SidebarSourceRequest.Default);

        Assert.Equal(3, n);                                    // the schema's own default cap
        Assert.Equal("spotify:concert:d", into[0].Id);          // soonest-first: "d" carries the earliest date
        Assert.Equal(SidebarSourceState.Ready, source.State);
        Assert.False(source.NeedsPrompt);
    }
}
