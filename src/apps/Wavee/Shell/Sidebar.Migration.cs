// ── Shell/Sidebar.Migration.cs ─────────────────────────────────────────────────────────────────────────────────────
// the ONE migration from the three designs + sidebar-layout.json v2 to the two layouts + sidebar.json v3 + the account
// files, run once before the pane first mounts, latched by sidebar.bootstrap.version = 2
//
// Role: CORE (SidebarMigrationV2) + SHELL (SidebarMigrationHost)
// Spec: sidebar-rework-implementation.md §P3.10 · design corner case "Migration from today's data", Q1a, Q15
// Delete one release after the rework ships, with Sidebar.Store.V2.cs.

using System.Globalization;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Everything the migration reads, gathered by the host so the rule is pure.</summary>
public readonly record struct SidebarMigrationInput(
    int Design,                       // sidebar.design: 0 Classic · 1 LibraryV3 · 2 Curated
    SidebarLayoutDocDto? V2,          // sidebar-layout.json (null: absent / unreadable)
    bool ClassicPinnedOpen, bool ClassicLibraryOpen, bool ClassicPlaylistsOpen,
    float DesignWidth, bool DesignCollapsed,
    int V3Filter, int V3Sort, bool V3Desc, int V3View,
    bool PinsMigrated,                // the old global sidebar.pins.migratedToServer
    string AccountKey);               // the stored credential's account at migration time ("" ⇒ pending)

public sealed record SidebarMigrationResult(
    SidebarLayoutId Layout,
    SidebarLayoutState State,
    SidebarDensity Density,
    SidebarLibraryFilter Filter,
    float Width,
    bool UserCollapsed,
    SidebarAccountData Account,
    bool ShowToast,
    IReadOnlyList<string> Dropped);   // human-readable names of what did not carry over (Q15)

public static class SidebarMigrationV2
{
    public static SidebarMigrationResult Migrate(in SidebarMigrationInput input)
    {
        // the lambdas below cannot capture the `in` parameter (CS1628), so the fields they read are copied out first
        bool pinnedOpen = input.ClassicPinnedOpen, libraryOpen = input.ClassicLibraryOpen, playlistsOpen = input.ClassicPlaylistsOpen, desc = input.V3Desc;
        var dropped = new List<string>();
        var curated = input.Design == 2 ? input.V2?.Curated?.Sections : null;

        // ── the layout: ALWAYS from sidebar.design (the key's "unwritten" state cannot be probed) ──
        var layout = input.Design switch
        {
            1 => SidebarLayoutId.Library,
            2 => CuratedLooksLikeLibrary(curated) ? SidebarLayoutId.Library : SidebarLayoutId.Classic,
            _ => SidebarLayoutId.Classic,
        };

        // ── Classic: today's three collapse flags; from Curated, its feeds and hidden built-ins ──
        var classic = new List<SectionState>(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Classic).Sections);
        Set(classic, "pinned", s => s with { Collapsed = !pinnedOpen });
        Set(classic, "collections", s => s with { Collapsed = !libraryOpen });
        Set(classic, "playlists", s => s with { Collapsed = !playlistsOpen });
        if (curated is not null) ApplyCurated(curated, classic, dropped);

