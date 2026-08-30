using System;
using System.Collections.Generic;
using System.Text.Json;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// The sidebar-layout WIRE contract (F.3.2.1 + the §C1.8.8 wire-string amendment): every section kind and every display
// field survives a model → JSON → model round trip byte-for-byte in meaning, the camelCase kind strings are exactly the
// ones the synthesis notes bind, and — the load-bearing forward-compatibility rule — a section kind or a member THIS
// build does not know round-trips UNTOUCHED instead of being dropped (a version downgrade must never be destructive).
//
// Pure: no disk, no store. SidebarLayoutStoreTests owns the file mechanics.
public class SidebarLayoutJsonTests
{
    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static string Json(SidebarLayoutDocDto doc) =>
        JsonSerializer.Serialize(doc, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto);

    static SidebarLayoutDocDto Parse(string json) =>
        JsonSerializer.Deserialize(json, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto)!;

    static SidebarLayoutDocDto Envelope(SidebarCustomLayout layout, SidebarWireCarry? carry = null) => new()
    {
        Version = SidebarLayoutStore.CurrentVersion,
        Curated = SidebarLayoutWire.WriteCurated(layout, carry),
    };

    /// <summary>Deep structural equality for a layout. Record equality is NOT enough: SidebarSectionSpec's Items/Children
    /// are IReadOnlyList members, which records compare by REFERENCE.</summary>
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

    /// <summary>One section of EVERY kind, every display field pushed off its default, items with every override set, a
    /// CustomGroup with two children, and an EntityList with a non-default query.</summary>
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
                    Item(id + "_d", SidebarItemTarget.Action, "wavee.queue.addNext"),   // v2
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
                        IncludeUris: ["spotify:artist:1", "spotify:artist:2"],           // v2
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

    // ── round trip ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesEverySectionKind()
    {
        var layout = EveryKindLayout();
        var back = SidebarLayoutWire.ReadCurated(Parse(Json(Envelope(layout))).Curated).Layout;
        AssertLayoutEqual(layout, back);
    }

