using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// The projection BINDER's rules — Wave 1's missing Entries driver, plus M1's contribution resolution.
//
// SidebarProjectionBinder itself is the impure shell (stores, signals, the UI-thread pump) and cannot be constructed
// without an engine; every DECISION it makes is in the engine-free half that IS source-included, and that is what these
// tests drive: the rebuild trigger gate (SidebarBinderTriggers), the filter → sort → pins-first shaping
// (SidebarBinderPipeline), the first-seen commit trigger (SidebarProjection), the resolution of an Extension section to a
// planner slice, and the rows SidebarRowPlanner then produces for every degraded state.
public sealed class SidebarProjectionBinderTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name,
        string creator = "", long sortStamp = 1, int order = 0, int depth = 0,
        SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None)
        => new(id, kind, uri, name, creator, null, null, ChildCount: 0, AddedAtMs: 0, SortStamp: sortStamp,
               LastVisitedTicksUtc: 0, SourceOrder: order, Depth: depth, Circular: false, Flavor: flavor);

    static SidebarLibraryEntry Playlist(string slug, string name, int order = 0,
        SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None)
        => Entry(SidebarPinId.PlaylistPrefix + "spotify:playlist:" + slug, SidebarEntryKind.Playlist,
                 "spotify:playlist:" + slug, name, "Owner", 100 + order, order, flavor: flavor);

    static SidebarLibraryEntry Album(string slug, string name, int order = 0)
        => Entry(SidebarPinId.AlbumPrefix + "spotify:album:" + slug, SidebarEntryKind.Album,
                 "spotify:album:" + slug, name, "Artist", 200 + order, order);

    static SidebarLibraryEntry Folder(string id, string name)
        => Entry(SidebarPinId.FolderPrefix + id, SidebarEntryKind.Folder, "", name);

    static SidebarPin Pin(string id) => new(id, SidebarPinId.KindOf(id), SidebarPinId.UriOf(id), "cached", 0);

    static SidebarSectionSpec ExtSection(string id, string contribution, int schemaVersion = 1, int maxItems = 0)
        => new(id, SidebarSectionKind.Extension, null, null)
        {
            Extension = new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, contribution, schemaVersion,
                                                default),
            Display = maxItems > 0 ? SidebarDisplayOptions.Default with { MaxItems = maxItems } : null,
        };

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections) => new(SidebarTemplates.Curated, sections);

    static SidebarRowKind[] KindsOf(SidebarRowPlan plan)
    {
        var k = new SidebarRowKind[plan.Rows.Count];
        for (int i = 0; i < k.Length; i++) k[i] = plan.Rows[i].Kind;
        return k;
    }

    /// <summary>A configurable contributed source (the shape M3's sandboxed extensions arrive in).</summary>
    sealed class StubSource : SidebarDataSourceBase
    {
        readonly List<SidebarLibraryEntry> _rows = new();
        public bool Throw;
        public bool PartialThenThrow;
        public int SchemaVersion = 1;

        public StubSource(string id) : base(id) { }

        public override SidebarConfigSchema ConfigSchema => new(SchemaVersion, Array.Empty<SidebarConfigField>());

        public StubSource With(params SidebarLibraryEntry[] rows)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            return this;
        }

        public void Publish(SidebarSourceState state, bool prompt = false) => SetHealth(state, null, prompt);

        public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
        {
            if (Throw) throw new InvalidOperationException("boom");
            if (PartialThenThrow)
            {
                into.Add(Playlist("partial", "Partial"));
                throw new InvalidOperationException("boom after a partial fill");
            }
            int max = request.MaxItems > 0 ? request.MaxItems : _rows.Count;
            int n = _rows.Count < max ? _rows.Count : max;
            for (int i = 0; i < n; i++) into.Add(_rows[i]);
            return n;
        }
    }

    // ── the rebuild gate ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Identical_triggers_compare_equal_so_a_redundant_sync_does_no_work()
    {
        var a = new SidebarBinderTriggers(LibraryEpoch: 11, PinsVersion: 2, PlayLogRevision: 3);
        var b = new SidebarBinderTriggers(LibraryEpoch: 11, PinsVersion: 2, PlayLogRevision: 3);
        Assert.Equal(a, b);
        Assert.Equal(a.Fold(), b.Fold());
    }

    [Fact]
    public void A_pin_mutation_triggers_a_rebuild()
    {
        var before = new SidebarBinderTriggers(PinsVersion: 4);
        var after = before with { PinsVersion = 5 };
        Assert.NotEqual(before, after);
        Assert.NotEqual(before.Fold(), after.Fold());
    }

    [Fact]
    public void A_play_log_append_triggers_a_rebuild()
    {
        var before = new SidebarBinderTriggers(PlayLogRevision: 17);
        var after = before with { PlayLogRevision = 18 };
        Assert.NotEqual(before, after);
        Assert.NotEqual(before.Fold(), after.Fold());
    }

    [Fact]
    public void A_filter_sort_or_design_change_triggers_a_rebuild()
    {
        int all = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: true);
        int playlists = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.Playlists,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: true);
        int alphabetical = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Alphabetical, descending: true);
        int ascending = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: false);
        int curated = SidebarBinderTriggers.PackV3((int)SidebarDesign.Curated, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: true);

        Assert.Equal(5, new HashSet<int> { all, playlists, alphabetical, ascending, curated }.Count);
    }

    [Fact]
    public void A_search_keystroke_triggers_a_rebuild()
    {
        var before = new SidebarBinderTriggers(SearchHash: "caf".GetHashCode(StringComparison.Ordinal));
        var after = new SidebarBinderTriggers(SearchHash: "café".GetHashCode(StringComparison.Ordinal));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void A_source_notification_triggers_a_rebuild()
    {
        var before = new SidebarBinderTriggers(SourceEpoch: 1);
        Assert.NotEqual(before, before with { SourceEpoch = 2 });
    }

    [Fact]
    public void A_queue_or_now_playing_change_triggers_a_rebuild()
    {
        var before = new SidebarBinderTriggers(PlaybackEpoch: 4L << 20);
        Assert.NotEqual(before, before with { PlaybackEpoch = 5L << 20 });
        Assert.NotEqual(before.Fold(), (before with { PlaybackEpoch = (4L << 20) ^ 99 }).Fold());
    }

    // ── the published entry list ───────────────────────────────────────────────────────────────────────────────────────

    static readonly IReadOnlyList<SidebarLibraryEntry> Library =
    [
        Playlist("1", "Alpha", 0, SidebarPlaylistFlavor.ByYou),
        Playlist("2", "Beta", 1, SidebarPlaylistFlavor.BySpotify),
        Album("9", "Ceremony", 2),
        Folder("f1", "Chill"),
    ];

    static (List<SidebarLibraryEntry> Rows, SidebarEntriesShape Shape) Project(
        SidebarV3Filter filter = SidebarV3Filter.All,
        SidebarV3Qualifier qualifier = SidebarV3Qualifier.Any,
        SidebarV3Sort sort = SidebarV3Sort.Recents,
        bool desc = true,
        string? search = null,
        bool qualifiersAvailable = false,
        IReadOnlyList<SidebarPin>? pins = null,
        IReadOnlyList<string>? customOrder = null,
        IReadOnlyList<SidebarLibraryEntry>? library = null)
    {
        var into = new List<SidebarLibraryEntry>();
        var scratch = new List<SidebarLibraryEntry>();
        var query = new SidebarV3Query(filter, qualifier, sort, desc, search, qualifiersAvailable);
        var shape = SidebarBinderPipeline.Project(library ?? Library, into, scratch, in query, pins, customOrder);
        return (into, shape);
    }

    [Fact]
    public void The_filter_selects_the_contributing_kinds()
    {
        Assert.Equal(4, Project().Shape.Count);                                     // All: playlists + album + folder
        Assert.Equal(1, Project(SidebarV3Filter.Albums).Shape.Count);
        Assert.Equal(0, Project(SidebarV3Filter.Artists).Shape.Count);
        Assert.Equal(3, Project(SidebarV3Filter.Playlists).Shape.Count);            // playlists INCLUDE their folders
    }

    [Fact]
    public void Search_matches_name_and_flattens_folders_away()
    {
        var (rows, shape) = Project(search: "alpha");
        Assert.Equal(1, shape.Count);
        Assert.Equal("Alpha", rows[0].Name);

        // A folder is a container, not a result: searching drops folder rows entirely.
        Assert.Equal(0, Project(search: "chill").Shape.Count);
    }

    [Fact]
    public void Search_is_case_and_diacritics_insensitive_and_trims()
    {
        Assert.Equal(1, Project(search: "  BETA ").Shape.Count);
    }

    [Fact]
    public void A_stale_qualifier_cannot_hide_the_list_when_the_chips_are_unavailable()
    {
        // QualifiersAvailable == false ⇒ the persisted qualifier is treated as Any (the whole list survives)…
        Assert.Equal(3, Project(SidebarV3Filter.Playlists, SidebarV3Qualifier.BySpotify).Shape.Count);
        // …and honoured once the data supports the chips.
        Assert.Equal(1, Project(SidebarV3Filter.Playlists, SidebarV3Qualifier.BySpotify,
                                qualifiersAvailable: true).Shape.Count);
    }

    [Fact]
    public void Pins_lead_in_pin_order_and_PinCount_is_the_band_length()
    {
        var pins = new[] { Pin(SidebarPinId.AlbumPrefix + "spotify:album:9"),
                           Pin(SidebarPinId.PlaylistPrefix + "spotify:playlist:2") };
        var (rows, shape) = Project(sort: SidebarV3Sort.Alphabetical, desc: false, pins: pins);

        Assert.Equal(2, shape.PinCount);
        Assert.Equal(SidebarPinId.AlbumPrefix + "spotify:album:9", rows[0].Id);      // PIN order, not sort order
        Assert.Equal(SidebarPinId.PlaylistPrefix + "spotify:playlist:2", rows[1].Id);
        Assert.True(rows[0].IsPinned);
        Assert.True(rows[1].IsPinned);
        Assert.False(rows[2].IsPinned);
        Assert.Equal(4, shape.Count);                                               // a pin is moved, never duplicated
    }

    [Fact]
    public void A_pin_the_filter_excludes_does_not_appear()
    {
        var pins = new[] { Pin(SidebarPinId.AlbumPrefix + "spotify:album:9") };
        var (rows, shape) = Project(SidebarV3Filter.Playlists, pins: pins);
        Assert.Equal(0, shape.PinCount);
        for (int i = 0; i < rows.Count; i++)
            Assert.NotEqual(SidebarPinId.AlbumPrefix + "spotify:album:9", rows[i].Id);
    }

    [Fact]
    public void Custom_sort_outside_the_playlists_filter_falls_back_to_alphabetical()
    {
        var order = new[] { SidebarPinId.AlbumPrefix + "spotify:album:9" };
        var (rows, _) = Project(SidebarV3Filter.All, sort: SidebarV3Sort.Custom, customOrder: order);
        // Custom would have put the album first; outside Playlists the DISPLAY sort is Alphabetical (A→Z, desc ignored
        // for the fallback direction here only in the sense that the preference itself is untouched).
        Assert.NotEqual(SidebarPinId.AlbumPrefix + "spotify:album:9", rows[0].Id);
    }

    [Fact]
    public void Custom_sort_under_the_playlists_filter_honours_the_local_order()
    {
        var order = new[] { SidebarPinId.PlaylistPrefix + "spotify:playlist:2" };
        var (rows, _) = Project(SidebarV3Filter.Playlists, sort: SidebarV3Sort.Custom, customOrder: order);
        Assert.Equal(SidebarPinId.PlaylistPrefix + "spotify:playlist:2", rows[0].Id);
    }

    [Fact]
    public void Shape_operates_in_place_so_the_binder_needs_no_copy()
    {
        var list = new List<SidebarLibraryEntry> { Playlist("1", "Alpha"), Playlist("2", "Beta") };
        var scratch = new List<SidebarLibraryEntry>();
        var query = new SidebarV3Query(SidebarV3Filter.Playlists, Search: "beta");
        var shape = SidebarBinderPipeline.Shape(list, scratch, in query);
        Assert.Equal(1, shape.Count);
        Assert.Single(list);
        Assert.Equal("Beta", list[0].Name);
    }

    // ── an unlisted pin (editorial/Spotify-owned playlist, or anything else never saved to the library) ────────────────
    //
    // Repro: pinning "Top Songs - South Korea" (owner "Spotify") rendered a blank cover + "0 songs" in the Pinned
    // section even though the same playlist's own detail page painted real art and a full tracklist in the SAME
    // session. Root cause: SidebarProjectionBinder.ResolvePins resolves a pin by looking it up in `_index`, which is
    // built ONLY from LibraryStore's rootlist tree + Albums/Artists/Shows (the user's own library) — an editorial
    // playlist the user never saved is never in there, so the old code fell straight to a bare display-cache row
    // (Cover = null, ChildCount = 0) with no path to ever fill in real data. SidebarBinderPipeline.ResolveUnlistedPin
    // is the pure merge the binder now runs instead: same bare row when nothing has been hydrated yet, overlaid with
    // whatever RequestPinHydration resolved (through the same Services.Library façade DetailPage uses) once it lands.

    [Fact]
    public void An_unlisted_pin_with_no_hydration_yet_still_renders_the_offline_display_cache()
    {
        var pin = new SidebarPin(SidebarPinId.PlaylistPrefix + "spotify:playlist:korea",
            SidebarEntryKind.Playlist, "spotify:playlist:korea", "Top Songs - South Korea", AddedAtMs: 1000);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null);

        Assert.True(row.IsPinned);
        Assert.Equal(pin.Id, row.Id);
        Assert.Equal(SidebarEntryKind.Playlist, row.Kind);
        Assert.Equal("Top Songs - South Korea", row.Name);         // the pin's own cached name, never blank
        Assert.Null(row.Cover);                                    // …but no art or count until hydration lands
        Assert.Equal(0, row.TrackCount);
    }

    [Fact]
    public void An_unlisted_pin_projects_real_art_and_a_real_track_count_once_hydrated()
    {
        // The playlist is NOT in the rootlist/library projection (that is the whole point of this fixture: an
        // editorial playlist), so the row can ONLY come from the hydration overlay, exactly like
        // SidebarProjectionBinder.HydratePlaylistAsync builds it from Services.Library.GetPlaylistAsync.
        var pin = new SidebarPin(SidebarPinId.PlaylistPrefix + "spotify:playlist:korea",
            SidebarEntryKind.Playlist, "spotify:playlist:korea", "Top Songs - South Korea", AddedAtMs: 1000);
        var hydrated = new SidebarLibraryEntry(
            "", SidebarEntryKind.Playlist, "", "", "Spotify", new Image("https://i.scdn.co/image/korea-cover"), null,
            ChildCount: 50, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.True(row.IsPinned);
        Assert.Equal(pin.Id, row.Id);
        Assert.Equal("Top Songs - South Korea", row.Name);         // the pin's own cache still names the row
        Assert.NotNull(row.Cover);
        Assert.Equal("https://i.scdn.co/image/korea-cover", row.Cover!.Url);
        Assert.Equal(50, row.TrackCount);
        Assert.Equal("Spotify", row.Creator);                      // the owner name, from the SAME façade the detail page uses
    }

    [Fact]
    public void An_unlisted_pins_hydration_overlay_never_blanks_a_field_it_did_not_resolve()
    {
        // A partial hydration reply (e.g. a show's Publisher with no cover yet) must not stomp the pin's own display
        // cache with an empty value — only a field the overlay actually carries wins.
        var pin = new SidebarPin(SidebarPinId.ShowPrefix + "spotify:show:1", SidebarEntryKind.Show,
            "spotify:show:1", "cached", AddedAtMs: 0);
        var hydrated = new SidebarLibraryEntry(
            "", SidebarEntryKind.Show, "", "", "Acme Media", null, null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.Null(row.Cover);                 // the overlay had none — the (already-null) base value survives
        Assert.Equal("Acme Media", row.Creator); // …but the field the overlay DID carry wins
    }

    // ── the first-seen commit trigger (commit point #9) ────────────────────────────────────────────────────────────────

    [Fact]
    public void First_seen_stamps_are_reported_once_which_is_when_the_binder_persists()
    {
        var tree = new PlaylistNode[]
        {
            new PlaylistLeaf(new PlaylistSummary("spotify:playlist:1", "Alpha", "Me", 3, null, null, false, true)),
            new PlaylistLeaf(new PlaylistSummary("spotify:playlist:2", "Beta", "Me", 3, null, null, false, true)),
        };
        var seen = new SidebarFirstSeen(() => 1_000L);
        var buffer = new List<SidebarLibraryEntry>();

        var first = SidebarProjection.Build(buffer, SidebarEntryKindMask.PlaylistTree, tree,
            Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Show>(), null, null, seen,
            includeFolderChildren: true);
        Assert.Equal(2, first.NewFirstSeenStamps);          // > 0 ⇒ prefs.PublishFirstSeen

        seen.ResetNewCount();
        var second = SidebarProjection.Build(buffer, SidebarEntryKindMask.PlaylistTree, tree,
            Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Show>(), null, null, seen,
            includeFolderChildren: true);
        Assert.Equal(0, second.NewFirstSeenStamps);         // a steady-state rebuild never commits the document
    }

    // ── contribution resolution ───────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionSlice Resolve(SidebarSectionSpec section, ISidebarContributionHost? host,
        List<SidebarLibraryEntry> pool, SidebarContributionCache? cache = null)
        => SidebarBinderPipeline.Resolve(section, host, pool, cache);

    [Fact]
    public void An_unregistered_contribution_resolves_to_missing()
    {
        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "charts"), new SidebarDataSourceTable(), pool);

        Assert.Equal(SidebarContributionAvailability.Missing, slice.Availability);
        Assert.Equal(0, slice.Count);
        Assert.Empty(pool);
    }

    [Fact]
    public void A_section_with_no_extension_ref_resolves_to_missing()
    {
        var pool = new List<SidebarLibraryEntry>();
        var bare = new SidebarSectionSpec("sec_bare", SidebarSectionKind.Extension, null, null);
        Assert.Equal(SidebarContributionAvailability.Missing, Resolve(bare, new SidebarDataSourceTable(), pool).Availability);
    }

    [Fact]
    public void A_disabled_contribution_resolves_to_disabled_and_keeps_the_section()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha")));
        table.SetEnabled(SidebarContributions.Library, false);

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool);
        Assert.Equal(SidebarContributionAvailability.Disabled, slice.Availability);
        Assert.Empty(pool);
    }

    [Fact]
    public void A_newer_config_schema_resolves_to_incompatible_and_changes_nothing()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha")));

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library", schemaVersion: 2), table, pool);
        Assert.Equal(SidebarContributionAvailability.Incompatible, slice.Availability);
        Assert.Empty(pool);
    }

    [Fact]
    public void A_live_source_fills_a_window_into_the_shared_pool()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha"), Playlist("2", "Beta")));

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool);

        Assert.Equal(SidebarContributionAvailability.Live, slice.Availability);
        Assert.Equal(0, slice.Start);
        Assert.Equal(2, slice.Count);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void The_sections_MaxItems_reaches_the_source_as_the_request_bound()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library)
            .With(Playlist("1", "Alpha"), Playlist("2", "Beta"), Playlist("3", "Gamma")));

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library", maxItems: 2), table, pool);
        Assert.Equal(2, slice.Count);
    }

    [Fact]
    public void Every_extension_section_gets_a_disjoint_window_over_one_pool()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha")));
        table.Add(new StubSource(SidebarContributions.Queue).With(Album("9", "Ceremony"), Album("8", "Other")));

        var pool = new List<SidebarLibraryEntry>();
        var slices = new SidebarExtensionSlices();
        SidebarBinderPipeline.ResolveExtensions(
            Doc(ExtSection("sec_1", "library"), ExtSection("sec_2", "queue")), table, pool, slices);

        Assert.True(slices.TryGet("sec_1", out var one));
        Assert.True(slices.TryGet("sec_2", out var two));
        Assert.Equal((0, 1), (one.Start, one.Count));
        Assert.Equal((1, 2), (two.Start, two.Count));
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void Extension_sections_nested_in_a_custom_group_are_resolved_too()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Queue).With(Album("9", "Ceremony")));

        var group = new SidebarSectionSpec("sec_group", SidebarSectionKind.CustomGroup, null, null)
        {
            Children = new[] { ExtSection("sec_child", "queue") },
        };
        var pool = new List<SidebarLibraryEntry>();
        var slices = new SidebarExtensionSlices();
        SidebarBinderPipeline.ResolveExtensions(Doc(group), table, pool, slices);

        Assert.True(slices.TryGet("sec_child", out var slice));
        Assert.Equal(1, slice.Count);
    }

    [Fact]
    public void A_throwing_source_leaks_no_partial_rows_and_reports_error()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library) { PartialThenThrow = true });

        var pool = new List<SidebarLibraryEntry> { Album("9", "Pre-existing") };
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool);

        Assert.Equal(SidebarSourceState.Error, slice.State);
        Assert.Equal(0, slice.Count);
        Assert.Single(pool);                                   // the earlier section's row is untouched
        Assert.Equal(SidebarPinId.AlbumPrefix + "spotify:album:9", pool[0].Id);
    }

    [Fact]
    public void A_failed_source_replays_its_last_good_snapshot_as_cached()
    {
        var table = new SidebarDataSourceTable();
        var source = new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha"), Playlist("2", "Beta"));
        table.Add(source);
        var cache = new SidebarContributionCache();
        var section = ExtSection("sec_1", "library");

        var pool = new List<SidebarLibraryEntry>();
        var live = Resolve(section, table, pool, cache);
        Assert.Equal(SidebarContributionAvailability.Live, live.Availability);
        Assert.True(cache.Has(SidebarContributions.Library));

        // The source starts failing: the section must go STALE, not blank.
        source.With();
        source.Publish(SidebarSourceState.Error);
        pool.Clear();
        var stale = Resolve(section, table, pool, cache);

        Assert.Equal(SidebarContributionAvailability.Cached, stale.Availability);
        Assert.Equal(SidebarSourceState.Ready, stale.State);    // there ARE rows to draw
        Assert.Equal(2, stale.Count);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void A_failing_source_with_no_snapshot_is_an_error_slice_not_a_cached_one()
    {
        var table = new SidebarDataSourceTable();
        var source = new StubSource(SidebarContributions.Library);
        source.Publish(SidebarSourceState.Error);
        table.Add(source);

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool, new SidebarContributionCache());
        Assert.Equal(SidebarContributionAvailability.Live, slice.Availability);
        Assert.Equal(SidebarSourceState.Error, slice.State);
        Assert.Equal(0, slice.Count);
    }

    [Fact]
    public void An_actionable_degraded_state_travels_to_the_slice()
    {
        var table = new SidebarDataSourceTable();
        var concerts = new StubSource(SidebarContributions.Concerts);
        concerts.Publish(SidebarSourceState.Ready, prompt: true);      // "set your location"
        table.Add(concerts);

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "concerts"), table, pool);
        Assert.True(slice.NeedsPrompt);
        Assert.Equal(SidebarSourceState.Ready, slice.State);
        Assert.Equal(0, slice.Count);
    }

    [Fact]
    public void The_slice_table_reports_availability_for_the_surfaces_badge()
    {
        var slices = new SidebarExtensionSlices();
        slices.Set("sec_1", new SidebarSectionSlice(0, 0, SidebarSourceState.Error,
                                                    SidebarContributionAvailability.Disabled));
        Assert.Equal(SidebarContributionAvailability.Disabled, slices.AvailabilityOf("sec_1"));
        Assert.Equal(SidebarContributionAvailability.Missing, slices.AvailabilityOf("sec_unknown"));
        slices.Clear();
        Assert.Equal(0, slices.Count);
    }

    // ── the planner side: what each degraded state RENDERS ─────────────────────────────────────────────────────────────

    static SidebarRowPlan Plan(SidebarSectionSpec section, SidebarSectionSlice? slice = null,
        IReadOnlyList<SidebarLibraryEntry>? pool = null)
    {
        var slices = new SidebarExtensionSlices();
        if (slice is { } s) slices.Set(section.Id, s);
        var input = new SidebarProjectionInput
        {
            ExtensionEntries = pool,
            ExtensionSlices = slices,
            Revision = 3,
        };
        return SidebarRowPlanner.Build(Doc(section), in input);
    }

    [Fact]
    public void An_extension_section_plans_one_row_per_contributed_entry()
    {
        var pool = new List<SidebarLibraryEntry> { Playlist("1", "Alpha"), Album("9", "Ceremony") };
        var plan = Plan(ExtSection("sec_1", "library"), new SidebarSectionSlice(0, 2), pool);

        Assert.Equal(new[] { SidebarRowKind.EntityRow, SidebarRowKind.EntityRow }, KindsOf(plan));
        Assert.Equal("sec_1", plan.Rows[0].SectionId);
        Assert.Equal(pool[0].Id, plan.Rows[0].Key);                       // projected rows join by entry id
        Assert.True(plan.Rows[0].EntryIndex >= 0);
        Assert.Equal(pool[0].Id, plan.Entries[plan.Rows[0].EntryIndex].Id);
        Assert.Equal(3, plan.Revision);
    }

    [Fact]
    public void A_missing_contribution_plans_exactly_one_manage_extension_prompt_row()
    {
        foreach (var availability in new[]
                 {
                     SidebarContributionAvailability.Missing,
                     SidebarContributionAvailability.Disabled,
                     SidebarContributionAvailability.Incompatible,
                 })
        {
            var plan = Plan(ExtSection("sec_1", "library"),
                new SidebarSectionSlice(0, 0, SidebarSourceState.Error, availability));
            Assert.Equal(new[] { SidebarRowKind.PromptRow }, KindsOf(plan));
            Assert.Equal("sec_1", plan.Rows[0].Key);
        }
    }

    [Fact]
    public void An_unresolved_section_with_no_slice_at_all_still_plans_the_prompt_row()
    {
        Assert.Equal(new[] { SidebarRowKind.PromptRow }, KindsOf(Plan(ExtSection("sec_1", "library"))));
    }

    [Fact]
    public void A_pending_contribution_plans_skeletons()
    {
        var plan = Plan(ExtSection("sec_1", "library"), new SidebarSectionSlice(0, 0, SidebarSourceState.Pending));
        Assert.Equal(new[] { SidebarRowKind.Skeleton, SidebarRowKind.Skeleton, SidebarRowKind.Skeleton }, KindsOf(plan));
    }

    [Fact]
    public void A_ready_but_empty_contribution_plans_the_empty_row()
    {
        var plan = Plan(ExtSection("sec_1", "library"), new SidebarSectionSlice(0, 0));
        Assert.Equal(new[] { SidebarRowKind.Empty }, KindsOf(plan));
    }

    [Fact]
    public void An_actionable_empty_contribution_plans_a_prompt_row_instead_of_an_empty_one()
    {
        var plan = Plan(ExtSection("sec_1", "concerts"),
            new SidebarSectionSlice(0, 0, SidebarSourceState.Ready, SidebarContributionAvailability.Live,
                                    NeedsPrompt: true));
        Assert.Equal(new[] { SidebarRowKind.PromptRow }, KindsOf(plan));
    }

    [Fact]
    public void A_stale_window_degrades_to_empty_instead_of_indexing_out_of_range()
    {
        var pool = new List<SidebarLibraryEntry> { Playlist("1", "Alpha") };
        var plan = Plan(ExtSection("sec_1", "library"), new SidebarSectionSlice(5, 3), pool);
        Assert.Equal(new[] { SidebarRowKind.Empty }, KindsOf(plan));
    }

    [Fact]
    public void A_window_that_overruns_the_pool_is_clamped()
    {
        var pool = new List<SidebarLibraryEntry> { Playlist("1", "Alpha"), Album("9", "Ceremony") };
        var plan = Plan(ExtSection("sec_1", "library"), new SidebarSectionSlice(1, 5), pool);
        Assert.Equal(new[] { SidebarRowKind.EntityRow }, KindsOf(plan));
        Assert.Equal(pool[1].Id, plan.Rows[0].Key);
    }

    [Fact]
    public void An_extension_section_contributes_one_rail_tile()
    {
        var pool = new List<SidebarLibraryEntry> { Playlist("1", "Alpha"), Album("9", "Ceremony") };
        var slices = new SidebarExtensionSlices();
        slices.Set("sec_1", new SidebarSectionSlice(0, 2));
        var input = new SidebarProjectionInput { ExtensionEntries = pool, ExtensionSlices = slices };

        var rail = SidebarRowPlanner.BuildRail(Doc(ExtSection("sec_1", "library")), in input);
        Assert.Equal(new[] { SidebarRowKind.IconRow }, KindsOf(rail));
        Assert.Equal("sec_1", rail.Rows[0].Key);
    }

    // ── the query's include/exclude uri sets (m1a's SidebarEntityQuery fields, enforced by the planner) ───────────────

    static SidebarRowPlan PlanEntityList(SidebarEntityQuery query)
    {
        var section = new SidebarSectionSpec("sec_q", SidebarSectionKind.EntityList, null, null) { Query = query };
        var input = new SidebarProjectionInput { Library = Library, Revision = 1 };
        return SidebarRowPlanner.Build(Doc(section), in input);
    }

    [Fact]
    public void An_include_set_is_a_whitelist()
    {
        var plan = PlanEntityList(new SidebarEntityQuery(SidebarEntityKinds.All)
        {
            IncludeUris = new[] { "spotify:playlist:2" },
        });
        Assert.Single(plan.Rows);
        Assert.Equal(SidebarPinId.PlaylistPrefix + "spotify:playlist:2", plan.Rows[0].Key);
    }

    [Fact]
    public void An_exclude_set_wins_over_include_and_over_everything_else()
    {
        var plan = PlanEntityList(new SidebarEntityQuery(SidebarEntityKinds.All)
        {
            IncludeUris = new[] { "spotify:playlist:1", "spotify:playlist:2" },
            ExcludeUris = new[] { "spotify:playlist:1" },
        });
        Assert.Single(plan.Rows);
        Assert.Equal(SidebarPinId.PlaylistPrefix + "spotify:playlist:2", plan.Rows[0].Key);
    }

    [Fact]
    public void An_entry_id_also_satisfies_the_uri_sets()
    {
        var plan = PlanEntityList(new SidebarEntityQuery(SidebarEntityKinds.All)
        {
            IncludeUris = new[] { SidebarPinId.AlbumPrefix + "spotify:album:9" },
        });
        Assert.Single(plan.Rows);
        Assert.Equal(SidebarPinId.AlbumPrefix + "spotify:album:9", plan.Rows[0].Key);
    }

    [Fact]
    public void No_uri_sets_means_every_kind_matching_entry_passes()
    {
        Assert.Equal(4, PlanEntityList(SidebarEntityQuery.Default).Rows.Count);
    }

    [Fact]
    public void A_hidden_extension_section_contributes_nothing()
    {
        var section = ExtSection("sec_1", "library") with { Hidden = true };
        Assert.Empty(Plan(section, new SidebarSectionSlice(0, 0)).Rows);
    }
}
