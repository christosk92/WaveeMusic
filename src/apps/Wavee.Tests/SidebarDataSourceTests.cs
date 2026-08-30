using System;
using System.Collections.Generic;
using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The data-source CONTRACT (M1): the opaque-config readers, the contribution id scheme, the registry/host resolution
// (missing / disabled / live), the service-health translation, and the domain → SidebarLibraryEntry mappers every
// first-party adapter is built out of.
//
// Driven against the REAL SidebarDataSource / SidebarSourceMap / SidebarDataSourceTable (source-included, engine-free).
// The concrete adapters in Features/Sidebar/Data/Sources/ hold engine-bound services (LibraryStore, PlaybackBridge, the
// switchable Spotify services) and are deliberately NOT source-included — which is exactly why every decision they make
// lives in the files exercised here.
public class SidebarDataSourceTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSourceConfig Config(string json)
        => new(JsonDocument.Parse(json).RootElement);

    static Track Tr(string id, string title, params string[] artists)
    {
        var refs = new ArtistRef[artists.Length];
        for (int i = 0; i < artists.Length; i++) refs[i] = new ArtistRef("ar" + i, "spotify:artist:ar" + i, artists[i]);
        return new Track("t" + id, "spotify:track:" + id, title, refs,
                         new AlbumRef("al" + id, "spotify:album:" + id, "Album " + id), 200_000, false, null);
    }

    /// <summary>A minimal source: proves the published interface + base class are implementable from outside, and gives the
    /// resolution tests something with configurable health.</summary>
    sealed class StubSource : SidebarDataSourceBase
    {
        readonly List<SidebarLibraryEntry> _rows = new();
        public bool Throw;
        public int SchemaVersion = 1;
        public int Fills;

        public StubSource(string id) : base(id) { }

        public override SidebarConfigSchema ConfigSchema => new(SchemaVersion, Array.Empty<SidebarConfigField>());

        public void SetRows(params string[] ids)
        {
            _rows.Clear();
            for (int i = 0; i < ids.Length; i++)
                _rows.Add(SidebarLibraryEntry.ForRoute(ids[i], ids[i], i));
        }

        public void Publish(SidebarSourceState state, bool prompt = false) => SetHealth(state, null, prompt);

        public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
        {
            Fills++;
            if (Throw) throw new InvalidOperationException("boom");
            for (int i = 0; i < _rows.Count; i++) into.Add(_rows[i]);
            return _rows.Count;
        }
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

    // ── health translation (the "null/offline ⇒ Empty, never broken" rule) ─────────────────────────────────────────────

    [Theory]
    [InlineData(NotificationFeedState.Idle, SidebarSourceState.Pending)]
    [InlineData(NotificationFeedState.Loading, SidebarSourceState.Pending)]
    [InlineData(NotificationFeedState.Populated, SidebarSourceState.Ready)]
    [InlineData(NotificationFeedState.Empty, SidebarSourceState.Ready)]
    [InlineData(NotificationFeedState.Offline, SidebarSourceState.Ready)]
    [InlineData(NotificationFeedState.Error, SidebarSourceState.Error)]
    public void Feed_state_maps_offline_to_ready_and_only_error_to_error(NotificationFeedState feed,
                                                                        SidebarSourceState expected)
        => Assert.Equal(expected, SidebarSourceMap.FromFeedState(feed));

    // ── mappers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Track_rows_are_unpinnable_and_never_navigate()
    {
        var e = SidebarSourceMap.FromTrack(Tr("1", "Song", "A"), order: 0);
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
        var e = SidebarSourceMap.FromTrack(Tr("1", "Song", "A", "B", "C", "D"), 0);
        Assert.Equal("A, B, C…", e.Creator);
        Assert.Equal("A", e.FirstArtistName);
    }

    [Fact]
    public void Tracks_dedupes_by_uri_and_honours_max()
    {
        var into = new List<SidebarLibraryEntry>();
        var tracks = new List<Track> { Tr("1", "One"), Tr("1", "One again"), Tr("2", "Two"), Tr("3", "Three") };

        Assert.Equal(2, SidebarSourceMap.Tracks(tracks, into, max: 2));
        Assert.Equal("spotify:track:1", into[0].Id);
        Assert.Equal("spotify:track:2", into[1].Id);
    }

    [Fact]
    public void Events_carry_title_venue_and_the_event_instant_and_are_not_pinnable()
    {
        var e = SidebarSourceMap.FromEvent("concert:spotify:concert:9", "Live at X", "The Venue", 1_700_000_000_000L,
                                           null, order: 0);
        Assert.Equal(SidebarEntryKind.AppRoute, e.Kind);
        Assert.Equal("Live at X", e.Name);
        Assert.Equal("The Venue", e.Creator);
        Assert.Equal(1_700_000_000_000L, e.SortStamp);
        Assert.Equal("concert:spotify:concert:9", e.RouteKey);       // it DOES navigate…
        Assert.Null(SidebarPinId.FromEntry(in e));                   // …but an event is not a library entity
    }

    [Fact]
    public void New_releases_resolve_against_the_projection_and_keep_the_release_stamp()
    {
        var album = new SidebarLibraryEntry(
            SidebarPinId.AlbumPrefix + "spotify:album:5", SidebarEntryKind.Album, "spotify:album:5", "Real Album",
            "Real Artist", null, null, 10, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);
        var index = new SidebarSourceIndex();
        index.Rebuild([album]);

        var feed = new List<NewReleaseNotification>
        {
            new("n1", 1_700_000_000_000L, true, NewReleaseKind.Album, "spotify:album:5", "Wire name", null, "Wire artist", null, false),
            new("n2", 1_600_000_000_000L, true, NewReleaseKind.Episode, "spotify:show:7", "An episode", null, "A show", null, false),
        };
        var into = new List<SidebarLibraryEntry>();
        Assert.Equal(2, SidebarSourceMap.NewReleases(feed, index, into, max: 10));

        Assert.Equal("Real Album", into[0].Name);                     // the projection wins over the feed's own copy…
        Assert.Equal(1_700_000_000_000L, into[0].SortStamp);          // …but the RELEASE instant is the feed's
        Assert.Equal(SidebarEntryKind.Show, into[1].Kind);            // an episode release opens its show
        Assert.Equal(SidebarPinId.ShowPrefix + "spotify:show:7", into[1].Id);
    }

    [Fact]
    public void New_releases_skip_uris_that_cannot_become_a_route()
    {
        var feed = new List<NewReleaseNotification>
        {
            new("n1", 1L, true, NewReleaseKind.Album, "spotify:track:1", "A track", null, "X", null, false),
        };
        var into = new List<SidebarLibraryEntry>();
        Assert.Equal(0, SidebarSourceMap.NewReleases(feed, SidebarSourceIndex.Empty, into, 10));
    }

    [Fact]
    public void Played_contexts_resolve_and_carry_the_play_time_not_the_visit_time()
    {
        var playlist = new SidebarLibraryEntry(
            SidebarPinId.PlaylistPrefix + "spotify:playlist:1", SidebarEntryKind.Playlist, "spotify:playlist:1",
            "Mix", "Me", null, null, 3, 0, 500L, LastVisitedTicksUtc: 999L, 0, 0, false, SidebarPlaylistFlavor.ByYou);
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
        // An unresolved context is still emitted (an editorial playlist is not in your library) — with an empty Name, the
        // surface's "render dimmed from the uri" signal.
        Assert.Equal(SidebarPinId.AlbumPrefix + "spotify:album:2", into[1].Id);
        Assert.Equal("", into[1].Name);
        Assert.Equal(SidebarEntryKind.Track, into[2].Kind);                    // a bare track play stays a track
    }

    [Fact]
    public void Visited_walks_newest_first_and_falls_back_to_a_route_row()
    {
        var album = new SidebarLibraryEntry(
            SidebarPinId.AlbumPrefix + "spotify:album:5", SidebarEntryKind.Album, "spotify:album:5", "Album",
            "Artist", null, null, 0, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);
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
            "Mix", "Me", null, null, 0, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);
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
}
