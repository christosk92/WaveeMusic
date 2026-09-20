using System;
using System.IO;
using System.Text.Json;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// LAYOUT V2's version ladder, end to end through the real store (M1 delta spec item 1).
//
// The whole promise of v2 is that it costs an existing user NOTHING: v1 → v2 is an IDENTITY migration, so a document
// written by the shipped build loads with the same sections, the same pins, the same V3 overlay and the same unknown-member
// carry, keeps rendering identically, and simply stamps "version": 2 the next time anything is saved. The tests below pin
// that promise from both directions — a v1 file read by this build, and a v2 file read by a build that predates v2 (the
// opaque-carry path) — plus the version GATE above it: v3 is TooNew, is never touched, and blocks writes.
//
// File mechanics (atomic write, .bak, corruption) belong to SidebarLayoutStoreTests; wire shapes to SidebarLayoutJsonTests.
public sealed class SidebarLayoutV2MigrationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-sidebar-v2-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public SidebarLayoutV2MigrationTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "sidebar-layout.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    SidebarLayoutStore Store() => new(_path);

    static string Json(SidebarLayoutDocDto doc) =>
        JsonSerializer.Serialize(doc, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto);

    static SidebarLayoutDocDto Parse(string json) =>
        JsonSerializer.Deserialize(json, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto)!;

    /// <summary>A realistic document as the SHIPPED v1 build wrote it: pins, the V3 overlay, a curated layout using only
    /// v1 kinds, an unknown member from an even newer build, and no v2 member anywhere.</summary>
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

    // ── v1 → v2 is IDENTITY ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void V1Document_Loads_WithoutAFault_AndStampsVersionTwoInMemory()
    {
        File.WriteAllText(_path, V1Document);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Equal(SidebarLoadFault.None, load.Fault);
        Assert.NotNull(load.Doc);
        Assert.Equal(2, load.Doc!.Version);                 // upgraded IN MEMORY…
        Assert.Equal(before, File.ReadAllBytes(_path));      // …and the file is untouched until an ordinary commit
        Assert.False(store.WritesBlocked);
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
    }

    [Fact]
    public void V1Document_LoadsWithEveryPayloadIntact_AndNoV2FieldInvented()
    {
        File.WriteAllText(_path, V1Document);
        var doc = Store().Load().Doc!;

        // envelope
        Assert.Equal(1753893041233, doc.UpdatedAtMs);
        Assert.Equal("1.0.0", doc.AppVersion);
        Assert.True(doc.Extra!.ContainsKey("telemetryOptIn"));

        // pins + the V3 overlay survive the version bump untouched
        Assert.Equal(2, doc.Pins!.Length);
        Assert.Equal("liked", doc.Pins[0].Id);
        Assert.Equal(5, doc.Pins[1].Kind);
        Assert.Equal(new[] { "pl:a", "pl:b" }, doc.V3!.CustomOrder);
        Assert.Equal(new[] { "6a1f2c" }, doc.V3.ExpandedFolders);
        Assert.Equal("pl:b", doc.V3.FirstSeen![0].Id);

        // the curated layout: same five sections, same order, same nesting
        var read = SidebarLayoutWire.ReadCurated(doc.Curated);
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

        // …and NOTHING v2 is fabricated: no extension ref, no action binding, no uri set.
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
    public void V1Document_ReSavesAsV2_WithoutLosingAnything()
    {
        File.WriteAllText(_path, V1Document);

        var store = Store();
        var doc = store.Load().Doc!;
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000));

        string saved = File.ReadAllText(_path);
        Assert.Contains("\"version\": 2", saved);              // the ONLY visible difference
        Assert.DoesNotContain("\"version\": 1", saved);
        Assert.Contains("telemetryOptIn", saved);              // the envelope carry
        // pins — the default JSON encoder unicode-escapes '&', so probe the name's words rather than the raw glyph
        Assert.True(saved.Contains("Cafe") && saved.Contains("chill"),
            "the pin name did not survive the v1->v2 re-save");
        Assert.Contains("6a1f2c", saved);                      // the V3 overlay
        Assert.Contains("sec_kid", saved);                     // nesting
        Assert.Contains("gravity", saved);                     // an unknown SECTION member, via the wire carry
        Assert.DoesNotContain("\"extension\"", saved);         // …and no v2 member is invented on the way out
        Assert.DoesNotContain("\"action\"", saved);
        Assert.DoesNotContain("includeUris", saved);

        // Reading the re-saved file yields the SAME layout, structurally — "no visual change to existing layouts".
        var reloaded = Store().Load();
        Assert.Equal(SidebarLoadFault.None, reloaded.Fault);
        Assert.Equal(2, reloaded.Doc!.Version);
        Assert.True(SidebarLayoutCompare.Equal(
            SidebarLayoutWire.ReadCurated(Parse(V1Document).Curated).Layout,
            SidebarLayoutWire.ReadCurated(reloaded.Doc.Curated).Layout),
            SidebarLayoutCompare.FirstDifference(
                SidebarLayoutWire.ReadCurated(Parse(V1Document).Curated).Layout,
                SidebarLayoutWire.ReadCurated(reloaded.Doc.Curated).Layout));
    }

    [Fact]
    public void Upgrade_FromV1_IsInPlace_Idempotent_AndKeepsTheCarry()
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
    public void Upgrade_IsTotal()
    {
        // Never throws, never returns null, never leaves a version above the current one.
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(null!).Version);
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(new SidebarLayoutDocDto { Version = 0 }).Version);
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(new SidebarLayoutDocDto { Version = 2 }).Version);
        Assert.Equal(2, SidebarLayoutMigrations.Upgrade(new SidebarLayoutDocDto { Version = 7 }).Version);
    }

    [Fact]
    public void ExactLegacyCuratedDefault_PreservesItsSectionDividers()
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
    public void LegacyDefault_WithAnAuthoredOptionDivergence_PreservesEveryDivider()
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
    public void LegacyDefault_WithAnAuthoredDividerTitle_IsNotRewritten()
    {
        var doc = Parse(LegacyCuratedDefault);
        doc.Curated!.Sections![1].Title = "My separator";

        SidebarLayoutMigrations.Upgrade(doc);
        var layout = SidebarLayoutWire.ReadCurated(doc.Curated).Layout;

        Assert.Equal(7, layout.Sections.Count);
        Assert.Equal("My separator", layout.Sections[1].Title);
    }

    // ── the version gate above v2 ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    public void VersionAboveTwo_IsTooNew_KeepsTheFile_AndBlocksWrites(int version)
    {
        string payload = $$"""
        { "version": {{version}}, "curated": { "templateId": "curated", "sections": [
          { "id": "sec_future", "kind": "extension",
            "extension": { "extensionId": "acme", "contributionId": "thing", "config": { "k": 1 } } } ] } }
        """;
        File.WriteAllText(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Equal(SidebarLoadFault.TooNew, load.Fault);
        Assert.Null(load.Doc);
        Assert.Contains(version.ToString(), load.Detail);
        Assert.True(store.WritesBlocked);

        store.Commit(new SidebarLayoutDocDto { Version = 2 });
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));             // a newer build owns the file
    }

    // ── a v2 document opened by a build that predates v2 (the other direction) ─────────────────────────────────────────

    [Fact]
    public void V2Payload_ReadAndReSaved_KeepsEveryV2Member()
    {
        // The v2 members this build DOES understand survive a full store round trip; the opaque config inside them is
        // never inspected, so an unknown extension's settings come back byte-for-byte in meaning.
        const string v2 = """
        {
          "version": 2,
          "curated": {
            "templateId": "curated",
            "sections": [
              { "id": "sec_x", "kind": "extension",
                "extension": { "extensionId": "acme.stats", "contributionId": "listening.heatmap", "schemaVersion": 4,
                               "config": { "buckets": [1, 2, 3], "palette": { "warm": "#f00" } } } },
              { "id": "sec_g", "kind": "customGroup", "items": [
                { "id": "itm_a", "target": "action", "key": "wavee.play",
                  "action": { "providerId": "wavee", "actionId": "play", "targetMode": "fixedEntity",
                              "targetKey": "spotify:playlist:1", "arguments": { "shuffle": true } } } ] },
              { "id": "sec_e", "kind": "entityList",
                "query": { "kinds": ["artists"], "includeUris": ["spotify:artist:a"], "excludeUris": ["spotify:artist:b"] } }
            ]
          }
        }
        """;
        File.WriteAllText(_path, v2);

        var store = Store();
        var doc = store.Load().Doc!;
        Assert.Equal(2, doc.Version);

        var read = SidebarLayoutWire.ReadCurated(doc.Curated);
        var x = read.Layout.Sections[0].Extension!;
        Assert.Equal("acme.stats", x.ExtensionId);
        Assert.Equal(4, x.SchemaVersion);
        var binding = read.Layout.Sections[1].ItemList[0].Action!;
        Assert.Equal(SidebarActionTargetMode.FixedEntity, binding.TargetMode);
        Assert.Equal("spotify:playlist:1", binding.TargetKey);
        Assert.Equal(new[] { "spotify:artist:a" }, read.Layout.Sections[2].Query!.IncludeList);

        doc.Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry);
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000));

        string saved = File.ReadAllText(_path);
        Assert.Contains("\"kind\": \"extension\"", saved);
        Assert.Contains("listening.heatmap", saved);
        Assert.Contains("\"warm\": \"#f00\"", saved);
        Assert.Contains("\"targetMode\": \"fixedEntity\"", saved);
        Assert.Contains("\"shuffle\": true", saved);
        Assert.Contains("\"includeUris\"", saved);
        Assert.Contains("\"excludeUris\"", saved);

        // …and the second read equals the first, so a save/load cycle is a fixed point.
        var again = SidebarLayoutWire.ReadCurated(Store().Load().Doc!.Curated).Layout;
        Assert.True(SidebarLayoutCompare.Equal(read.Layout, again),
            SidebarLayoutCompare.FirstDifference(read.Layout, again));
    }

    [Fact]
    public void V2Document_WithAFutureKind_StillRoundTripsTheOpaqueSection()
    {
        // The unknown-KIND policy is unchanged by v2 (and now also covers a kind a v3 build introduces).
        const string v2WithFuture = """
        { "version": 2, "curated": { "templateId": "curated", "sections": [
          { "id": "sec_known", "kind": "extension",
            "extension": { "extensionId": "wavee", "contributionId": "queue" } },
          { "id": "sec_future", "kind": "hologram", "config": { "spin": 3 } } ] } }
        """;
        File.WriteAllText(_path, v2WithFuture);

        var store = Store();
        var doc = store.Load().Doc!;
        var read = SidebarLayoutWire.ReadCurated(doc.Curated);
        Assert.Single(read.Layout.Sections);
        Assert.Equal(1, read.Carry.UnknownSectionCount);

        doc.Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry);
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000));

        string saved = File.ReadAllText(_path);
        Assert.Contains("\"kind\": \"hologram\"", saved);
        Assert.Contains("\"spin\"", saved);
        Assert.Contains("\"contributionId\": \"queue\"", saved);
    }
}
