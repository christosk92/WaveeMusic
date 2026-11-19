// ── Screens/Settings.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Settings page's CORE HOST: the install call, the route's mount point, the pivot's tab cell and the deep-link seams, the
// page epoch every writer bumps, and the one component (`PageHost`) that keeps the hooks and publishes the page's context
//
// Role: CORE (themes plan 6.1, T11b; settings rework phase 4). The page's PICTURE — the head, the search box, the strip or
// section list, the category bodies, the rows — is the structure's `Pages.Settings` view, drawn over the registry
// (`SettingsSpec`, through `SettingsPageModel.cs`). What stays here is what decides and publishes:
//   · `InstallScreens()` / `Page()` / `Open(link)`: the route, the deep link and the open CATEGORY (root-lifetime; a link or
//     `OpenCategory` moves it, a strip writes its cell, Settings entered from another page with no link opens the home). A link
//     (design 4.9) names a category, a dial or any old slug or row id (`SettingsSpec.ParseLink`); a dial link also records the
//     `SettingsFocus` the page plays the arrival from (`SettingsPageModel.ArrivalOf`). A category opened as a place is the route
//     `settings` with the category's slug as its arg (Zune's Compact section page);
//   · the page EPOCH (`Bump()`): the settings store is not observable, so a writer that returns "re-read" bumps it, the host
//     folds it (and the two foreign epochs the Now playing and full-screen dials read) into the props' `Revision`, and the template re-runs;
//   · the page CONTEXT (`SettingsContext.Bind`): the overlay host, the input hooks and the UI poster the verbs read;
//   · the Storage census trigger (the first visit to the Storage category starts it) and the About notices' warm read;
//   · the search box's model (`SettingsSearchModel`: the host re-ranks on a change of its query), the home and the category
//     bodies (`SettingsPageModel.cs`), and the two chords (`FindInPage`: Ctrl+F on Settings; `OpenWithSearch`: Ctrl+,).
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
        // never a place of its own (plan D4). An old slug lands on the category that now holds what it opened (`SettingsSpec.Aliases`).
        Shell.OnSettingsTabRequested = static arg => Open(arg);
        Shell.OnSettingsEntered = static () => EnteredFresh();
        ReleaseNotes.InstallUi();
        Feedback.InstallUi();
        Setup.InstallWizard();
    }

    // MOUNT POINT (stage B contract)
    public static Element Page() => Embed.Comp(new HostProps(""), static () => new PageHost());

    /// <summary>The page for a route: the route's arg, a category's slug, is the OPEN SECTION (the Compact sections-then-section page; every other tier keeps
    /// its strip). The slot is the same for every arg (<c>KeyedByArg</c> is false), so opening or closing a section re-pushes the props to the mounted page.</summary>
    public static Element PageFor(in Shell.Route route) => Embed.Comp(new HostProps(Entities.Strings.Resolve(route.Arg)), static () => new PageHost());

    sealed record HostProps(string Arg);

    /// <summary>The route of a category's page (arg = the category's slug).</summary>
    static Shell.Route CategoryRoute(int category)
        => new(Shell.RouteKind.Settings, default, Entities.Strings.Intern(SettingsSpec.Categories[Math.Clamp(category, 0, SettingsSpec.Categories.Count - 1)].Slug.AsSpan()));

    /// <summary>Open Settings from a link (a deep link's arg, the palette's Settings group, a toast, the AI sparkle, the inspector's
    /// `settings focus=`): <c>category</c>, <c>category/dialId</c>, <c>dialId</c>, or any slug or row id a link ever used
    /// (<see cref="SettingsSpec.ParseLink"/>). A dial link records a new <see cref="Focus"/> (its sequence grows on every request, so the
    /// same dial twice replays the arrival). The page opens the link's category (<see cref="Category"/>; no arg, <c>home</c> or an unknown
    /// arg opens the Settings home) and the box clears: a link lands on its category, never on a results page. Returns what the link
    /// named.</summary>
    public static SettingsLink Open(string? arg)
    {
        SettingsLink link = SettingsSpec.ParseLink(arg);
        SettingsSearchModel.Current.Clear();
        if (arg is not { Length: > 0 })
        {
            SetCategory(0);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings));
            return link;
        }
        if (link.Dial is { } dial) s_focusState.Value.Value = new SettingsFocus(dial.Id, ++s_focusSeq);
        OpenAt(link.Known ? SettingsPageModel.IndexOf(link.Category) : 0);
        return link;
    }

    /// <summary>Open the category <paramref name="slug"/> names (its own slug or an old one; <c>home</c> or an unknown one opens the Settings
    /// home) as a place: a history step from the Settings home or another page (Zune's Compact drill-in), the entry swapped from another
    /// category's. The box clears. A strip that switches in place writes <see cref="CategoryCell"/> instead (no history step).</summary>
    public static void OpenCategory(string? slug)
    {
        SettingsSearchModel.Current.Clear();
        OpenAt(SettingsPageModel.IndexOf(SettingsSpec.CategoryFromSlug(slug)));
    }

    /// <summary>Ctrl+,: open Settings on its home with its box focused (owner decision 1, design 4.8).</summary>
    public static void OpenWithSearch()
    {
        Open(null);
        SettingsSearchModel.Current.RequestFocus();
    }

    /// <summary>Ctrl+F while Settings is the page and its page draws a bound box: the box takes the caret instead of the app's search. False
    /// (the app's search takes it) on any other page, or while no bound box is mounted.</summary>
    public static bool FindInPage()
    {
        if (Shell.Current.Peek().Kind != Shell.RouteKind.Settings) return false;
        var search = SettingsSearchModel.Current;
        if (!search.HasBox) return false;
        search.RequestFocus();
        return true;
    }

    /// <summary>The open category: an index into <see cref="SettingsPageModel.Categories"/>, 0 the Settings home. ROOT-LIFETIME: a remounted
    /// root opens the home (or Appearance after a theme pick). Settings entered from another page with no link opens the home
    /// (<see cref="EnteredFresh"/>).</summary>
    public static IReadSignal<int> Category => s_categoryState.Value;

    /// <summary>The category cell as the props carry it (a strip binds it; a writable cell travels as a <c>Func</c>: WVTH006).</summary>
    public static Func<Signal<int>> CategoryCell => s_categoryCell;

    static readonly RootState<Signal<int>> s_categoryState = new("Settings.Category", static () => new Signal<int>(TakePendingCategory()));
    static readonly Func<Signal<int>> s_categoryCell = static () => s_categoryState.Value;
    static readonly Action<string> s_openCategory = static slug => OpenCategory(slug);
    /// <summary>The category the NEXT mounted root opens on, or -1 for the home (<see cref="OpenNextMountOn"/>).</summary>
    static int s_pendingCategory = -1;

    static void SetCategory(int index) => s_categoryState.Value.Value = Math.Clamp(index, 0, SettingsSpec.Categories.Count - 1);

    static int TakePendingCategory()
    {
        int category = s_pendingCategory;
        s_pendingCategory = -1;
        return category >= 0 && category < SettingsSpec.Categories.Count ? category : 0;
    }

    /// <summary>Settings was entered from another page with no link (the navigation pane, the palette's Settings row, a menu): the page opens
    /// on its home with an empty box (owner decision 1). Called by the shell's commit (<see cref="Shell.OnSettingsEntered"/>); Back and
    /// Forward keep the category they left.</summary>
    static void EnteredFresh()
    {
        SetCategory(0);
        SettingsSearchModel.Current.Clear();
    }

    /// <summary>The category a route's arg opens: a category's slug, any old slug a restored session or an old link carries (the alias
    /// table), or a link's dial's category; else the home.</summary>
    static int CategoryForArg(string arg) => SettingsPageModel.IndexOf(SettingsSpec.ParseLink(arg).Category);

    /// <summary>Navigate to Settings on <paramref name="category"/>: the home is the bare route (a Compact sections list shows the list), a
    /// category the route with its slug. Already on a category's page, the entry is swapped (a category switch is not a new place); from
    /// the home or from another page it is a step.</summary>
    static void OpenAt(int category)
    {
        SetCategory(category);
        var route = category == 0 ? new Shell.Route(Shell.RouteKind.Settings) : CategoryRoute(category);
        if (Shell.Current.Peek() is { Kind: Shell.RouteKind.Settings, Arg.IsEmpty: false }) Shell.ReplaceCurrent(route);
        else Shell.GoTo(route);
    }

    /// <summary>The props the page host last pushed (the inspector's `settings model` reads what the page was handed).</summary>
    internal static SettingsPageProps? LastPublished { get; private set; }

    /// <summary>The last dial a link asked for: the page (<c>SettingsPageProps.Focus</c>) opens its category, its parent's expander, glides it
    /// into view and highlights it whenever <see cref="SettingsFocus.Seq"/> moves. ROOT-LIFETIME: a remounted root starts with none.</summary>
    public static IReadSignal<SettingsFocus> Focus => s_focusState.Value;

    static readonly RootState<Signal<SettingsFocus>> s_focusState = new("Settings.Focus", static () => new Signal<SettingsFocus>(SettingsFocus.None));
    static int s_focusSeq;

    /// <summary>Owner S's notification simulator (G-218): push a real event of the topic through the pipeline and report what
    /// consumed it. Null → "Send event" answers <see cref="TestEventOutcome.Unavailable"/>.</summary>
    public static Func<NotifyTopic, TestEventResult>? SendTestEvent;

    // ══ 2. PAGE STATE (0.2.9's SettingsPage instance fields; page-lifetime) ════════════════════════════════════════

    /// <summary>Ask the next mounted root to open Settings on the category <paramref name="slug"/> names: the theme picker's pick remounts the
    /// root (owner Q26), and the remounted page is a fresh navigation to Settings › Appearance with the picker first. The new root's category
    /// cell consumes it when it is created.</summary>
    public static void OpenNextMountOn(string slug) => s_pendingCategory = SettingsPageModel.IndexOf(slug);

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

            // The page's own epoch plus the two foreign epochs the Now playing and full-screen dials read: a write on the fullscreen stage's
            // gallery or the player bar re-renders the rows live. Each only ever grows, so the sum changes when any one does.
            int revision = s_epoch.Value + Prefs.PlayerBar.Epoch.Value + Prefs.Stage.Epoch.Value;
            string arg = UseProps<HostProps>().Arg;
            bool section = arg.Length > 0;

            // The open category (a route that names one, from history or a restored session, opens it), the box's ranking (an auto-tracked
            // effect: it re-runs on a change of the query alone, never re-rendering this host), and a link's arrival.
            var search = SettingsSearchModel.Current;
            UseEffect(() => search.Refresh(search.Query.Value));
            int argCategory = section ? CategoryForArg(arg) : -1;
            UseEffect(() => { if (argCategory >= 0 && s_categoryState.Value.Peek() != argCategory) SetCategory(argCategory); }, DepKey.From(argCategory));
            int category = Math.Clamp(s_categoryState.Value.Value, 0, SettingsSpec.Categories.Count - 1);
            SettingsArrival arrival = SettingsPageModel.ArrivalOf(s_focusState.Value.Value, ShellPolicy.Tracked.SettingsPage.HighlightMs);
            bool storage = category == StorageCategory;

            UseEffect(() => EnterStorage(storage), DepKey.From(storage ? 1 : 0));

            var props = new SettingsPageProps
            {
                Revision = revision,
                OpenSection = section,
                CloseSection = static () =>
                {
                    // The history decides: the entry below is the sections list (Settings, no arg) -> Back; else the page is swapped for the list.
                    if (Shell.TryPeekBack(out var below) && below is { Kind: Shell.RouteKind.Settings, Arg.IsEmpty: true }) Shell.GoBack();
                    else Shell.ReplaceCurrent(new Shell.Route(Shell.RouteKind.Settings));
                },
                Storage = storage ? StorageFacts() : SettingsStorageProps.Idle,
                Category = category,
                Categories = SettingsPageModel.Categories,
                CategoryCell = s_categoryCell,
                OpenCategory = s_openCategory,
                Focus = arrival,
                Search = search,
                Home = SettingsHomeSource.Shared,
                Body = SettingsBodySource.Shared,
            };
            LastPublished = props;
            return Slots.View(ViewKeys.SettingsPage, props);
        }
    }

    /// <summary>The Storage category's index (its census starts on the first visit).</summary>
    static int StorageCategory => SettingsPageModel.IndexOf("storage");
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