        // ── Library: the V3 sort / direction / view; density from the compact list ──
        var library = new List<SectionState>(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library).Sections);
        var sort = (uint)input.V3Sort <= 4 ? (SidebarLibrarySort)input.V3Sort : SidebarLibrarySort.Recents;
        var view = input.V3View >= 2 ? SidebarLibraryView.Grid : SidebarLibraryView.List;
        Set(library, "library", s => s with { Sort = sort, Descending = desc, View = view });
        var density = input.Design == 1 && input.V3View == 0 ? SidebarDensity.Compact : SidebarDensity.Default;
        var filter = input.V3Filter switch
        {
            1 => SidebarLibraryFilter.Playlists, 2 => SidebarLibraryFilter.Podcasts,
            3 => SidebarLibraryFilter.Albums, 4 => SidebarLibraryFilter.Artists, _ => SidebarLibraryFilter.None,
        };

        var state = new SidebarLayoutState(
            SidebarLayoutRules.MergeWithCatalogue(new LayoutOverlay(SidebarLayoutId.Classic, classic.ToArray()), null),
            SidebarLayoutRules.MergeWithCatalogue(new LayoutOverlay(SidebarLayoutId.Library, library.ToArray()), null));

        // ── pins: the v2 list (fixed routes dropped, Q1a), then the TopBar shortcuts as local route pins, prepended ──
        var pins = new List<SidebarPin>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool topBarDiffered = false;
        if (input.V2?.TopBar is { } topBar)
        {
            topBarDiffered = !(topBar.Length == 1 && topBar[0] is { Target: "route", Key: "home" });
            var shortcuts = new List<SidebarPin>(topBar.Length);
            for (int i = 0; i < topBar.Length; i++)
            {
                var item = topBar[i];
                if (item is null || item.Hidden == true || string.IsNullOrEmpty(item.Key)) continue;
                string? id = item.Target switch
                {
                    "route" => SidebarPinId.FromRoute(item.Key),
                    "entity" => SidebarPinId.FromUri(item.Key),
                    _ => null,                                   // tracks and actions have no pin form
                };
                if (id is null || SidebarPinRules.IsFixedRoute(id) || !ids.Add(id)) continue;
                var kind = SidebarPinId.KindOf(id);
                string name = item.Label ?? item.FallbackTitle ?? "";
                shortcuts.Add(new SidebarPin(id, kind, SidebarPinId.UriOf(id), name, 0L));
            }
            pins.AddRange(shortcuts);
        }
        if (input.V2?.Pins is { } v2Pins)
            for (int i = 0; i < v2Pins.Length && pins.Count < SidebarAccountStore.MaxPins; i++)
            {
                var d = v2Pins[i];
                if (d?.Id is not { Length: > 0 } id || SidebarPinRules.IsFixedRoute(id) || !ids.Add(id)) continue;
                SidebarEntryKind kind;
                if (!string.IsNullOrEmpty(d.EntityKind)) { if (!SidebarPinWire.TryParseKind(d.EntityKind, out kind)) continue; }
                else if (!SidebarStoreV2.TryLegacyPinKind(d.Kind, out kind)) continue;
                pins.Add(new SidebarPin(id, kind, d.Uri ?? "", d.Name ?? "", d.AddedAtMs));
            }

        var account = new SidebarAccountData(
            input.AccountKey,
            input.PinsMigrated,
            pins,
            input.V2?.V3?.ExpandedFolders is { } folders ? Clean(folders) : [],
            0L,
            input.V2?.V3?.FirstSeen ?? []);

        return new SidebarMigrationResult(layout, state, density, filter, SidebarPaneBounds.Clamp(input.DesignWidth),
            input.DesignCollapsed, account, input.Design == 2 || topBarDiffered, dropped);
    }

    /// <summary>A Curated document reads as Library when it shows a library LIST and no playlist TREE.</summary>
    public static bool CuratedLooksLikeLibrary(SidebarSectionDto[]? sections)
    {
        if (sections is null) return false;
        bool list = false, tree = false;
        void Walk(SidebarSectionDto[] level)
        {
            for (int i = 0; i < level.Length; i++)
            {
                var s = level[i];
                if (s is null || s.Hidden == true) continue;
                if (s.Kind == "entityList") list = true;
                if (s.Kind == "playlistTree") tree = true;
                if (s.Children is { } kids) Walk(kids);
            }
        }
        Walk(sections);
        return list && !tree;
    }

    static void ApplyCurated(SidebarSectionDto[] sections, List<SectionState> classic, List<string> dropped)
    {
        void Walk(SidebarSectionDto[] level)
        {
            for (int i = 0; i < level.Length; i++)
            {
                var s = level[i];
                if (s is null) continue;
                bool visible = s.Hidden != true;
                switch (s.Kind)
                {
                    case "jumpBackIn" when visible:
                        Set(classic, "recent", x => x with { Hidden = false, Limit = NearestLimit(s.Display?.MaxItems, 5) });
                        break;
                    case "newReleases" when visible:
                        Set(classic, "newReleases", x => x with { Hidden = false, Limit = NearestLimit(s.Display?.MaxItems, 10) });
                        break;
                    case "pinned":
                        Set(classic, "pinned", x => x with { Hidden = !visible, Collapsed = s.Collapsed == true });
                        break;
                    case "collectionShortcuts":
                        var hidden = new List<string>();
                        if (s.Items is { } items)
                            for (int k = 0; k < items.Length; k++)
                                if (items[k] is { Hidden: true, Target: "route" } it && it.Key is { } key
                                    && IsCollectionItem(key))
                                    hidden.Add(key);
                        Set(classic, "collections", x => x with
                        {
                            Hidden = !visible, Collapsed = s.Collapsed == true,
                            HiddenItems = hidden.Count > 0 ? hidden.ToArray() : null,
                        });
                        break;
                    case "playlistTree":
                        Set(classic, "playlists", x => x with { Collapsed = s.Collapsed == true });   // it cannot hide
                        break;
                    case "customGroup":
                        if (visible) Note(dropped, "sidebar.migration.kind.group");
                        if (s.Children is { } kids) Walk(kids);
                        break;
                    default:
                        if (visible && DroppedName(s) is { } name) Note(dropped, name);
                        break;
                }
            }
        }
        Walk(sections);
    }

    /// <summary>The loc key naming a section that does not carry over (Q15: human-readable, never a wire kind).</summary>
    static string? DroppedName(SidebarSectionDto s) => s.Kind switch
    {
        "entityEmbed" => "sidebar.migration.kind.spotlight",
        "concerts" => "sidebar.migration.kind.concerts",
        "staticLinks" => "sidebar.migration.kind.links",
        "header" => "sidebar.migration.kind.heading",
        "entityList" => "sidebar.migration.kind.libraryList",
        "extension" => s.Extension?.ContributionId switch
        {
            "queue" => "sidebar.migration.kind.queue",
            "nowPlaying" => "sidebar.migration.kind.nowPlaying",
            "artist.topTracks" => "sidebar.migration.kind.topTracks",
            "concerts" => "sidebar.migration.kind.concerts",
            _ => "sidebar.migration.kind.extension",
        },
        _ => null,   // divider: a separator is structural now, nothing to report
    };

    static void Note(List<string> dropped, string key) { if (!dropped.Contains(key)) dropped.Add(key); }

    static bool IsCollectionItem(string key)
    {
        foreach (var item in SidebarCatalogue.ItemsOf(SidebarLayoutId.Classic, SidebarSectionKind.Collections))
            if (string.Equals(item, key, StringComparison.Ordinal)) return true;
        return false;
    }

    static int NearestLimit(int? maxItems, int fallback)
    {
        if (maxItems is not int m || m <= 0) return fallback;
        return m <= 7 ? 5 : m <= 15 ? 10 : 20;
    }

    static void Set(List<SectionState> list, string id, Func<SectionState, SectionState> change)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.Ordinal)) { list[i] = change(list[i]); return; }
    }

    static string[] Clean(string?[] ids)
    {
        var list = new List<string>(ids.Length);
        for (int i = 0; i < ids.Length; i++) if (!string.IsNullOrEmpty(ids[i]) && !list.Contains(ids[i]!)) list.Add(ids[i]!);
        return list.ToArray();
    }
}

