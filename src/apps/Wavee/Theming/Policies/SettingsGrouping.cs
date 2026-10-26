// ── Theming/Policies/SettingsGrouping.cs — how a structure groups the Settings page's twelve areas into tabs (P6 WK, owner Q3) ─────────────
//
// The page's AREAS are core (`Settings.Area`, `Screens\Settings.Areas.cs`): each a list of document sections (`Settings.Part`), each with its slug
// that a deep link, the palette and a toast's "Manage" land on. A structure that draws FEWER tabs than areas (WinUI: General · Appearance ·
// Playback · Notifications · Storage & cache · Privacy & diagnostics · About) declares the grouping here; the core host resolves every slug
// through it and hands the view the tab the open area lives in (`SettingsPageProps.Group` / `.Grouping` / `.OpenGroup`).
//
// THE RULES.
//   · Every area has ONE home tab (the tab that lists it in `Areas`), and every tab is the home of at least one area: the area cell stays the
//     page's one state, and the tab on show is always `GroupOf(area)`.
//   · A tab draws its areas' sections in order, unless it lists its sections itself (`Parts`, "rows moved, not invented"): a section listed by
//     a tab is drawn there and nowhere else. Every document section is drawn by exactly one tab (`Validate`).
//   · Slugs: an area's own slug lands on that area; a tab's own slug (one that is not an area slug) lands on the tab's first home area; every
//     older alias (`Settings.AreaFromSlug`) lands where it always did, so its area's home tab shows it. An unknown slug lands where the page
//     always opened a bad arg.
//
// `Default` is the ungrouped page: twelve tabs, one area each, slug = the area's slug. It resolves every slug exactly as `Settings.AreaFromSlug`.
// `Seven` is the boards' seven tabs (W-Settings), which Classic and WinUI both draw (owner 2026-10-25: twelve tabs overran Classic's strip at every width):
//
//   General                 Account, Devices & connect and Keyboard, and the window rows the old General tab held (language, links, the notification-area icon,
//                           graphics), listed by the tab itself ("rows moved, not invented")
//   Appearance              Appearance (theme, its options, display, fullscreen, now playing, home) and Lyrics
//   Playback                Playback and Audio
//   Notifications           Notifications
//   Storage & cache         Storage
//   Privacy & diagnostics   Privacy and Feedback & help (the logs, the tools and the developer rows ride Feedback)
//   About                   About
//
// Every old slug lands: an area's own slug on its area, shown on the tab that holds it (account, devices, keyboard on General; lyrics on Appearance; audio on
// Playback; feedback on Privacy & diagnostics); a tab's own slug (`general`, `storage-cache`, `privacy-diagnostics`) on the tab's first area; every older alias
// where it always did. The old `general` alias used to open Appearance, where the window rows lived; the tab's own slug wins (General now holds them).

using System.Collections.Immutable;

namespace Wavee.Theming;

/// <summary>One tab of a grouped Settings page.</summary>
/// <param name="Slug">The tab's slug (a deep link's arg may name it).</param>
/// <param name="TitleKey">The tab's name (a loc key).</param>
/// <param name="Areas">The areas whose home is this tab, in order (at least one).</param>
public sealed record SettingsTab(string Slug, string TitleKey, ImmutableArray<Settings.Area> Areas)
{
    /// <summary>The sections the tab draws, in order, where it lists them itself (sections moved here from another tab's area); default: its
    /// areas' sections, minus any section another tab lists.</summary>
    public ImmutableArray<Settings.Part> Parts { get; init; }

    /// <summary>The tab lists its sections itself.</summary>
    public bool ListsParts => !Parts.IsDefault;

    /// <summary>Equal when slug, title, areas and listed sections are (an array compares by its items, so a republished equal grouping is not a change).</summary>
    public bool Equals(SettingsTab? other)
        => other is not null && Slug == other.Slug && TitleKey == other.TitleKey && Same(Areas, other.Areas)
           && ListsParts == other.ListsParts && (!ListsParts || Same(Parts, other.Parts));

    public override int GetHashCode() => HashCode.Combine(Slug, TitleKey, Areas.IsDefault ? 0 : Areas.Length, ListsParts ? Parts.Length : -1);

