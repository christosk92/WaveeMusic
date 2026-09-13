// ── Wavee.Tests/SidebarDocTests.cs — the sidebar-layout.json document, wire, migrations and defaults ───────────────
//
// The 0.3 gate for Shell/Sidebar.Doc.cs (CORE: the document + the wire + the version ladder + the built-in
// documents — nothing here opens a stream, that is Sidebar.Host.cs). Ported from 0.2.9's SidebarLayoutJsonTests.cs,
// SidebarLayoutV2MigrationTests.cs and SidebarPinKindWireTests.cs (src/apps/_old/Wavee.Tests), folded into one file
// because all three exercise the same three rules Sidebar.Doc.cs's own header names:
//
//   1. The on-disk format is 0.2.9's, byte for byte — every [JsonPropertyName], every default, every version
//      constant is frozen, and persisted enums are append-only.
//   2. Nothing vanishes — an unrecognised section KIND round-trips untouched at its original index, and unknown
//      MEMBERS anywhere survive through [JsonExtensionData], re-attached by owning id (SidebarWireCarry).
//   3. The version ladder is total, in-memory, preserving, and steps one version at a time.
//
// Pure: no disk, no store, no engine loop, no window, no network — every fact here is Parse/Serialize plus
// SidebarLayoutWire/SidebarLayoutMigrations/SidebarLayoutDefaults called directly on in-memory DTOs and records.
// TestScope is not used: the document has no Entities dependency, so no class here needs EntitiesCollection.
//
// NOT ported here — belongs to the file-I/O gate (SidebarLayoutStoreTests, not yet ported): atomic write, the .bak
// dance, corruption handling, SidebarLoadFault/SidebarSaveFault, and the v3+ "too new" gate. Those all need
// SidebarLayoutStore, which lives in Sidebar.Host.cs — a 14-line stub with an empty partial-class body in this
// tree today (see MISMATCH in the porting report). SidebarBootstrapTests.cs contributed nothing portable: every
// fact in that file is about SidebarBootstrap's settings-migration/fresh-install-witness behaviour (a different
// subsystem entirely, backed by Wavee.Backend.Persistence types), never about the layout DOCUMENT itself.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarDocTests
{
    // ── shared helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    static string Json(SidebarLayoutDocDto doc) =>
        JsonSerializer.Serialize(doc, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto);

    static SidebarLayoutDocDto Parse(string json) =>
        JsonSerializer.Deserialize(json, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto)!;

    static SidebarLayoutDocDto Envelope(SidebarCustomLayout layout, SidebarWireCarry? carry = null) => new()
    {
        Version = SidebarLayoutStore.CurrentVersion,
        Curated = SidebarLayoutWire.WriteCurated(layout, carry),
    };

    static SidebarLayoutDocDto TopBarEnvelope(SidebarCustomLayout layout, SidebarWireCarry? carry = null) => new()
    {
        Version = SidebarLayoutStore.CurrentVersion,
        Curated = SidebarLayoutWire.WriteCurated(layout, carry),
        TopBar = SidebarLayoutWire.WriteTopBar(layout.TopBar, carry),
    };

    /// <summary>Deep structural equality for a layout. Record equality is not enough: SidebarSectionSpec's
    /// Items/Children are IReadOnlyList members, which records compare by REFERENCE.</summary>
    static void AssertLayoutEqual(SidebarCustomLayout a, SidebarCustomLayout b)
    {
        Assert.Equal(a.TemplateId, b.TemplateId);
        Assert.Equal(a.Sections.Count, b.Sections.Count);
        for (int i = 0; i < a.Sections.Count; i++) AssertSectionEqual(a.Sections[i], b.Sections[i]);
    }

    static void AssertSectionEqual(SidebarSectionSpec a, SidebarSectionSpec b)
    {
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.Title, b.Title);
        Assert.Equal(a.TitleLocKey, b.TitleLocKey);
        Assert.Equal(a.Hidden, b.Hidden);
        Assert.Equal(a.Collapsed, b.Collapsed);
        Assert.Equal(a.Opts, b.Opts);                     // SidebarDisplayOptions is a flat record — value equality is right
        Assert.Equal(a.Query, b.Query);                   // SidebarEntityQuery declares list-aware equality (v2 uri sets)
        Assert.Equal(a.Extension, b.Extension);           // SidebarExtensionRef compares its config by raw JSON
        Assert.Equal(a.ItemList.Count, b.ItemList.Count);
        for (int i = 0; i < a.ItemList.Count; i++) Assert.Equal(a.ItemList[i], b.ItemList[i]);   // flat record
        Assert.Equal(a.ChildList.Count, b.ChildList.Count);
        for (int i = 0; i < a.ChildList.Count; i++) AssertSectionEqual(a.ChildList[i], b.ChildList[i]);
    }

    static SidebarItemSpec Item(string id, SidebarItemTarget target, string key) =>
        new(id, target, key)
        {
            EntityKind = target == SidebarItemTarget.Track ? SidebarEntityKind.Track : SidebarEntityKind.Playlist,
            LabelOverride = "Alias " + id,
            IconOverride = "Heart",
            FallbackTitle = "Last known " + id,
            FallbackImageUrl = "https://i.example/" + id + ".jpg",
            Hidden = true,
            Action = target == SidebarItemTarget.Action
                ? new SidebarActionBinding("wavee", "queue.addNext", SidebarActionTargetMode.FixedTrack,
                    "spotify:track:4uLU6hMCjMI75M1A2tKUQC", SidebarJson.Detach("""{"position":"end"}"""))
                : null,
        };

    /// <summary>One section of EVERY kind, every display field pushed off its default, items with every override
    /// set, a CustomGroup with two children, and an EntityList with a non-default query.</summary>
    static SidebarCustomLayout EveryKindLayout()
    {
        var opts = SidebarDisplayOptions.Default with
        {
            Density = SidebarDensity.Comfortable,
            Presentation = SidebarPresentation.Grid,
            Artwork = false,
            Subtitles = false,
            CountBadges = true,
            CollapsedByDefault = true,
            ShowInRail = false,
            MaxItems = 7,
            GridColumns = 4,
            InlineControls = true,
            PlayButton = false,
            Recents = SidebarRecentsSource.Played,
            EmptyBehavior = SidebarEmptyBehavior.ActionCard,
        };

        var kinds = new[]
        {
            SidebarSectionKind.Pinned, SidebarSectionKind.JumpBackIn, SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.PlaylistTree, SidebarSectionKind.EntityList, SidebarSectionKind.StaticLinks,
            SidebarSectionKind.CustomGroup, SidebarSectionKind.Header, SidebarSectionKind.Divider,
            SidebarSectionKind.EntityEmbed, SidebarSectionKind.NewReleases, SidebarSectionKind.Concerts,
            SidebarSectionKind.Extension,
        };

        var sections = new List<SidebarSectionSpec>(kinds.Length);
        for (int i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            string id = "sec_" + i.ToString("x8");
            var spec = new SidebarSectionSpec(id, kind)
            {
                Title = "Title " + i,
                TitleLocKey = "sidebar.section.test" + i,
                Hidden = true,
                Collapsed = true,
                Display = opts,
                Items =
                [
                    Item(id + "_a", SidebarItemTarget.Route, "liked"),
                    Item(id + "_b", SidebarItemTarget.Entity, "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO"),
                    Item(id + "_c", SidebarItemTarget.Track, "spotify:track:4uLU6hMCjMI75M1A2tKUQC"),
                    Item(id + "_d", SidebarItemTarget.Action, "wavee.queue.addNext"),
                ],
            };

            if (kind == SidebarSectionKind.EntityList)
                spec = spec with
                {
                    Query = new SidebarEntityQuery(
                        Kinds: SidebarEntityKinds.Playlists | SidebarEntityKinds.Shows,
                        Sort: SidebarSortMode.CustomOrder,
                        Descending: false,
                        Qualifier: SidebarPlaylistQualifier.BySpotify,
                        IncludeUris: ["spotify:artist:1", "spotify:artist:2"],
                        ExcludeUris: ["spotify:playlist:noisy"]),
                };

            if (kind == SidebarSectionKind.Extension)
                spec = spec with
                {
                    Extension = new SidebarExtensionRef("wavee", "artist.topTracks", 3,
                        SidebarJson.Detach("""{"artistUri":"spotify:artist:7","limit":5,"nested":{"deep":[1,2,3]}}""")),
                };

            if (kind == SidebarSectionKind.CustomGroup)
                spec = spec with
                {
                    Children =
                    [
                        new SidebarSectionSpec(id + "_k1", SidebarSectionKind.StaticLinks)
                        {
                            Title = "Child one",
                            Items = [Item(id + "_k1_a", SidebarItemTarget.Route, "history")],
                        },
                        new SidebarSectionSpec(id + "_k2", SidebarSectionKind.EntityEmbed)
                        {
                            Display = SidebarDisplayOptions.Default with { PlayButton = false },
                            Items = [Item(id + "_k2_a", SidebarItemTarget.Entity, "spotify:album:1DFixLWuPkv3KT3TnV35m3")],
                        },
                    ],
                };

            sections.Add(spec);
        }

        return new SidebarCustomLayout(SidebarTemplates.Curated, sections);
    }

    // ── region: wire vocabulary and round trip (from 0.2.9 SidebarLayoutJsonTests.cs) ──────────────────────────────────

    [Fact]
    public void Every_section_kind_round_trips_through_the_wire_unchanged()
    {
        var layout = EveryKindLayout();
        var back = SidebarLayoutWire.ReadCurated(Parse(Json(Envelope(layout))).Curated).Layout;
        AssertLayoutEqual(layout, back);
    }

    [Fact]
    public void Every_kind_and_enum_string_is_the_bound_camelCase_spelling()
    {
        Assert.Equal("pinned", SidebarLayoutWire.KindName(SidebarSectionKind.Pinned));
        Assert.Equal("jumpBackIn", SidebarLayoutWire.KindName(SidebarSectionKind.JumpBackIn));
        Assert.Equal("collectionShortcuts", SidebarLayoutWire.KindName(SidebarSectionKind.CollectionShortcuts));
        Assert.Equal("playlistTree", SidebarLayoutWire.KindName(SidebarSectionKind.PlaylistTree));
        Assert.Equal("entityList", SidebarLayoutWire.KindName(SidebarSectionKind.EntityList));
        Assert.Equal("staticLinks", SidebarLayoutWire.KindName(SidebarSectionKind.StaticLinks));
        Assert.Equal("customGroup", SidebarLayoutWire.KindName(SidebarSectionKind.CustomGroup));
        Assert.Equal("header", SidebarLayoutWire.KindName(SidebarSectionKind.Header));
        Assert.Equal("divider", SidebarLayoutWire.KindName(SidebarSectionKind.Divider));
        Assert.Equal("entityEmbed", SidebarLayoutWire.KindName(SidebarSectionKind.EntityEmbed));
        Assert.Equal("newReleases", SidebarLayoutWire.KindName(SidebarSectionKind.NewReleases));
        Assert.Equal("concerts", SidebarLayoutWire.KindName(SidebarSectionKind.Concerts));
        Assert.Equal("extension", SidebarLayoutWire.KindName(SidebarSectionKind.Extension));

        Assert.Equal("track", SidebarLayoutWire.TargetName(SidebarItemTarget.Track));
        Assert.Equal("track", SidebarLayoutWire.EntityKindName(SidebarEntityKind.Track));
        Assert.Equal("visited", SidebarLayoutWire.RecentsName(SidebarRecentsSource.Visited));
        Assert.Equal("played", SidebarLayoutWire.RecentsName(SidebarRecentsSource.Played));
        Assert.Equal("hideBody", SidebarLayoutWire.EmptyBehaviorName(SidebarEmptyBehavior.HideBody));
        Assert.Equal("compactHint", SidebarLayoutWire.EmptyBehaviorName(SidebarEmptyBehavior.CompactHint));
        Assert.Equal("actionCard", SidebarLayoutWire.EmptyBehaviorName(SidebarEmptyBehavior.ActionCard));

        Assert.Equal("action", SidebarLayoutWire.TargetName(SidebarItemTarget.Action));
        Assert.Equal("none", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.None));
        Assert.Equal("fixedEntity", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.FixedEntity));
        Assert.Equal("fixedTrack", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.FixedTrack));
        Assert.Equal("nowPlaying", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.NowPlaying));
        Assert.Equal("activeRoute", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.ActiveRoute));

        foreach (var kind in Enum.GetValues<SidebarSectionKind>())
        {
            Assert.True(SidebarLayoutWire.TryParseKind(SidebarLayoutWire.KindName(kind), out var back), kind.ToString());
            Assert.Equal(kind, back);
        }
        foreach (var mode in Enum.GetValues<SidebarActionTargetMode>())
            Assert.Equal(mode, SidebarLayoutWire.ParseTargetMode(SidebarLayoutWire.TargetModeName(mode)));
        foreach (var target in Enum.GetValues<SidebarItemTarget>())
            Assert.Equal(target, SidebarLayoutWire.ParseTarget(SidebarLayoutWire.TargetName(target)));
    }

    [Fact]
    public void The_serialized_document_is_camelCase_members_and_string_keyed_enums()
    {
        string json = Json(Envelope(EveryKindLayout()));
        Assert.Contains("\"version\": 2", json);
        Assert.Contains("\"curated\"", json);
        Assert.Contains("\"kind\": \"entityEmbed\"", json);
        Assert.Contains("\"kind\": \"newReleases\"", json);
        Assert.Contains("\"kind\": \"concerts\"", json);
        Assert.Contains("\"target\": \"track\"", json);
        Assert.Contains("\"inlineControls\": true", json);
        Assert.Contains("\"playButton\": false", json);
        Assert.Contains("\"recents\": \"played\"", json);
        Assert.DoesNotContain("\"Kind\"", json);   // no PascalCase leaked through

        Assert.Contains("\"kind\": \"extension\"", json);
        Assert.Contains("\"extension\"", json);
        Assert.Contains("\"extensionId\": \"wavee\"", json);
        Assert.Contains("\"contributionId\": \"artist.topTracks\"", json);
        Assert.Contains("\"schemaVersion\": 3", json);
        Assert.Contains("\"artistUri\"", json);                    // the opaque config, verbatim
        Assert.Contains("\"target\": \"action\"", json);
        Assert.Contains("\"providerId\": \"wavee\"", json);
        Assert.Contains("\"actionId\": \"queue.addNext\"", json);
        Assert.Contains("\"targetMode\": \"fixedTrack\"", json);
        Assert.Contains("\"includeUris\"", json);
        Assert.Contains("\"excludeUris\"", json);
        Assert.DoesNotContain("\"ExtensionId\"", json);
        Assert.DoesNotContain("\"TargetMode\"", json);
    }

    [Fact]
    public void Default_display_options_cost_no_bytes_on_the_wire()
    {
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
        [
            new SidebarSectionSpec("sec_1", SidebarSectionKind.Pinned),   // Display == null ⇒ Default
        ]);
        string json = Json(Envelope(layout));
        Assert.DoesNotContain("\"display\"", json);
        Assert.DoesNotContain("\"items\"", json);
        Assert.DoesNotContain("\"hidden\"", json);      // false is not written
        Assert.DoesNotContain("\"collapsed\"", json);

        var back = SidebarLayoutWire.ReadCurated(Parse(json).Curated).Layout;
        Assert.Null(back.Sections[0].Display);
        Assert.Equal(SidebarDisplayOptions.Default, back.Sections[0].Opts);
    }

    [Theory]
    [InlineData(SidebarEmptyBehavior.HideBody, "hideBody")]
    [InlineData(SidebarEmptyBehavior.CompactHint, "compactHint")]
    [InlineData(SidebarEmptyBehavior.ActionCard, "actionCard")]
    public void An_authored_empty_behavior_round_trips_as_the_optional_wire_string(
        SidebarEmptyBehavior behavior, string wire)
    {
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
        [
            new SidebarSectionSpec("sec_empty", SidebarSectionKind.JumpBackIn)
            {
                Display = SidebarDisplayOptions.Default with { EmptyBehavior = behavior },
            },
        ]);

        string json = Json(Envelope(layout));
        Assert.Contains($"\"emptyBehavior\": \"{wire}\"", json);
        var back = SidebarLayoutWire.ReadCurated(Parse(json).Curated).Layout;
        Assert.Equal(behavior, back.Sections[0].Opts.EmptyBehavior);
    }

    [Fact]
    public void An_empty_v1_envelope_round_trips_with_nothing_invented()
    {
        var doc = new SidebarLayoutDocDto { Version = 1 };
        var back = Parse(Json(doc));
        Assert.Equal(1, back.Version);
        Assert.Null(back.Pins);
        Assert.Null(back.V3);
        Assert.Null(back.Curated);
        Assert.Equal(SidebarCustomLayout.Empty.Sections.Count,
            SidebarLayoutWire.ReadCurated(back.Curated).Layout.Sections.Count);
    }

    [Fact]
    public void Pins_and_the_v3_overlay_round_trip()
    {
        var doc = new SidebarLayoutDocDto
        {
            Version = 1,
            Pins =
            [
                new SidebarPinDto { Id = "liked", Kind = 0, Uri = "spotify:collection:tracks", Name = "Liked Songs", AddedAtMs = 1753013400000 },
                new SidebarPinDto { Id = "folder:6a1f2c", Kind = 5, Uri = "", Name = "Cafe & chill", AddedAtMs = 1753301100000 },
            ],
            V3 = new SidebarV3Dto
            {
                CustomOrder = ["pl:a", "pl:b"],
                ExpandedFolders = ["6a1f2c"],
                FirstSeen = [new SidebarFirstSeenDto("pl:b", 1753578000000)],
            },
        };

        var back = Parse(Json(doc));
        Assert.Equal(2, back.Pins!.Length);
        Assert.Equal("liked", back.Pins[0].Id);
        Assert.Equal(5, back.Pins[1].Kind);
        Assert.Equal("Cafe & chill", back.Pins[1].Name);
        Assert.Equal(new[] { "pl:a", "pl:b" }, back.V3!.CustomOrder);
        Assert.Equal(new[] { "6a1f2c" }, back.V3.ExpandedFolders);
        Assert.Equal("pl:b", back.V3.FirstSeen![0].Id);
        Assert.Equal(1753578000000, back.V3.FirstSeen[0].Ms);
    }

    // ── the shell top-bar band (envelope-level, additive) ───────────────────────────────────────────────────────────────

    [Fact]
    public void TopBar_absent_reads_as_never_customized_and_resolves_to_the_built_in_home()
    {
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
            [new SidebarSectionSpec("sec_1", SidebarSectionKind.Pinned)]);
        Assert.Null(layout.TopBar);

        string json = Json(TopBarEnvelope(layout));
        Assert.DoesNotContain("\"topBar\"", json);          // null ⇒ omitted by WhenWritingNull

        var back = Parse(json);
        Assert.Null(back.TopBar);
        var read = SidebarLayoutWire.ReadTopBar(back.TopBar);
        Assert.Null(read);                                   // still "never customized"…
        Assert.Single(new SidebarCustomLayout("x", layout.Sections, read).EffectiveTopBar);   // …i.e. the built-in Home
    }

    [Fact]
    public void TopBar_round_trips_every_item_target_as_an_envelope_member()
    {
        var band = new SidebarItemSpec[]
        {
            Item("itm_tb_a", SidebarItemTarget.Route, "liked"),
            Item("itm_tb_b", SidebarItemTarget.Entity, "spotify:album:1DFixLWuPkv3KT3TnV35m3"),
            Item("itm_tb_c", SidebarItemTarget.Track, "spotify:track:4uLU6hMCjMI75M1A2tKUQC"),
            Item("itm_tb_d", SidebarItemTarget.Action, "wavee.queue.addNext"),
        };
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
            [new SidebarSectionSpec("sec_1", SidebarSectionKind.Pinned)], band);

        string json = Json(TopBarEnvelope(layout));
        Assert.Contains("\"topBar\"", json);                 // an ENVELOPE member, beside pins — not inside curated
        Assert.DoesNotContain("\"TopBar\"", json);

        var back = Parse(json);
        Assert.Null(back.Extra);                             // recognized, never swallowed as an unknown member
        var read = SidebarLayoutWire.ReadTopBar(back.TopBar);
        Assert.NotNull(read);
        Assert.Equal(band.Length, read!.Count);
        for (int i = 0; i < band.Length; i++) Assert.Equal(band[i], read[i]);   // flat record: value equality is right
    }

    [Fact]
    public void An_emptied_top_bar_survives_as_empty_not_as_absent()
    {
        // "The user removed every shortcut" must never read back as "never customized" — that would restore Home.
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
            [new SidebarSectionSpec("sec_1", SidebarSectionKind.Pinned)], Array.Empty<SidebarItemSpec>());

        string json = Json(TopBarEnvelope(layout));
        Assert.Contains("\"topBar\": []", json);

        var read = SidebarLayoutWire.ReadTopBar(Parse(json).TopBar);
        Assert.NotNull(read);
        Assert.Empty(read!);
        Assert.Empty(new SidebarCustomLayout("x", layout.Sections, read).EffectiveTopBar);
    }

    [Fact]
    public void An_unknown_member_on_a_top_bar_tile_rides_the_carry()
    {
        const string json = """
        {
          "version": 2,
          "topBar": [
            { "id": "itm_tb_a", "target": "route", "key": "liked", "futureTileMember": { "x": 1 } }
          ],
          "curated": { "templateId": "curated", "sections": [] }
        }
        """;
        var dto = Parse(json);
        var carry = new SidebarWireCarry();
        var band = SidebarLayoutWire.ReadTopBar(dto.TopBar, carry);
        Assert.NotNull(band);
        Assert.Single(band!);

        var again = SidebarLayoutWire.WriteTopBar(band, carry);
        string reemitted = Json(new SidebarLayoutDocDto { Version = 2, TopBar = again });
        Assert.Contains("\"futureTileMember\"", reemitted);
    }

    [Fact]
    public void Unknown_envelope_and_curated_members_ride_the_carry()
    {
        // The band's own forward-compat premise: an ADDITIVE member a newer build writes at the envelope (or on the
        // curated payload object) must survive a save by an older build. Both levels rebuild their DTO from scratch,
        // so both need an explicit carry — this is the regression test for that.
        const string json = """
        {
          "version": 2,
          "futureEnvelopeMember": { "shape": "unknown" },
          "curated": { "templateId": "curated", "sections": [], "futureCuratedMember": [1, 2] }
        }
        """;
        var dto = Parse(json);
        Assert.NotNull(dto.Extra);
        Assert.NotNull(dto.Curated!.Extra);

        var read = SidebarLayoutWire.ReadCurated(dto.Curated);
        read.Carry.CaptureDoc(dto);

        var snapshot = new SidebarLayoutDocDto
        {
            Version = SidebarLayoutStore.CurrentVersion,
            Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry),
            TopBar = SidebarLayoutWire.WriteTopBar(read.Layout.TopBar, read.Carry),
        };
        read.Carry.ReattachDoc(snapshot);

        string reemitted = Json(snapshot);
        Assert.Contains("\"futureEnvelopeMember\"", reemitted);
        Assert.Contains("\"futureCuratedMember\"", reemitted);
    }

    [Fact]
    public void A_large_document_round_trips_within_the_smoke_bounds()
    {
        var sections = new List<SidebarSectionSpec>(40);
        for (int s = 0; s < 40; s++)
        {
            var items = new SidebarItemSpec[500];
            for (int i = 0; i < items.Length; i++)
                items[i] = new SidebarItemSpec($"itm_{s:x2}{i:x4}", SidebarItemTarget.Entity, $"spotify:playlist:{s}_{i}");
            sections.Add(new SidebarSectionSpec($"sec_{s:x8}", SidebarSectionKind.CustomGroup) { Items = items });
        }
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated, sections);

        long start = Environment.TickCount64;
        string json = Json(Envelope(layout));
        var back = SidebarLayoutWire.ReadCurated(Parse(json).Curated).Layout;
        long elapsed = Environment.TickCount64 - start;

        AssertLayoutEqual(layout, back);
        // Smoke bounds, NOT perf gates. WriteIndented = true is fixed on the context (the document is deliberately
        // user-inspectable), which roughly doubles the payload and costs a first-run JIT pass; a 20 000-item
        // document is also ~100x anything a real user builds.
        Assert.True(json.Length < 4 * 1024 * 1024, $"payload {json.Length} B exceeds the 4 MB smoke bound");
        Assert.True(elapsed < 5000, $"round trip took {elapsed} ms — far past the smoke bound");
    }

    // ── forward compatibility: the load-bearing rules ─────────────────────────────────────────────────────────────────

    const string NewerBuildJson = """
    {
      "version": 1,
      "updatedAtMs": 1753893041233,
      "appVersion": "99.9.9",
      "curated": {
        "templateId": "curated",
        "sections": [
          { "id": "sec_known", "kind": "pinned", "display": { "density": "compact", "wobble": 42 }, "gravity": "down" },
          { "id": "sec_future", "kind": "quantumFeed", "title": "From tomorrow",
            "options": { "resonance": 3 }, "items": [ { "id": "itm_x", "key": "spotify:thing:1" } ] },
          { "id": "sec_group", "kind": "customGroup",
            "items": [ { "id": "itm_k", "target": "track", "key": "spotify:track:z", "aura": "violet" } ] }
        ]
      },
      "telemetryOptIn": true
    }
    """;

    [Fact]
    public void An_unrecognized_section_kind_round_trips_untouched_at_its_original_index()
    {
        var read = SidebarLayoutWire.ReadCurated(Parse(NewerBuildJson).Curated);

        // The unknown kind is NOT a section this build renders…
        Assert.Equal(2, read.Layout.Sections.Count);
        Assert.Equal("sec_known", read.Layout.Sections[0].Id);
        Assert.Equal("sec_group", read.Layout.Sections[1].Id);
        // …but it IS preserved, and it comes back at its original index on the next save.
        Assert.Equal(1, read.Carry.UnknownSectionCount);

        string resaved = Json(new SidebarLayoutDocDto
        {
            Version = 1,
            Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry),
        });

        Assert.Contains("\"kind\": \"quantumFeed\"", resaved);
        Assert.Contains("\"sec_future\"", resaved);
        Assert.Contains("\"resonance\"", resaved);          // the unknown section's own unknown members survive too
        Assert.Contains("\"From tomorrow\"", resaved);

        var reparsed = Parse(resaved).Curated!.Sections!;
        Assert.Equal(3, reparsed.Length);
        Assert.Equal("sec_known", reparsed[0].Id);
        Assert.Equal("sec_future", reparsed[1].Id);         // index 1, exactly where the newer build put it
        Assert.Equal("sec_group", reparsed[2].Id);
    }

    [Fact]
    public void Unknown_members_on_known_sections_survive_the_model_hop()
    {
        var read = SidebarLayoutWire.ReadCurated(Parse(NewerBuildJson).Curated);
        string resaved = Json(new SidebarLayoutDocDto
        {
            Version = 1,
            Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry),
        });

        Assert.Contains("\"gravity\"", resaved);   // an unknown SECTION member
        Assert.Contains("\"wobble\"", resaved);    // an unknown DISPLAY member
        Assert.Contains("\"aura\"", resaved);      // an unknown ITEM member (matched back by item id)
    }

    [Fact]
    public void Unknown_document_members_survive_the_envelope()
    {
        var doc = Parse(NewerBuildJson);
        Assert.NotNull(doc.Extra);
        Assert.True(doc.Extra!.ContainsKey("telemetryOptIn"));
        Assert.Contains("\"telemetryOptIn\"", Json(doc));
    }

    [Fact]
    public void An_older_build_document_reads_with_defaults_for_the_newer_fields()
    {
        // A document written before the extended kind/field catalog existed: only the original nine kinds, and
        // none of the three newer display fields.
        const string olderJson = """
        {
          "version": 1,
          "curated": {
            "templateId": "curated",
            "sections": [
              { "id": "sec_1", "kind": "jumpBackIn", "display": { "maxItems": 4 } },
              { "id": "sec_2", "kind": "entityList", "query": { "kinds": ["playlists"], "sort": "alphabetical", "descending": false } }
            ]
          }
        }
        """;

        var layout = SidebarLayoutWire.ReadCurated(Parse(olderJson).Curated).Layout;
        Assert.Equal(2, layout.Sections.Count);

        var jump = layout.Sections[0];
        Assert.Equal(4, jump.Opts.MaxItems);
        Assert.Equal(SidebarRecentsSource.Visited, jump.Opts.Recents);
        Assert.False(jump.Opts.InlineControls);
        Assert.True(jump.Opts.PlayButton);

        var list = layout.Sections[1];
        Assert.Equal(SidebarEntityKinds.Playlists, list.Query!.Kinds);
        Assert.Equal(SidebarSortMode.Alphabetical, list.Query.Sort);
        Assert.False(list.Query.Descending);
        Assert.Equal(SidebarPlaylistQualifier.Any, list.Query.Qualifier);
    }

    [Fact]
    public void Newer_build_kinds_and_display_fields_read_exactly()
    {
        const string json = """
        {
          "version": 1,
          "curated": {
            "templateId": "curated",
            "sections": [
              { "id": "sec_e", "kind": "entityEmbed", "display": { "playButton": false },
                "items": [ { "id": "itm_e", "target": "entity", "entityKind": "album", "key": "spotify:album:x" } ] },
              { "id": "sec_n", "kind": "newReleases", "display": { "maxItems": 4 } },
              { "id": "sec_c", "kind": "concerts", "display": { "maxItems": 3 } },
              { "id": "sec_j", "kind": "jumpBackIn", "display": { "recents": "played", "maxItems": 4 } },
              { "id": "sec_l", "kind": "entityList", "display": { "inlineControls": true } },
              { "id": "sec_g", "kind": "customGroup",
                "items": [ { "id": "itm_t", "target": "track", "entityKind": "track", "key": "spotify:track:y" } ] }
            ]
          }
        }
        """;

        var layout = SidebarLayoutWire.ReadCurated(Parse(json).Curated).Layout;
        Assert.Equal(6, layout.Sections.Count);
        Assert.Equal(SidebarSectionKind.EntityEmbed, layout.Sections[0].Kind);
        Assert.False(layout.Sections[0].Opts.PlayButton);
        Assert.Equal(SidebarEntityKind.Album, layout.Sections[0].ItemList[0].EntityKind);
        Assert.Equal(SidebarSectionKind.NewReleases, layout.Sections[1].Kind);
        Assert.Equal(4, layout.Sections[1].Opts.MaxItems);
        Assert.Equal(SidebarSectionKind.Concerts, layout.Sections[2].Kind);
        Assert.Equal(3, layout.Sections[2].Opts.MaxItems);
        Assert.Equal(SidebarRecentsSource.Played, layout.Sections[3].Opts.Recents);
        Assert.True(layout.Sections[4].Opts.InlineControls);
        Assert.Equal(SidebarItemTarget.Track, layout.Sections[5].ItemList[0].Target);
        Assert.Equal(SidebarEntityKind.Track, layout.Sections[5].ItemList[0].EntityKind);
    }

    [Fact]
    public void The_entity_kinds_flag_set_is_a_stable_string_array()
    {
        Assert.Equal(new[] { "playlists", "albums", "artists", "shows" }, SidebarLayoutWire.KindsNames(SidebarEntityKinds.All));
        Assert.Equal(new[] { "albums", "shows" }, SidebarLayoutWire.KindsNames(SidebarEntityKinds.Albums | SidebarEntityKinds.Shows));
        Assert.Equal(SidebarEntityKinds.All, SidebarLayoutWire.ParseKinds(["playlists", "albums", "artists", "shows"]));
        Assert.Equal(SidebarEntityKinds.All, SidebarLayoutWire.ParseKinds(null));            // absent ⇒ All (the model default)
        Assert.Equal(SidebarEntityKinds.Albums, SidebarLayoutWire.ParseKinds(["albums", "hologram"]));   // unknown flag ignored
    }

    [Fact]
    public void Unknown_enum_strings_degrade_to_the_model_default_without_throwing()
    {
        Assert.False(SidebarLayoutWire.TryParseKind("quantumFeed", out _));
        Assert.False(SidebarLayoutWire.TryParseKind(null, out _));
        Assert.Equal(SidebarItemTarget.Route, SidebarLayoutWire.ParseTarget("teleport"));
        Assert.Equal(SidebarEntityKind.None, SidebarLayoutWire.ParseEntityKind("hologram"));
        Assert.Equal(SidebarDensity.Cozy, SidebarLayoutWire.ParseDensity("gigantic"));
        Assert.Equal(SidebarPresentation.List, SidebarLayoutWire.ParsePresentation("carousel"));
        Assert.Equal(SidebarSortMode.Recents, SidebarLayoutWire.ParseSort("vibes"));
        Assert.Equal(SidebarPlaylistQualifier.Any, SidebarLayoutWire.ParseQualifier("byRobots"));
        Assert.Equal(SidebarRecentsSource.Visited, SidebarLayoutWire.ParseRecents("dreamt"));
        Assert.Equal(SidebarEmptyBehavior.Default, SidebarLayoutWire.ParseEmptyBehavior("billboard"));
        Assert.Equal(SidebarEmptyBehavior.Default, SidebarLayoutWire.ParseEmptyBehavior(null));
        Assert.Equal(SidebarActionTargetMode.None, SidebarLayoutWire.ParseTargetMode("telepathy"));
        Assert.Equal(SidebarActionTargetMode.None, SidebarLayoutWire.ParseTargetMode(null));
        Assert.Equal(SidebarActionTargetMode.None, SidebarLayoutWire.ParseTargetMode(""));
    }

    // ── v2: extension refs, action bindings, query uri sets ───────────────────────────────────────────────────────────

    [Fact]
    public void An_extension_section_round_trips_its_ref_and_opaque_config()
    {
        var config = SidebarJson.Detach("""{"artistUri":"spotify:artist:7","limit":5,"flags":{"live":true},"tags":["a","b"]}""");
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
        [
            new SidebarSectionSpec("sec_x", SidebarSectionKind.Extension)
            {
                Extension = new SidebarExtensionRef("wavee", "artist.topTracks", 3, config),
            },
        ]);

        var back = SidebarLayoutWire.ReadCurated(Parse(Json(Envelope(layout))).Curated).Layout.Sections[0];
        Assert.Equal(SidebarSectionKind.Extension, back.Kind);
        var x = back.Extension!;
        Assert.Equal("wavee", x.ExtensionId);
        Assert.Equal("artist.topTracks", x.ContributionId);
        Assert.Equal(3, x.SchemaVersion);
        Assert.Equal("wavee/artist.topTracks", x.ContributionKey);
        // The config comes back as the SAME JSON — content equality, not reference (the whole point of SidebarJson).
        Assert.Equal(5, x.Config.GetProperty("limit").GetInt32());
        Assert.True(x.Config.GetProperty("flags").GetProperty("live").GetBoolean());
        Assert.Equal(2, x.Config.GetProperty("tags").GetArrayLength());
        Assert.Equal(new SidebarExtensionRef("wavee", "artist.topTracks", 3, config), x);
    }

    [Fact]
    public void Config_from_an_unknown_extension_is_preserved_verbatim()
    {
        // A contribution this build has never heard of, with a config shape it cannot possibly understand: the
        // section is a known KIND, so it is not an opaque blob — the config must still survive byte-for-byte.
        const string json = """
        {
          "version": 2,
          "curated": {
            "templateId": "curated",
            "sections": [
              { "id": "sec_alien", "kind": "extension",
                "extension": { "extensionId": "acme.stats", "contributionId": "listening.heatmap", "schemaVersion": 9,
                               "config": { "buckets": [1, 2, 3], "palette": { "warm": "#f00" }, "weird": null },
                               "futureRefField": 42 } }
            ]
          }
        }
        """;

        var read = SidebarLayoutWire.ReadCurated(Parse(json).Curated);
        var x = read.Layout.Sections[0].Extension!;
        Assert.Equal("acme.stats", x.ExtensionId);
        Assert.Equal(9, x.SchemaVersion);
        Assert.Equal(3, x.Config.GetProperty("buckets").GetArrayLength());

        string resaved = Json(new SidebarLayoutDocDto
        {
            Version = SidebarLayoutStore.CurrentVersion,
            Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry),
        });
        Assert.Contains("\"acme.stats\"", resaved);
        Assert.Contains("\"listening.heatmap\"", resaved);
        Assert.Contains("\"palette\"", resaved);
        Assert.Contains("\"warm\"", resaved);
        Assert.Contains("\"buckets\"", resaved);
        Assert.Contains("\"futureRefField\"", resaved);   // an unknown member ON the ref survives via the carry
    }

    [Fact]
    public void An_extension_section_with_no_ref_stays_unbound_and_still_round_trips()
    {
        const string json = """
        { "version": 2, "curated": { "templateId": "curated",
          "sections": [ { "id": "sec_orphan", "kind": "extension" } ] } }
        """;

        var layout = SidebarLayoutWire.ReadCurated(Parse(json).Curated).Layout;
        Assert.Single(layout.Sections);
        Assert.Null(layout.Sections[0].Extension);
        Assert.True(layout.Sections[0].IsUnboundExtension);        // renders the "Manage extension" placeholder

        // …and re-saving keeps the section (never auto-removed) without inventing a ref.
        string resaved = Json(Envelope(layout));
        Assert.Contains("\"sec_orphan\"", resaved);
        Assert.DoesNotContain("\"extensionId\"", resaved);
    }

    [Theory]
    [InlineData(SidebarActionTargetMode.None, null)]
    [InlineData(SidebarActionTargetMode.FixedEntity, "spotify:playlist:1")]
    [InlineData(SidebarActionTargetMode.FixedTrack, "spotify:track:2")]
    [InlineData(SidebarActionTargetMode.NowPlaying, null)]
    [InlineData(SidebarActionTargetMode.ActiveRoute, null)]
    public void An_action_binding_round_trips_every_target_mode(SidebarActionTargetMode mode, string? targetKey)
    {
        var binding = new SidebarActionBinding("wavee", "play", mode, targetKey,
            SidebarJson.Detach("""{"shuffle":true}"""));
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
        [
            new SidebarSectionSpec("sec_g", SidebarSectionKind.CustomGroup)
            {
                Items = [new SidebarItemSpec("itm_a", SidebarItemTarget.Action, "wavee.play") { Action = binding }],
            },
        ]);

        var item = SidebarLayoutWire.ReadCurated(Parse(Json(Envelope(layout))).Curated).Layout.Sections[0].ItemList[0];
        Assert.Equal(SidebarItemTarget.Action, item.Target);
        Assert.Equal(binding, item.Action);                        // content equality incl. the arguments element
        Assert.Equal(mode, item.Action!.TargetMode);
        Assert.Equal(targetKey, item.Action.TargetKey);
        Assert.True(item.Action.Arguments!.Value.GetProperty("shuffle").GetBoolean());
    }

    [Fact]
    public void An_unknown_action_target_mode_degrades_to_none_without_throwing()
    {
        const string json = """
        { "version": 2, "curated": { "templateId": "curated", "sections": [
          { "id": "sec_g", "kind": "customGroup", "items": [
            { "id": "itm_a", "target": "action", "key": "acme.doThing",
              "action": { "providerId": "acme", "actionId": "doThing", "targetMode": "telepathy",
                          "targetKey": "spotify:track:9", "arguments": { "x": 1 } } },
            { "id": "itm_b", "target": "action", "key": "broken",
              "action": { "targetMode": "nowPlaying" } } ] } ] } }
        """;

        var items = SidebarLayoutWire.ReadCurated(Parse(json).Curated).Layout.Sections[0].ItemList;
        Assert.Equal(2, items.Count);
        Assert.Equal(SidebarActionTargetMode.None, items[0].Action!.TargetMode);
        Assert.Equal("acme.doThing", items[0].Action!.ActionKey);
        // The MODE degraded, not the binding: None needs no target key, so the row is still invokable (the stale
        // targetKey is simply unused — the reducer clears it the next time the binding is rewritten).
        Assert.True(items[0].Action!.IsResolvable);
        Assert.True(items[0].HasRunnableAction);
        Assert.Equal("spotify:track:9", items[0].Action!.TargetKey);
        // An id-less binding cannot address an action: it reads as "no binding" and the row renders disabled.
        Assert.Null(items[1].Action);
        Assert.Equal(SidebarItemTarget.Action, items[1].Target);
    }

    [Fact]
    public void Query_uri_sets_round_trip_and_an_empty_set_reads_back_as_null()
    {
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
        [
            new SidebarSectionSpec("sec_e", SidebarSectionKind.EntityList)
            {
                Query = SidebarEntityQuery.Default with
                {
                    Kinds = SidebarEntityKinds.Artists,
                    IncludeUris = ["spotify:artist:a", "spotify:artist:b"],
                    ExcludeUris = ["spotify:artist:c"],
                },
            },
        ]);

        var q = SidebarLayoutWire.ReadCurated(Parse(Json(Envelope(layout))).Curated).Layout.Sections[0].Query!;
        Assert.Equal(new[] { "spotify:artist:a", "spotify:artist:b" }, q.IncludeList);
        Assert.Equal(new[] { "spotify:artist:c" }, q.ExcludeList);
        Assert.True(q.HasIncludeSet);
        Assert.Equal(layout.Sections[0].Query, q);

        // `[]` (and a blank entry) on the wire is "no restriction", never "include nothing".
        const string emptySets = """
        { "version": 2, "curated": { "templateId": "curated", "sections": [
          { "id": "sec_e", "kind": "entityList", "query": { "includeUris": [], "excludeUris": ["", null] } } ] } }
        """;
        var empty = SidebarLayoutWire.ReadCurated(Parse(emptySets).Curated).Layout.Sections[0].Query!;
        Assert.Null(empty.IncludeUris);
        Assert.Null(empty.ExcludeUris);
        Assert.False(empty.HasIncludeSet);

        // A default query with no uri sets writes neither key.
        string bare = Json(Envelope(new SidebarCustomLayout(SidebarTemplates.Curated,
            [new SidebarSectionSpec("sec_e", SidebarSectionKind.EntityList) { Query = SidebarEntityQuery.Default }])));
        Assert.DoesNotContain("includeUris", bare);
        Assert.DoesNotContain("excludeUris", bare);
    }

    [Fact]
    public void A_v1_document_reads_with_null_extension_and_action()
    {
        // The v2 additions are all OPTIONAL: a v1 document has none of them and must read as "absent", not as a
        // default object (an empty extension ref would fabricate a contribution that does not exist).
        const string v1 = """
        { "version": 1, "curated": { "templateId": "curated", "sections": [
          { "id": "sec_p", "kind": "pinned", "items": [ { "id": "itm_1", "target": "entity", "key": "spotify:playlist:1" } ] },
          { "id": "sec_e", "kind": "entityList", "query": { "sort": "alphabetical" } } ] } }
        """;

        var layout = SidebarLayoutWire.ReadCurated(Parse(v1).Curated).Layout;
        Assert.Null(layout.Sections[0].Extension);
        Assert.Null(layout.Sections[0].ItemList[0].Action);
        Assert.Null(layout.Sections[1].Query!.IncludeUris);
        Assert.Null(layout.Sections[1].Query!.ExcludeUris);
        Assert.False(layout.Sections[0].IsExtension);
    }

    // ── region: pin kind — the dual legacy-int / string encoding (from 0.2.9 SidebarPinKindWireTests.cs) ──────────────
    //
    // 2026-08-19's unification deleted the old SidebarPinKind enum and its hand-written catch-all mapping:
    // SidebarPin.Kind IS a SidebarEntryKind now, and SidebarLayoutWire freezes the OLD numbering in a lookup table
    // instead of duplicating the domain type. These facts are driven over Enum.GetValues<SidebarEntryKind>() so a
    // future member added without a matching wire arm fails HERE as a broken round trip, rather than silently
    // degrading to a route pin the way the old catch-all default did.

    static readonly SidebarEntryKind[] AllEntryKinds = Enum.GetValues<SidebarEntryKind>();

    [Fact]
    public void Every_entry_kind_has_a_non_empty_wire_string()
    {
        foreach (var kind in AllEntryKinds)
            Assert.False(string.IsNullOrEmpty(SidebarLayoutWire.PinKindName(kind)));
    }

    [Fact]
    public void Every_entry_kind_round_trips_through_its_wire_string()
    {
        // A member left off PinKindName's switch falls through to its "appRoute" default, which round-trips to
        // AppRoute instead of the member itself — exactly the failure this test exists to catch.
        foreach (var kind in AllEntryKinds)
        {
            string name = SidebarLayoutWire.PinKindName(kind);
            Assert.True(SidebarLayoutWire.TryParsePinKind(name, out var parsed),
                $"'{name}' (written for {kind}) does not parse back at all.");
            Assert.Equal(kind, parsed);
        }
    }

    [Fact]
    public void Every_wire_string_round_trips_through_the_enum()
    {
        foreach (var kind in AllEntryKinds)
        {
            string name = SidebarLayoutWire.PinKindName(kind);
            Assert.True(SidebarLayoutWire.TryParsePinKind(name, out var parsed));
            Assert.Equal(name, SidebarLayoutWire.PinKindName(parsed));
        }
    }

    [Fact]
    public void An_unrecognized_pin_kind_string_fails_explicitly_and_never_guesses()
    {
        Assert.False(SidebarLayoutWire.TryParsePinKind("madeUpKind", out _));
        Assert.False(SidebarLayoutWire.TryParsePinKind("", out _));
        Assert.False(SidebarLayoutWire.TryParsePinKind(null, out _));
    }

    // The frozen legacy table: the pre-unification SidebarPinKind byte numbering (Route=0, Playlist=1, Album=2,
    // Artist=3, Show=4, Folder=5). Never edit these values — decoding an int a build predating the unification wrote
    // is their only job.

    [Theory]
    [InlineData(0, SidebarEntryKind.AppRoute)]  // the old SidebarPinKind.Route
    [InlineData(1, SidebarEntryKind.Playlist)]
    [InlineData(2, SidebarEntryKind.Album)]
    [InlineData(3, SidebarEntryKind.Artist)]
    [InlineData(4, SidebarEntryKind.Show)]
    [InlineData(5, SidebarEntryKind.Folder)]
    public void The_frozen_legacy_int_maps_to_the_kind_it_historically_meant(int legacy, SidebarEntryKind expected)
    {
        Assert.True(SidebarLayoutWire.TryLegacyPinKind(legacy, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]     // one past the old enum's last member (Folder = 5) — SidebarEntryKind.Track has NO legacy slot
    [InlineData(255)]
    public void A_legacy_int_outside_the_frozen_range_fails_explicitly(int legacy)
        => Assert.False(SidebarLayoutWire.TryLegacyPinKind(legacy, out _));

    [Fact]
    public void The_legacy_int_write_and_read_round_trip_for_every_pinnable_kind()
    {
        // LegacyPinKindInt is the inverse of TryLegacyPinKind, written for a downgrade. Track is excluded here (not
        // via SidebarPinId.IsPinnable, which belongs to the projection/binder layer, out of scope for this file):
        // SidebarLayoutWire.LegacyPinKindInt(Track) deliberately falls through to the old Route slot (0) because
        // some int must be written, never because a track pin can reach this arm — see its own doc comment.
        foreach (var kind in AllEntryKinds)
        {
            if (kind == SidebarEntryKind.Track) continue;
            int legacy = SidebarLayoutWire.LegacyPinKindInt(kind);
            Assert.True(SidebarLayoutWire.TryLegacyPinKind(legacy, out var back));
            Assert.Equal(kind, back);
        }
    }

    // ── region: the version ladder (from 0.2.9 SidebarLayoutV2MigrationTests.cs, document-only facts) ────────────────
    //
    // File mechanics (atomic write, .bak, corruption, the v3+ "too new" gate) belong to SidebarLayoutStoreTests —
    // not ported here; see the file header. Everything below runs purely in memory over SidebarLayoutMigrations and
    // SidebarLayoutWire, exactly as the migration ladder's own contract requires (total, in-memory, preserving, one
    // version at a time).

    const string V1Document = """
    {
      "version": 1,
      "updatedAtMs": 1753893041233,
      "appVersion": "1.0.0",
      "pins": [
        { "id": "liked", "kind": 0, "uri": "spotify:collection:tracks", "name": "Liked Songs", "addedAtMs": 1753013400000 },
        { "id": "folder:6a1f2c", "kind": 5, "uri": "", "name": "Cafe & chill", "addedAtMs": 1753301100000 }
      ],
      "v3": {
        "customOrder": ["pl:a", "pl:b"],
        "expandedFolders": ["6a1f2c"],
        "firstSeen": [ { "id": "pl:b", "ms": 1753578000000 } ]
      },
      "curated": {
        "templateId": "curated",
        "sections": [
          { "id": "sec_pin", "kind": "pinned", "items": [ { "id": "itm_1", "target": "entity", "key": "spotify:playlist:1", "label": "Alias" } ] },
          { "id": "sec_jump", "kind": "jumpBackIn", "display": { "maxItems": 4 } },
          { "id": "sec_list", "kind": "entityList", "query": { "kinds": ["artists"], "sort": "alphabetical", "descending": false } },
          { "id": "sec_grp", "kind": "customGroup", "gravity": "down",
            "children": [ { "id": "sec_kid", "kind": "staticLinks", "items": [ { "id": "itm_2", "target": "route", "key": "home" } ] } ] },
          { "id": "sec_div", "kind": "divider" }
        ]
      },
      "telemetryOptIn": true
    }
    """;

    // The exact first generated Curated default. Only ids and resolver-owned fallback caches are intentionally unstable.
    const string LegacyCuratedDefault = """
    {
      "version": 2,
      "curated": {
        "templateId": "curated",
        "sections": [
          { "id": "sec_pin", "kind": "pinned", "titleLocKey": "sidebar.pinned",
            "display": { "density": "cozy" } },
          { "id": "sec_div1", "kind": "divider" },
          { "id": "sec_played", "kind": "jumpBackIn", "titleLocKey": "sidebar.section.recentlyPlayed",
            "display": { "subtitles": false, "showInRail": false, "maxItems": 4, "recents": "played" } },
          { "id": "sec_div2", "kind": "divider" },
          { "id": "sec_shortcuts", "kind": "collectionShortcuts", "titleLocKey": "sidebar.yourLibrary",
            "display": { "artwork": false, "subtitles": false, "countBadges": true },
            "items": [
              { "id": "itm_liked", "target": "route", "key": "liked", "icon": "Heart", "fallbackTitle": "Liked Songs" },
              { "id": "itm_albums", "target": "route", "key": "albums", "icon": "Album" },
              { "id": "itm_artists", "target": "route", "key": "artists", "icon": "Contact" },
              { "id": "itm_podcasts", "target": "route", "key": "podcasts", "icon": "RadioTower" },
              { "id": "itm_local", "target": "route", "key": "local", "icon": "Folder" }
            ] },
          { "id": "sec_div3", "kind": "divider" },
          { "id": "sec_tree", "kind": "playlistTree", "titleLocKey": "sidebar.playlists",
            "display": { "density": "cozy" } }
        ]
      }
    }
    """;

    [Fact]
    public void A_v1_document_upgrades_in_memory_with_every_payload_intact()
    {
        var doc = Parse(V1Document);
        int sectionsBefore = doc.Curated!.Sections!.Length;

        var upgraded = SidebarLayoutMigrations.Upgrade(doc);

        Assert.Same(doc, upgraded);                                    // mutated in place ⇒ the Extra carry lives
        Assert.Equal(SidebarLayoutStore.CurrentVersion, upgraded.Version);
        Assert.Equal(sectionsBefore, upgraded.Curated!.Sections!.Length);
        Assert.NotNull(upgraded.Extra);

        // envelope + pins + the V3 overlay survive the version bump untouched
        Assert.Equal(1753893041233, upgraded.UpdatedAtMs);
        Assert.Equal("1.0.0", upgraded.AppVersion);
        Assert.True(upgraded.Extra!.ContainsKey("telemetryOptIn"));
        Assert.Equal(2, upgraded.Pins!.Length);
        Assert.Equal("liked", upgraded.Pins[0].Id);
        Assert.Equal(5, upgraded.Pins[1].Kind);
        Assert.Equal(new[] { "pl:a", "pl:b" }, upgraded.V3!.CustomOrder);

        // the curated layout: same five sections, same order, same nesting, and nothing v2 fabricated
        var read = SidebarLayoutWire.ReadCurated(upgraded.Curated);
        var layout = read.Layout;
        Assert.Equal("curated", layout.TemplateId);
        var ids = new string[layout.Sections.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = layout.Sections[i].Id;
        Assert.Equal(new[] { "sec_pin", "sec_jump", "sec_list", "sec_grp", "sec_div" }, ids);
        Assert.Equal(0, read.Carry.UnknownSectionCount);           // every kind in a v1 document is known to v2
        Assert.Equal("sec_kid", layout.Sections[3].ChildList[0].Id);
        Assert.Equal("Alias", layout.Sections[0].ItemList[0].LabelOverride);
        Assert.Equal(4, layout.Sections[1].Opts.MaxItems);
        Assert.Equal(SidebarEntityKinds.Artists, layout.Sections[2].Query!.Kinds);

        for (int i = 0; i < layout.Sections.Count; i++)
        {
            Assert.Null(layout.Sections[i].Extension);
            Assert.False(layout.Sections[i].IsExtension);
            var items = layout.Sections[i].ItemList;
            for (int j = 0; j < items.Count; j++) Assert.Null(items[j].Action);
        }
        Assert.Null(layout.Sections[2].Query!.IncludeUris);
        Assert.Null(layout.Sections[2].Query!.ExcludeUris);
    }

    [Fact]
    public void Upgrade_from_v1_mutates_in_place_and_the_second_pass_is_a_no_op()
    {
        var doc = Parse(V1Document);
        int sections = doc.Curated!.Sections!.Length;

        var once = SidebarLayoutMigrations.Upgrade(doc);
        Assert.Same(doc, once);                                    // mutated in place ⇒ [JsonExtensionData] survives
        Assert.Equal(2, once.Version);
        Assert.Equal(sections, once.Curated!.Sections!.Length);
        Assert.NotNull(once.Extra);

        string a = Json(once);
        string b = Json(SidebarLayoutMigrations.Upgrade(once));
        Assert.Equal(a, b);                                        // a second pass is a no-op
    }

    [Fact]
    public void Upgrade_is_total_never_throws_never_returns_null_never_overshoots()
    {
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(null!).Version);
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(new SidebarLayoutDocDto { Version = 0 }).Version);
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(new SidebarLayoutDocDto { Version = 2 }).Version);
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(new SidebarLayoutDocDto { Version = 7 }).Version);   // clamped, not carried past current
    }

    [Fact]
    public void The_exact_legacy_curated_default_preserves_its_section_dividers()
    {
        var doc = Parse(LegacyCuratedDefault);

        SidebarLayoutMigrations.Upgrade(doc);
        var layout = SidebarLayoutWire.ReadCurated(doc.Curated).Layout;

        Assert.Equal(7, layout.Sections.Count);
        Assert.Equal(SidebarSectionKind.Pinned, layout.Sections[0].Kind);
        Assert.Equal(SidebarSectionKind.Divider, layout.Sections[1].Kind);
        Assert.Equal(SidebarSectionKind.JumpBackIn, layout.Sections[2].Kind);
        Assert.Equal(SidebarSectionKind.Divider, layout.Sections[3].Kind);
        Assert.Equal(SidebarSectionKind.CollectionShortcuts, layout.Sections[4].Kind);
        Assert.Equal(SidebarSectionKind.Divider, layout.Sections[5].Kind);
        Assert.Equal(SidebarSectionKind.PlaylistTree, layout.Sections[6].Kind);
        Assert.Equal(SidebarPresentation.List, layout.Sections[2].Opts.Presentation);
        Assert.Equal(SidebarDensity.Cozy, layout.Sections[4].Opts.Density);
    }

    [Fact]
    public void An_authored_option_divergence_still_preserves_every_divider()
    {
        var doc = Parse(LegacyCuratedDefault);
        doc.Curated!.Sections![4].Display!.Density = "compact";

        SidebarLayoutMigrations.Upgrade(doc);
        var layout = SidebarLayoutWire.ReadCurated(doc.Curated).Layout;

        Assert.Equal(7, layout.Sections.Count);
        int dividers = 0;
        for (int i = 0; i < layout.Sections.Count; i++)
            if (layout.Sections[i].Kind == SidebarSectionKind.Divider) dividers++;
        Assert.Equal(3, dividers);
        Assert.Equal(SidebarDensity.Compact, layout.Sections[4].Opts.Density);
    }

    [Fact]
    public void An_authored_divider_title_is_not_rewritten_by_the_migration()
    {
        var doc = Parse(LegacyCuratedDefault);
        doc.Curated!.Sections![1].Title = "My separator";

        SidebarLayoutMigrations.Upgrade(doc);
        var layout = SidebarLayoutWire.ReadCurated(doc.Curated).Layout;

        Assert.Equal(7, layout.Sections.Count);
        Assert.Equal("My separator", layout.Sections[1].Title);
    }

    [Fact]
    public void Migration_from_a_newer_build_snapshot_is_idempotent()
    {
        var once = SidebarLayoutMigrations.Upgrade(Parse(NewerBuildJson));
        string a = Json(once);
        string b = Json(SidebarLayoutMigrations.Upgrade(once));
        Assert.Equal(a, b);
    }

    // ── region: the built-in documents — fresh-install AND corrupt-fallback (from SidebarLayoutJsonTests.cs's
    // defaults facts; SidebarBootstrapTests.cs itself contributed nothing here, see DROPPED in the porting report) ────
    //
    // SidebarLayoutDefaults.CuratedLayout() is documented as BOTH the fresh-install document and the corrupt-file
    // fallback (Sidebar.Doc.cs's own header comment on SidebarLayoutDefaults): "on any non-None load fault the
    // service loads CuratedLayout() in memory". The load-fault wiring itself is Sidebar.Host.cs's job (not yet
    // implemented in this tree); what belongs here is that the DOCUMENT that decision falls back to is exactly the
    // Wavee Curated template, wrapped in a current-version envelope.

    [Fact]
    public void The_curated_default_document_is_a_current_version_envelope_over_the_curated_template()
    {
        var doc = SidebarLayoutDefaults.CuratedDocument();
        Assert.Equal(SidebarLayoutStore.CurrentVersion, doc.Version);
        Assert.NotNull(doc.Curated);
        Assert.NotEmpty(doc.Curated!.Sections!);

        // Round-trips through the wire unchanged, and matches the template it delegates to structurally.
        var back = SidebarLayoutWire.ReadCurated(Parse(Json(doc)).Curated).Layout;
        var template = SidebarTemplates.Build(SidebarTemplates.Curated);
        Assert.Equal(template.Sections.Count, back.Sections.Count);
        for (int i = 0; i < template.Sections.Count; i++)
        {
            Assert.Equal(template.Sections[i].Kind, back.Sections[i].Kind);
            Assert.Equal(template.Sections[i].Opts, back.Sections[i].Opts);
        }
    }

    [Fact]
    public void The_empty_default_document_carries_only_the_version()
    {
        var doc = SidebarLayoutDefaults.EmptyDocument();
        Assert.Equal(SidebarLayoutStore.CurrentVersion, doc.Version);
        Assert.Null(doc.Curated);
        Assert.Null(doc.Pins);
        Assert.Null(doc.V3);
    }

    [Fact]
    public void An_unknown_template_id_falls_back_to_the_curated_layout_the_same_way_a_corrupt_file_does()
    {
        var fallback = SidebarLayoutDefaults.CuratedLayout();
        var unknownFallsBack = SidebarLayoutDefaults.LayoutOf("this-template-does-not-exist");
        var absentFallsBack = SidebarLayoutDefaults.LayoutOf(null);

        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(fallback, unknownFallsBack),
            SidebarLayoutCompare.FirstDifference(fallback, unknownFallsBack, ignoreIds: true));
        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(fallback, absentFallsBack),
            SidebarLayoutCompare.FirstDifference(fallback, absentFallsBack, ignoreIds: true));
        Assert.Equal(SidebarTemplates.Curated, fallback.TemplateId);
    }
}