    [Fact]
    public void KindStrings_AreTheBoundCamelCaseVocabulary()
    {
        // The synthesis-notes ruling: Mode C's names, lower-camel on the wire, plus the §C1.8 additions.
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
        Assert.Equal("extension", SidebarLayoutWire.KindName(SidebarSectionKind.Extension));   // v2

        // …and the item/display vocabularies §C1.8.8 adds.
        Assert.Equal("track", SidebarLayoutWire.TargetName(SidebarItemTarget.Track));
        Assert.Equal("track", SidebarLayoutWire.EntityKindName(SidebarEntityKind.Track));
        Assert.Equal("visited", SidebarLayoutWire.RecentsName(SidebarRecentsSource.Visited));
        Assert.Equal("played", SidebarLayoutWire.RecentsName(SidebarRecentsSource.Played));
        Assert.Equal("hideBody", SidebarLayoutWire.EmptyBehaviorName(SidebarEmptyBehavior.HideBody));
        Assert.Equal("compactHint", SidebarLayoutWire.EmptyBehaviorName(SidebarEmptyBehavior.CompactHint));
        Assert.Equal("actionCard", SidebarLayoutWire.EmptyBehaviorName(SidebarEmptyBehavior.ActionCard));

        // v2: the action item target + every target-mode string, exactly as the platform doc binds them.
        Assert.Equal("action", SidebarLayoutWire.TargetName(SidebarItemTarget.Action));
        Assert.Equal("none", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.None));
        Assert.Equal("fixedEntity", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.FixedEntity));
        Assert.Equal("fixedTrack", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.FixedTrack));
        Assert.Equal("nowPlaying", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.NowPlaying));
        Assert.Equal("activeRoute", SidebarLayoutWire.TargetModeName(SidebarActionTargetMode.ActiveRoute));

        // Every kind string is round-trippable — the vocabulary is what the document is keyed on.
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
    public void SerializedDocument_UsesCamelCaseMembersAndStringKinds()
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

        // v2 members, all camelCase, all string-keyed.
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
    public void DefaultDisplayOptions_AreOmittedFromTheWire()
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
    public void AuthoredEmptyBehavior_RoundTripsAsAnOptionalWireString(
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
    public void EmptyDocument_RoundTrips()
    {
        var doc = new SidebarLayoutDocDto { Version = 1 };
        var back = Parse(Json(doc));
        Assert.Equal(1, back.Version);
        Assert.Null(back.Pins);
        Assert.Null(back.V3);
        Assert.Null(back.Curated);
        Assert.Equal(SidebarCustomLayout.Empty.Sections.Count, SidebarLayoutWire.ReadCurated(back.Curated).Layout.Sections.Count);
    }

    [Fact]
    public void PinsAndV3Overlay_RoundTrip()
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

    // ── the shell TOP BAR band (envelope-level, additive, still v2) ───────────────────────────────────────────────────

    static SidebarLayoutDocDto TopBarEnvelope(SidebarCustomLayout layout, SidebarWireCarry? carry = null) => new()
    {
        Version = SidebarLayoutStore.CurrentVersion,
        Curated = SidebarLayoutWire.WriteCurated(layout, carry),
        TopBar = SidebarLayoutWire.WriteTopBar(layout.TopBar, carry),
    };

    [Fact]
    public void TopBar_AbsentMember_ReadsAsNull_AndResolvesToTheBuiltInDefault()
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
    public void TopBar_RoundTripsEveryItemTarget_OnTheEnvelope()
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
    public void TopBar_EmptyBand_SurvivesAsEmpty_NotAsAbsent()
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
    public void TopBar_UnknownMemberOnATile_RidesTheCarry()
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
    public void UnknownEnvelopeAndCuratedMembers_RideTheCarry()
    {
        // The band's own forward-compat premise: an ADDITIVE member a newer build writes at the envelope (or on the
        // curated payload object) must survive a save by an older build. Both levels rebuild their DTO from scratch, so
        // both need an explicit carry — this is the regression test for that.
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
    public void LargeDocument_RoundTrips()
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
        // Smoke bounds, NOT perf gates. The 2 MB / 250 ms figures in the test plan predate F.3.2.1's fixed
        // WriteIndented = true (the document is deliberately user-inspectable), which roughly doubles the payload and
        // costs a first-run JIT pass; a 20 000-item document is also ~100× anything a real user builds.
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
    public void UnknownSectionKind_RoundTripsUntouched()
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
    public void UnknownMembers_OnKnownSectionsSurviveTheModelHop()
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
    public void UnknownDocumentMembers_SurviveTheEnvelope()
    {
        var doc = Parse(NewerBuildJson);
        Assert.NotNull(doc.Extra);
        Assert.True(doc.Extra!.ContainsKey("telemetryOptIn"));
        Assert.Contains("\"telemetryOptIn\"", Json(doc));
    }

    [Fact]
    public void OlderBuildDocument_ReadsWithDefaultsForTheNewFields()
    {
        // A document written before §C1.8 existed: only the original nine kinds, and none of the three new display fields.
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
        Assert.Equal(SidebarRecentsSource.Visited, jump.Opts.Recents);          // the §C1.8.1 default
        Assert.False(jump.Opts.InlineControls);                                  // the §C1.8.6 default
        Assert.True(jump.Opts.PlayButton);                                       // the §C1.8.2 default

        var list = layout.Sections[1];
        Assert.Equal(SidebarEntityKinds.Playlists, list.Query!.Kinds);
        Assert.Equal(SidebarSortMode.Alphabetical, list.Query.Sort);
        Assert.False(list.Query.Descending);
        Assert.Equal(SidebarPlaylistQualifier.Any, list.Query.Qualifier);
    }

    [Fact]
    public void NewerBuildKindsAndFields_ReadExactly()
    {
        // The §C1.8.8 amendment read from the wire: every new kind, plus every new display field and the track target.
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
    public void EntityKindsFlagSet_IsAStableStringArray()
    {
        Assert.Equal(new[] { "playlists", "albums", "artists", "shows" }, SidebarLayoutWire.KindsNames(SidebarEntityKinds.All));
        Assert.Equal(new[] { "albums", "shows" }, SidebarLayoutWire.KindsNames(SidebarEntityKinds.Albums | SidebarEntityKinds.Shows));
        Assert.Equal(SidebarEntityKinds.All, SidebarLayoutWire.ParseKinds(["playlists", "albums", "artists", "shows"]));
        Assert.Equal(SidebarEntityKinds.All, SidebarLayoutWire.ParseKinds(null));            // absent ⇒ All (the model default)
        Assert.Equal(SidebarEntityKinds.Albums, SidebarLayoutWire.ParseKinds(["albums", "hologram"]));   // unknown flag ignored
    }

    [Fact]
    public void UnknownEnumStrings_DegradeToTheModelDefault()
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
        // v2: a target mode from a newer build degrades to None (visible-but-disabled), it never throws.
        Assert.Equal(SidebarActionTargetMode.None, SidebarLayoutWire.ParseTargetMode("telepathy"));
        Assert.Equal(SidebarActionTargetMode.None, SidebarLayoutWire.ParseTargetMode(null));
        Assert.Equal(SidebarActionTargetMode.None, SidebarLayoutWire.ParseTargetMode(""));
    }

    // ── v2: extension refs, action bindings, query uri sets ───────────────────────────────────────────────────────────

    [Fact]
    public void ExtensionSection_RoundTripsRefAndOpaqueConfig()
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
    public void ExtensionConfig_FromAnUnknownExtension_IsPreservedVerbatim()
    {
        // A contribution THIS build has never heard of, with a config shape it cannot possibly understand: the section is
        // a known KIND, so it is not an opaque blob — the config must still survive byte-for-byte in meaning.
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
    public void ExtensionSection_WithNoRef_StaysUnbound_AndStillRoundTrips()
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
    public void ActionBinding_RoundTripsEveryTargetMode(SidebarActionTargetMode mode, string? targetKey)
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
    public void ActionBinding_UnknownTargetMode_DegradesToNone_WithoutThrowing()
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
    public void QueryUriSets_RoundTrip_AndEmptyReadsBackAsNull()
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
    public void V1Document_ReadsWithNullExtensionAndAction()
    {
        // The v2 additions are all OPTIONAL: a v1 document has none of them and must read as "absent", not as a default
        // object (an empty extension ref would fabricate a contribution that does not exist).
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

    // ── migration ladder ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Migration_V1ToCurrent_PreservesSectionsAndPins()
    {
        var doc = Parse(NewerBuildJson);
        int sectionsBefore = doc.Curated!.Sections!.Length;

        var upgraded = SidebarLayoutMigrations.Upgrade(doc);

        Assert.Same(doc, upgraded);                                             // mutated in place ⇒ the Extra carry lives
        Assert.Equal(SidebarLayoutStore.CurrentVersion, upgraded.Version);
        Assert.Equal(sectionsBefore, upgraded.Curated!.Sections!.Length);
        Assert.NotNull(upgraded.Extra);
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        var once = SidebarLayoutMigrations.Upgrade(Parse(NewerBuildJson));
        string a = Json(once);
        string b = Json(SidebarLayoutMigrations.Upgrade(once));
        Assert.Equal(a, b);
    }

    // ── defaults ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CuratedDefaultDocument_IsAV1EnvelopeOverTheCuratedTemplate()
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
    public void EmptyDefaultDocument_CarriesOnlyTheVersion()
    {
        var doc = SidebarLayoutDefaults.EmptyDocument();
        Assert.Equal(SidebarLayoutStore.CurrentVersion, doc.Version);
        Assert.Null(doc.Curated);
        Assert.Null(doc.Pins);
        Assert.Null(doc.V3);
    }
}
