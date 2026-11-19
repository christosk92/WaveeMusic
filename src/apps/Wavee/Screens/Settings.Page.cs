// ── Screens/Settings.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Settings page's CORE HOST: the install call, the route's mount point, the pivot's tab cell and the deep-link seams, the
// page epoch every writer bumps, and the one component (`PageHost`) that keeps the hooks and publishes the page's context
//
// Role: CORE (themes plan 6.1, T11b). The page's PICTURE — the masthead, the seven-tab pivot, the scroll lane, the section
// headers, the row controls, the cards — is the structure's `Pages.Settings` view (Classic's `ClassicSettings*.cs`), drawn
// over `SettingsDoc`. What stays here is what decides and publishes:
//   · `InstallScreens()` / `Page()` / `Open(link)`: the route, the deep link and the pivot's root-lifetime cell (the cell holds an AREA: the page's twelve
//     sections, `Settings.Areas.cs`). A link (design 4.9) names a category, a dial or any old slug; a dial link also records the
//     `SettingsFocus` the registry's pages play the arrival from (phase 4), and lands, on these old pages, on the area that draws it;
//   · the page EPOCH (`Bump()`): the settings store is not observable, so a writer that returns "re-read" bumps it, the host
//     folds it (and the two foreign epochs the Appearance tab reads) into the props' `Revision`, and the template re-runs;
//   · the page CONTEXT (`SettingsContext.Bind`): the overlay host, the input hooks and the UI poster the verbs read;
//   · the Storage census trigger (the first visit to the tab starts it) and the About notices' warm read.
//
// MOUNT POINTS / SEAMS: `Settings.InstallScreens()` (the orchestrator's one call), `Settings.Page()`, `Settings.Open(string)` (every link);
// `Settings.SendTestEvent` (owner S assigns the notification simulator, G-218).

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. INSTALL, THE PAGE, THE SEAMS ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The Screens-side install: registers the Settings page, then the What's-new page, the report dialog and
    /// the setup wizard (each owner file's own install). Called ONCE by the composition root after
    /// <see cref="Shell.InstallUi"/>.</summary>
    public static void InstallScreens()
    {
        Shell.SetPage(Shell.RouteKind.Settings, static (in Shell.Route route) => PageFor(route));
        // `wavee://open?route=settings&arg=<link>`: a category, `category/dialId`, a dial, or any slug or row id a link ever used (design 4.9),
        // never a place of its own (plan D4). An old slug resolves through the structure's grouping exactly as it always did (P6 WK).
        Shell.OnSettingsTabRequested = static arg => Open(arg);
        ReleaseNotes.InstallUi();
        Feedback.InstallUi();
        Setup.InstallWizard();
    }

    // MOUNT POINT (stage B contract)
    public static Element Page() => Embed.Comp(new HostProps(""), static () => new PageHost());

    /// <summary>The page for a route: the route's arg, an area slug, is the OPEN SECTION (the Compact sections-then-section page; every other tier keeps the pivot).
    /// The slot is the same for every arg (<c>KeyedByArg</c> is false), so opening or closing a section re-pushes the props to the mounted page.</summary>
    public static Element PageFor(in Shell.Route route) => Embed.Comp(new HostProps(Entities.Strings.Resolve(route.Arg)), static () => new PageHost());

    sealed record HostProps(string Arg);

    /// <summary>The route of an area's page (arg = the area's slug).</summary>
    static Shell.Route SectionRoute(int area) => new(Shell.RouteKind.Settings, default, Entities.Strings.Intern(AreaSlugs[Math.Clamp(area, 0, AreaSlugs.Length - 1)].AsSpan()));

    /// <summary>Open Settings from a link (a deep link's arg, the palette's Settings group, a toast, the AI sparkle, the inspector's
    /// `settings focus=`): <c>category</c>, <c>category/dialId</c>, <c>dialId</c>, or any slug or row id a link ever used
    /// (<see cref="SettingsSpec.ParseLink"/>). A dial link records a new <see cref="Focus"/> (its sequence grows on every request, so the
    /// same dial twice replays the arrival). Until each theme draws the registry (phase 4) the page is the old one: a link lands on the
    /// area, and so the tab, that draws its dial, and an old slug lands exactly where it always did. Returns what the link named.</summary>
    public static SettingsLink Open(string? arg)
    {
        var grouping = ShellPolicy.Now.Settings;
        SettingsLink link = SettingsSpec.ParseLink(arg);
        if (arg is not { Length: > 0 } || IsOldSlug(arg, grouping) || !link.Known)
        {
            if (arg is { Length: > 0 }) OpenOldArea(grouping.AreaFromSlug(arg));
            else Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings));
            return link;
        }
        if (link.Dial is { } dial) s_focusState.Value.Value = new SettingsFocus(dial.Id, ++s_focusSeq);
        OpenOldArea(OldAreaOf(link, grouping));
        return link;
    }

    /// <summary>The last dial a link asked for: the registry's pages (phase 4, <c>SettingsPageProps.Focus</c>) open its category, its
    /// parent's expander, glide it into view and highlight it whenever <see cref="SettingsFocus.Seq"/> moves. ROOT-LIFETIME: a remounted
    /// root starts with none.</summary>
    public static IReadSignal<SettingsFocus> Focus => s_focusState.Value;

    static readonly RootState<Signal<SettingsFocus>> s_focusState = new("Settings.Focus", static () => new Signal<SettingsFocus>(SettingsFocus.None));
    static int s_focusSeq;

    /// <summary>Navigate to Settings on the old page's <paramref name="area"/>.</summary>
    static void OpenOldArea(Area area)
    {
        s_tab.Value = (int)area;
        var route = SectionRoute((int)area);
        // Already on a section's page: a tab switch is not a new place, the entry is swapped. From the sections list (Compact) or from another page it is a step.
        if (Shell.Current.Value is { Kind: Shell.RouteKind.Settings, Arg.IsEmpty: false }) Shell.ReplaceCurrent(route);
        else Shell.GoTo(route);
    }

    /// <summary>Owner S's notification simulator (G-218): push a real event of the topic through the pipeline and report what
    /// consumed it. Null → "Send event" answers <see cref="TestEventOutcome.Unavailable"/>.</summary>
    public static Func<NotifyTopic, TestEventResult>? SendTestEvent;

    // ══ 2. PAGE STATE (0.2.9's SettingsPage instance fields; page-lifetime) ════════════════════════════════════════

    // The pivot is ROOT-LIFETIME state (RootScope.cs, owner Q26): a remounted root opens Settings on its first tab, like a fresh
    // navigation. `Open(tab)` before the first mount writes the ambient scope the root then adopts.
    static readonly RootState<Signal<int>> s_tabState = new("Settings.Tab", static () => new Signal<int>(TakePendingTab()));
    static Signal<int> s_tab => s_tabState.Value;

    /// <summary>The pivot's cell as the props carry it (a writable cell travels as a <c>Func</c>: WVTH006).</summary>
    static readonly Func<Signal<int>> s_tabCell = static () => s_tab;

    /// <summary>A grouped pivot's tab click (P6 WK): the area cell moves to the tab's first home area, so the tab on show is that tab.</summary>
    static readonly Action<int> s_openGroup = static g => s_tab.Value = (int)ShellPolicy.Now.Settings.HomeAreaOf(g);

    /// <summary>The tab the NEXT mounted root's pivot opens on, or -1 for the first. A theme pick sets it to Appearance before the
    /// root remounts (owner Q26: the remounted page is a fresh navigation, to Settings › Appearance, picker first); the new
    /// root's pivot cell consumes it when it is created.</summary>
    static int s_pendingTab = -1;

    /// <summary>Ask the next mounted root to open Settings on <paramref name="tab"/>: the theme picker's pick remounts the root (owner Q26), and the
    /// remounted page is a fresh navigation to Settings › Appearance with the picker as its first row, instead of General.</summary>
    public static void OpenNextMountOn(Area area) => s_pendingTab = (int)area;

    /// <summary>The same for the area holding <paramref name="tab"/>'s first section (the pre-areas seam, kept for themes written against it).</summary>
    public static void OpenNextMountOn(Tab tab) => OpenNextMountOn(HomeArea(tab));

    static int TakePendingTab()
    {
        int tab = s_pendingTab;
        s_pendingTab = -1;
        return tab >= 0 && tab < AreaSlugs.Length ? tab : 0;
    }

    static readonly Signal<int> s_epoch = new(0);

    // Published unconditionally by the host render into `SettingsContext` (the verbs read the same values).
    static IOverlayService? s_overlay => SettingsContext.Overlay;
    static InputHooks? s_hooks => SettingsContext.Hooks;
    static Action<Action> s_post => SettingsContext.Post;

    /// <summary>The page epoch as a read-only signal: what an options view subscribes to for the legacy rows' writes.</summary>
    internal static IReadSignal<int> PageEpoch => s_epoch;

    /// <summary>Re-read every tab body: the settings store is not observable, so a writer that returns "re-read" bumps.</summary>
    internal static void Bump() => s_epoch.Value = s_epoch.Peek() + 1;

    // ══ 3. THE HOST COMPONENT ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page's hooks and nothing else: the context the verbs read, the census trigger, the notices warm read, and the
    /// props the structure's view draws. Every hook is above any branch (hook order).</summary>
    [CoreHost]
    sealed class PageHost : Component
    {
        public override Element Render()
        {
            // UNCONDITIONAL — the context the verbs read (the overlay host, the input hooks, the UI poster) and the theme-change
            // cross-fade's request channel.
            var overlayHost = UseContext(Overlay.Service);
            var inputHooks = UseContext(InputHooks.Current);
            Appearance.BindTransition(UseContext(ThemeControl.Request));
            var post = UsePost();
            SettingsContext.Bind(overlayHost, inputHooks, post);

            UseEffect(() =>
            {
                SeedCensus();
                SettingsFacts.WarmNotices(LoadNotices);
            }, DepKey.Empty);

            // The page's own epoch plus the two foreign epochs the Appearance tab reads: a write on the fullscreen stage's gallery
            // or the player bar re-renders the rows live. Each only ever grows, so the sum changes when any one does.
            int revision = s_epoch.Value + Prefs.PlayerBar.Epoch.Value + Prefs.Stage.Epoch.Value;
            string arg = UseProps<HostProps>().Arg;
            bool section = arg.Length > 0;
            // The structure's grouping (P6 WK): which tab the open area lives in, and where a tab's own slug lands. Tracked: a theme switch regroups.
            var grouping = ShellPolicy.Tracked.Settings;
            int argTab = (int)AreaForArg(arg, grouping);
            // A route that names a section (history, a restored session) opens the pivot on it.
            UseEffect(() => { if (section && s_tab.Peek() != argTab) s_tab.Value = argTab; }, DepKey.From(argTab, section ? 1 : 0));
            int area = Math.Clamp(s_tab.Value, 0, AreaSlugs.Length - 1);

            UseEffect(() => EnterStorage(area == (int)Area.Storage), DepKey.From(area));

            return Slots.View(ViewKeys.SettingsPage, new SettingsPageProps
            {
                Area = area,
                Grouping = grouping,
                Group = grouping.GroupOf((Area)area),
                OpenGroup = s_openGroup,
                Revision = revision,
                AreaCell = s_tabCell,
                OpenSection = section,
                OpenArea = static i => { s_tab.Value = i; Shell.GoTo(SectionRoute(i)); },
                CloseSection = static () =>
                {
                    // The history decides: the entry below is the sections list (Settings, no arg) -> Back; else the page is swapped for the list.
                    if (Shell.TryPeekBack(out var below) && below is { Kind: Shell.RouteKind.Settings, Arg.IsEmpty: true }) Shell.GoBack();
                    else Shell.ReplaceCurrent(new Shell.Route(Shell.RouteKind.Settings));
                },
                Storage = area == (int)Area.Storage ? StorageFacts() : SettingsStorageProps.Idle,
            });
        }
    }

    // ══ 4. LINKS ON THE OLD PAGES (until each theme draws the registry, phase 4) ═══════════════════════════════════════

    /// <summary>A name the old page always answered to: an area's slug, a tab slug of <paramref name="grouping"/>, or an old alias.</summary>
    static bool IsOldSlug(string arg, Wavee.Theming.SettingsGrouping grouping)
    {
        if (IsAreaSlug(arg)) return true;
        foreach (var tab in grouping.Tabs)
            if (string.Equals(tab.Slug, arg, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The area a route's arg opens on the old page: an old slug where it always landed, a link where its dial is drawn.</summary>
    static Area AreaForArg(string arg, Wavee.Theming.SettingsGrouping grouping)
    {
        if (arg.Length == 0 || IsOldSlug(arg, grouping)) return grouping.AreaFromSlug(arg);
        SettingsLink link = SettingsSpec.ParseLink(arg);
        return link.Known ? OldAreaOf(link, grouping) : grouping.AreaFromSlug(arg);
    }

    /// <summary>The area (so the tab, under <paramref name="grouping"/>) that draws the link's dial, or its category's first dial the old
    /// pages drew; the old default for a category with none (the Settings home).</summary>
    static Area OldAreaOf(in SettingsLink link, Wavee.Theming.SettingsGrouping grouping)
    {
        if (OldPartOf(link) is { } part)
        {
            int tab = grouping.GroupOfPart(part);
            if (AreaOf(part.Tab, part.Section) is { } area && (tab < 0 || grouping.GroupOf(area) == tab)) return area;
            if (tab >= 0) return grouping.HomeAreaOf(tab);
        }
        return grouping.AreaFromSlug(null);
    }

    /// <summary>The old section that draws the link's dial: its own; else its parent's (a promoted child rides its parent); else the
    /// Theme options section (the active theme's generated options); else the nearest dial of its group the old pages drew (before it,
    /// then after); else the first such dial of the link's category.</summary>
    static Part? OldPartOf(in SettingsLink link)
    {
        if (link.Dial is { } dial)
        {
            for (SettingsDial? d = dial; d is not null; d = d.Parent is { } parent && SettingsSpec.TryDial(parent, out var up) ? up : null)
                if (SettingsDoc.TryPart(d, out var own)) return own;
            if (dial.Id.StartsWith(SettingsSpec.ThemeOptionPrefix, StringComparison.Ordinal))
                return new Part(Tab.Appearance, Wavee.Theming.ThemeOptionsSection.SectionTitle);
            if (SettingsSpec.TryPlace(dial, out _, out var group))
            {
                int at = -1;
                for (int i = 0; i < group.Dials.Count; i++)
                    if (ReferenceEquals(group.Dials[i], dial)) { at = i; break; }
                for (int i = at - 1; i >= 0; i--)
                    if (SettingsDoc.TryPart(group.Dials[i], out var before)) return before;
                for (int i = at + 1; at >= 0 && i < group.Dials.Count; i++)
                    if (SettingsDoc.TryPart(group.Dials[i], out var after)) return after;
            }
        }
        foreach (var group in link.Category.Groups)
            foreach (var d in group.Dials)
                if (SettingsDoc.TryPart(d, out var first)) return first;
        return null;
    }
}

/// <summary>A link's arrival at one dial: the dial's registry id and a sequence number that grows on every request, so asking for the same
/// dial twice replays the arrival (design 4.9).</summary>
/// <param name="DialId">The dial (<see cref="SettingsDial.Id"/>), or empty for none.</param>
/// <param name="Seq">Grows with every link to a dial.</param>
public readonly record struct SettingsFocus(string DialId, int Seq)
{
    /// <summary>No dial asked for.</summary>
    public static SettingsFocus None => new("", 0);
}
