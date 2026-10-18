// ── Screens/Settings.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Settings page's CORE HOST: the install call, the route's mount point, the pivot's tab cell and the deep-link seams, the
// page epoch every writer bumps, and the one component (`PageHost`) that keeps the hooks and publishes the page's context
//
// Role: CORE (themes plan 6.1, T11b). The page's PICTURE — the masthead, the seven-tab pivot, the scroll lane, the section
// headers, the row controls, the cards — is the structure's `Pages.Settings` view (Classic's `ClassicSettings*.cs`), drawn
// over `SettingsDoc`. What stays here is what decides and publishes:
//   · `InstallScreens()` / `Page()` / `Open(tab)` / `TabFromSlug`: the route, the deep link and the pivot's root-lifetime cell;
//   · the page EPOCH (`Bump()`): the settings store is not observable, so a writer that returns "re-read" bumps it, the host
//     folds it (and the two foreign epochs the Appearance tab reads) into the props' `Revision`, and the template re-runs;
//   · the page CONTEXT (`SettingsContext.Bind`): the overlay host, the input hooks and the UI poster the verbs read;
//   · the Storage census trigger (the first visit to the tab starts it) and the About notices' warm read.
//
// MOUNT POINTS / SEAMS: `Settings.InstallScreens()` (the orchestrator's one call), `Settings.Page()`, `Settings.Open(Tab)`;
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
        // `wavee://open?route=settings&arg=<slug>`: the arg is a TAB slug, never a place of its own (plan D4).
        Shell.OnSettingsTabRequested = static slug => Open(TabFromSlug(slug));
        ReleaseNotes.InstallUi();
        Feedback.InstallUi();
        Setup.InstallWizard();
    }

    // MOUNT POINT (stage B contract)
    public static Element Page() => Embed.Comp(new HostProps(""), static () => new PageHost());

    /// <summary>The page for a route: the route's arg, a tab slug, is the OPEN SECTION (the Compact sections-then-section page; every other tier keeps the pivot).
    /// The slot is the same for every arg (<c>KeyedByArg</c> is false), so opening or closing a section re-pushes the props to the mounted page.</summary>
    public static Element PageFor(in Shell.Route route) => Embed.Comp(new HostProps(Entities.Strings.Resolve(route.Arg)), static () => new PageHost());

    sealed record HostProps(string Arg);

    /// <summary>The route of a tab's section page (arg = the tab's slug).</summary>
    static Shell.Route SectionRoute(int tab) => new(Shell.RouteKind.Settings, default, Entities.Strings.Intern(TabSlugs[Math.Clamp(tab, 0, TabSlugs.Length - 1)].AsSpan()));

    /// <summary>Navigate to Settings on <paramref name="tab"/> (the palette, a toast's "Manage", a deep link).</summary>
    public static void Open(Tab tab)
    {
        s_tab.Value = (int)tab;
        var route = SectionRoute((int)tab);
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

    /// <summary>The tab the NEXT mounted root's pivot opens on, or -1 for the first. A theme pick sets it to Appearance before the
    /// root remounts (owner Q26: the remounted page is a fresh navigation, to Settings › Appearance, picker first); the new
    /// root's pivot cell consumes it when it is created.</summary>
    static int s_pendingTab = -1;

    /// <summary>Ask the next mounted root to open Settings on <paramref name="tab"/>: the theme picker's pick remounts the root (owner Q26), and the
    /// remounted page is a fresh navigation to Settings › Appearance with the picker as its first row, instead of General.</summary>
    public static void OpenNextMountOn(Tab tab) => s_pendingTab = (int)tab;

    static int TakePendingTab()
    {
        int tab = s_pendingTab;
        s_pendingTab = -1;
        return tab >= 0 && tab < TabSlugs.Length ? tab : 0;
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
            int argTab = (int)TabFromSlug(arg);
            // A route that names a section (history, a restored session) opens the pivot on it.
            UseEffect(() => { if (section && s_tab.Peek() != argTab) s_tab.Value = argTab; }, DepKey.From(argTab, section ? 1 : 0));
            int tab = Math.Clamp(s_tab.Value, 0, TabSlugs.Length - 1);

            UseEffect(() => EnterStorage(tab == (int)Tab.Storage), DepKey.From(tab));

            return Slots.View(ViewKeys.SettingsPage, new SettingsPageProps
            {
                Tab = tab,
                Revision = revision,
                TabCell = s_tabCell,
                OpenSection = section,
                OpenTab = static i => { s_tab.Value = i; Shell.GoTo(SectionRoute(i)); },
                CloseSection = static () =>
                {
                    // The history decides: the entry below is the sections list (Settings, no arg) -> Back; else the page is swapped for the list.
                    if (Shell.TryPeekBack(out var below) && below is { Kind: Shell.RouteKind.Settings, Arg.IsEmpty: true }) Shell.GoBack();
                    else Shell.ReplaceCurrent(new Shell.Route(Shell.RouteKind.Settings));
                },
                Storage = tab == (int)Tab.Storage ? StorageFacts() : SettingsStorageProps.Idle,
            });
        }
    }
}