    static bool Same<T>(ImmutableArray<T> a, ImmutableArray<T> b)
        => a.IsDefault ? b.IsDefault : !b.IsDefault && System.Linq.Enumerable.SequenceEqual(a, b);
}

/// <summary>The structure's grouping of the Settings areas into tabs (P6 WK). See the file header for the rules.</summary>
/// <param name="Tabs">The tabs, in pivot order.</param>
public sealed record SettingsGrouping(ImmutableArray<SettingsTab> Tabs)
{
    /// <summary>Today's page: one tab per area, in area order, each with the area's slug and name.</summary>
    public static SettingsGrouping Default { get; } = BuildDefault();

    /// <summary>The boards' seven tabs over the twelve areas (see the file header).</summary>
    public static SettingsGrouping Seven { get; } = BuildSeven();

    static SettingsGrouping BuildSeven()
    {
        static ImmutableArray<Settings.Area> A(params Settings.Area[] areas) => [.. areas];
        var general = new SettingsTab("general", Strings.Settings.Tabs.General, A(Settings.Area.Account, Settings.Area.Devices, Settings.Area.Keyboard))
        {
            Parts =
            [
                new(Settings.Tab.General, "Language & region"), new(Settings.Tab.General, "Links"), new(Settings.Tab.General, "Notification area"),
                new(Settings.Tab.General, "Graphics"), new(Settings.Tab.Account, "Account"), new(Settings.Tab.Devices, "Connect"), new(Settings.Tab.Keyboard, "Shortcuts"),
            ],
        };
        return new SettingsGrouping(
        [
            general,
            new SettingsTab("appearance", Strings.Settings.Areas.Appearance, A(Settings.Area.Appearance, Settings.Area.Lyrics)),
            new SettingsTab("playback", Strings.Settings.Areas.Playback, A(Settings.Area.Playback, Settings.Area.Audio)),
            new SettingsTab("notifications", Strings.Settings.Areas.Notifications, A(Settings.Area.Notifications)),
            new SettingsTab("storage-cache", Strings.Settings.Areas.Storage, A(Settings.Area.Storage)),
            new SettingsTab("privacy-diagnostics", Strings.Settings.Tabs.Privacy, A(Settings.Area.Privacy, Settings.Area.Feedback)),
            new SettingsTab("about", Strings.Settings.Areas.About, A(Settings.Area.About)),
        ]);
    }

    static SettingsGrouping BuildDefault()
    {
        var tabs = ImmutableArray.CreateBuilder<SettingsTab>(Settings.AreaSlugs.Length);
        for (int i = 0; i < Settings.AreaSlugs.Length; i++)
            tabs.Add(new SettingsTab(Settings.AreaSlugs[i], Settings.AreaTitleKeys[i], [(Settings.Area)i]));
        return new SettingsGrouping(tabs.MoveToImmutable());
    }

    /// <summary>How many tabs the pivot draws.</summary>
    public int Count => Tabs.IsDefault ? 0 : Tabs.Length;

    /// <summary>The tab <paramref name="area"/> lives in (its home tab); 0 where no tab lists it (an invalid grouping).</summary>
    public int GroupOf(Settings.Area area)
    {
        for (int t = 0; t < Count; t++)
            foreach (var a in Tabs[t].Areas)
                if (a == area) return t;
        return 0;
    }

    /// <summary>The area tab <paramref name="group"/> opens on: its first home area (clamped to the tabs).</summary>
    public Settings.Area HomeAreaOf(int group) => Count == 0 ? Settings.Area.Appearance : Tabs[Math.Clamp(group, 0, Count - 1)].Areas[0];

    /// <summary>A deep link's name → the area the page opens on (the file header's slug rules). The tab on show is <see cref="GroupOf"/> of it.</summary>
    public Settings.Area AreaFromSlug(string? slug)
    {
        if (slug is { Length: > 0 })
        {
            for (int i = 0; i < Settings.AreaSlugs.Length; i++)
                if (string.Equals(Settings.AreaSlugs[i], slug, StringComparison.OrdinalIgnoreCase)) return (Settings.Area)i;
            for (int t = 0; t < Count; t++)
                if (string.Equals(Tabs[t].Slug, slug, StringComparison.OrdinalIgnoreCase)) return Tabs[t].Areas[0];
        }
        return Settings.AreaFromSlug(slug);
    }