/// <summary>The migration's IO half, run by <see cref="Sidebar.Boot"/> before anything else loads. The legacy keys are READ
/// here and nowhere else, and never written again; <c>sidebar-layout.json</c> is left on disk untouched.</summary>
internal static class SidebarMigrationHost
{
    // The legacy keys exist only here (Platform.Keys no longer declares them).
    static readonly SettingKey<int> Design = new("sidebar.design", 0);
    static readonly SettingKey<bool> ClassicPinned = new("sidebar.classic.section.pinned", true);
    static readonly SettingKey<bool> ClassicLibrary = new("sidebar.classic.section.library", true);
    static readonly SettingKey<bool> ClassicPlaylists = new("sidebar.classic.section.playlists", true);
    static readonly SettingKey<int> V3Filter = new("sidebar.v3.filter", 0);
    static readonly SettingKey<int> V3Sort = new("sidebar.v3.sort", 0);
    static readonly SettingKey<bool> V3Desc = new("sidebar.v3.desc", false);
    static readonly SettingKey<int> V3View = new("sidebar.v3.view", 1);

    public static void RunIfNeeded(IAppSettings settings, string profileDir, in CatalogScope scope)
    {
        if (settings.Get(Platform.Keys.SidebarBootstrapVersion) >= 2) return;
        int design = settings.Get(Design);
        string slug = design switch { 1 => "v3", 2 => "curated", _ => "classic" };
        float defaultWidth = design switch { 1 => 340f, 2 => 320f, _ => 280f };
        var input = new SidebarMigrationInput(
            design,
            SidebarStoreV2.TryRead(profileDir),
            settings.Get(ClassicPinned), settings.Get(ClassicLibrary), settings.Get(ClassicPlaylists),
            settings.Get(new SettingKey<float>("sidebar." + slug + ".width", defaultWidth)),
            settings.Get(new SettingKey<bool>("sidebar." + slug + ".collapsed", false)),
            settings.Get(V3Filter), settings.Get(V3Sort), settings.Get(V3Desc), settings.Get(V3View),
            settings.Get(Platform.Keys.PinsMigratedToServer),
            SidebarAccountKey.Of(scope));
        var r = SidebarMigrationV2.Migrate(in input);

        // Written NOW and waited for (bounded): the pane mounts right after Boot and must read what this produced.
        Directory.CreateDirectory(profileDir);
        var device = new SidebarFileStore(SidebarStoreV3.PathUnder(profileDir));
        device.Commit(SidebarStoreV3.Serialize(r.State));
        device.FlushNow();
        device.WaitForWrites(2000);
        if (r.Account.Pins.Count > 0 || r.Account.ExpandedFolders.Count > 0 || r.Account.FirstSeen.Count > 0)
        {
            var acct = new SidebarFileStore(Path.Combine(profileDir, SidebarAccountStore.FileNameOf(r.Account.Key)));
            acct.Commit(SidebarAccountStore.Serialize(r.Account));
            acct.FlushNow();
            acct.WaitForWrites(2000);
        }

        settings.Set(Platform.Keys.SidebarLayoutId, (int)r.Layout);
        settings.Set(Platform.Keys.SidebarPaneWidth, r.Width);
        settings.Set(Platform.Keys.SidebarPaneUserCollapsed, r.UserCollapsed);
        settings.Set(Platform.Keys.SidebarPaneDensity, (int)r.Density);
        settings.Set(Platform.Keys.SidebarLibraryFilter, (int)r.Filter);
        settings.Set(Platform.Keys.SidebarMigrationDropped, string.Join(",", r.Dropped));
        settings.Set(Platform.Keys.SidebarBootstrapVersion, 2);
        if (r.ShowToast) Sidebar.PendingMigrationToast = r;
        Log.Info("sidebar", "migration.v2 design=" + design.ToString(CultureInfo.InvariantCulture)
            + " layout=" + r.Layout + " pins=" + r.Account.Pins.Count.ToString(CultureInfo.InvariantCulture)
            + " account=" + (r.Account.Key.Length == 0 ? "pending" : SidebarAccountKey.Hash(r.Account.Key))
            + " dropped=" + r.Dropped.Count.ToString(CultureInfo.InvariantCulture));
    }
}