    /// <summary>The tab a deep link's name opens.</summary>
    public int GroupFromSlug(string? slug) => GroupOf(AreaFromSlug(slug));

    /// <summary>The document sections tab <paramref name="group"/> draws, in order.</summary>
    public IReadOnlyList<Settings.Part> PartsOf(int group)
    {
        if (Count == 0) return [];
        var tab = Tabs[Math.Clamp(group, 0, Count - 1)];
        if (tab.ListsParts) return tab.Parts;
        var parts = new List<Settings.Part>();
        foreach (var area in tab.Areas)
            foreach (var part in Settings.PartsOf(area))
                if (!ListedElsewhere(part, group)) parts.Add(part);
        return parts;
    }

    /// <summary>The tab that draws the document section <paramref name="part"/>, or -1 where none does.</summary>
    public int GroupOfPart(Settings.Part part)
    {
        for (int t = 0; t < Count; t++)
            foreach (var p in PartsOf(t))
                if (p == part) return t;
        return -1;
    }

    bool ListedElsewhere(Settings.Part part, int group)
    {
        for (int t = 0; t < Count; t++)
            if (t != group && Tabs[t].ListsParts && Tabs[t].Parts.Contains(part)) return true;
        return false;
    }

    /// <summary>Everything wrong with the grouping, empty when every area has one home tab, every tab a home area, every slug is unique and every
    /// document section is drawn by exactly one tab.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Count == 0) { problems.Add("Settings grouping has no tabs"); return problems; }
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var homes = new int[Settings.AreaSlugs.Length];
        for (int t = 0; t < Count; t++)
        {
            var tab = Tabs[t];
            if (tab is null) { problems.Add($"Settings tab {t} is missing"); continue; }
            if (string.IsNullOrEmpty(tab.Slug)) problems.Add($"Settings tab {t} has no slug");
            else if (!slugs.Add(tab.Slug)) problems.Add($"Settings tab slug '{tab.Slug}' is used twice");
            if (tab.Areas.IsDefaultOrEmpty) problems.Add($"Settings tab '{tab.Slug}' is the home of no area");
            else foreach (var a in tab.Areas)
            {
                if ((uint)a >= (uint)homes.Length) problems.Add($"Settings tab '{tab.Slug}' lists an unknown area {(int)a}");
                else homes[(int)a]++;
            }
        }
        for (int a = 0; a < homes.Length; a++)
            if (homes[a] != 1) problems.Add($"Settings area '{Settings.AreaSlugs[a]}' has {homes[a]} home tabs (exactly one)");
        if (problems.Count > 0) return problems;
        // Every section of the document is drawn exactly once.
        var drawn = new Dictionary<Settings.Part, int>();
        for (int t = 0; t < Count; t++)
            foreach (var p in PartsOf(t))
                drawn[p] = drawn.TryGetValue(p, out int n) ? n + 1 : 1;
        for (int a = 0; a < homes.Length; a++)
            foreach (var p in Settings.PartsOf((Settings.Area)a))
                if (!drawn.TryGetValue(p, out int n) || n != 1) problems.Add($"Settings section {p.Tab}/{p.Section} is drawn {(drawn.TryGetValue(p, out int m) ? m : 0)} times (exactly once)");
        foreach (var (p, _) in drawn)
            if (Settings.AreaOf(p.Tab, p.Section) is null) problems.Add($"Settings section {p.Tab}/{p.Section} is not a document section");
        return problems;
    }

    /// <summary>Equal when the tabs are (item by item).</summary>
    public bool Equals(SettingsGrouping? other)
        => other is not null && (Tabs.IsDefault ? other.Tabs.IsDefault : !other.Tabs.IsDefault && System.Linq.Enumerable.SequenceEqual(Tabs, other.Tabs));

    public override int GetHashCode() => Count;
}
