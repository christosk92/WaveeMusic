// ── Themes/WinUi/Structure/WinUiStructure.cs — the WinUI structure (themes plan 6.2; P6 W0) ────────────────────────────────────────────
//
// THE SKELETON. WinUI's views arrive one family per lane (P6 waves 2-4). Each lane implements ITS registrar, a C# partial method declared
// below, in its own file `Structure\WinUiStructure.<Family>.cs`:
//
//     public sealed partial class WinUiStructure
//     {
//         static partial void RegisterFrame(ViewRegistryBuilder views)
//         {
//             views.Add(ViewKeys.ShellFrame, WinUiFrame.Template, "winui.shell.frame");
//         }
//     }
//
// An unimplemented registrar compiles away. `RegisterOwn` runs every registrar, then the Common blocks every theme shares (W8b may hand
// them its own module page through `ModulePage`) and the keys WinUI draws as Nothing (the Home quickplay strip: WinUI's pins are the nav
// pane's, as in Classic).
//
// THE STAND-IN (until every key is WinUI's, P6 W9). A key no theme registers renders nothing (the engine's slot host) and a missing style
// key throws (`ViewContext.StyleOf`), so a half-built WinUI would be a blank, broken window. The composition (`WinUiTheme.Create`) therefore
// hands the structure a STAND-IN registration, Classic's. `Register` registers WinUI's own views first, then copies every key WinUI does
// not draw yet from the stand-in (`ViewRegistryBuilder.AddMissing`). `new WinUiStructure()` (no stand-in) registers WinUI's own views only:
// what the WinUI registry tests read.
//
// OPTIONS (technical plan 6.2, the W-ThemeOptions board; exactly six, no hidden, opt-in or composition-only member): the structure's three
// are `navigationPane` {Expanded, Compact, Automatic} (Live: the pane's mode animates its width), `tabs` (Live: the strip's row animates)
// and `artworkInRows` (Transaction: row extents), stored as `theme.winui.structure.<id>`. The style's three (`backdrop`, `density`,
// `useWindowsAccent`) are `WinUiStyle`'s. The values reach the core through `WinUiPolicies.For(WinUiParameters)` and the WinUI views
// through `WinUiOptions`.

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The structure of WinUI.</summary>
public sealed partial class WinUiStructure : IStandInStructure
{
    /// <summary>The structure id.</summary>
    public const string LayerId = "WinUi.Structure";

    private readonly Action<ViewRegistryBuilder>? _standIn;

    /// <summary>WinUI's own views only.</summary>
    public WinUiStructure() : this(null) { }

    /// <summary>The structure with a stand-in registration (the composition passes Classic's) for every key WinUI does not draw yet.</summary>
    public WinUiStructure(Action<ViewRegistryBuilder>? standIn) => _standIn = standIn;

    /// <inheritdoc />
    public string Id => LayerId;

    /// <summary>True when this instance fills the keys WinUI does not draw from the stand-in.</summary>
    public bool UsesStandIn => _standIn is not null;

    /// <inheritdoc />
    void IStandInStructure.RegisterOwnViews(ViewRegistryBuilder views) => RegisterOwn(views);

    /// <inheritdoc />
    public void Register(ViewRegistryBuilder views)
    {
        ArgumentNullException.ThrowIfNull(views);
        RegisterOwn(views);
        if (_standIn is { } standIn)
        {
            // Every key WinUI does not draw yet comes from the stand-in, key by key: WinUI's own views always win.
            var fallback = new ViewRegistryBuilder();
            standIn(fallback);
            views.AddMissing(fallback.Build());
        }
    }

    /// <summary>WinUI's own views: every family's registrar.</summary>
    public static void RegisterOwn(ViewRegistryBuilder views)
    {
        ArgumentNullException.ThrowIfNull(views);
        RegisterFrame(views);
        RegisterNav(views);
        RegisterLists(views);
        RegisterScreens(views);
        RegisterPlayer(views);
        RegisterStage(views);
        RegisterStageMore(views);
        RegisterDiscovery(views);
        RegisterDetail(views);
        RegisterEntities(views);
        RegisterCollection(views);
        RegisterOverlays(views);

        // The Common blocks every theme shares: history, the Home customizer, the frame-rate overlay, diagnostics, logs, the module page.
        ViewTemplate<ModulePageProps>? module = null;
        string moduleId = "common.module.page";
        ModulePage(ref module, ref moduleId);
        CommonBlocks.Register(views, module, moduleId);
        // No quickplay strip on Home: WinUI's pins are the nav pane's Pinned section (W-Home; W5 replaces this line if it draws one).
        CommonNothing.Register(views, ViewKeys.HomeQuickplay);
    }

    // The registrars, one per family lane (P6-plan §4): each is implemented in `Structure\WinUiStructure.<Family>.cs`.

    /// <summary>W2a: the frame, the title bar, search and its suggestions, the palette, the history menu, the file drop, the drag chip, the
    /// cover scrim, the content state, the masthead.</summary>
    static partial void RegisterFrame(ViewRegistryBuilder views);

    /// <summary>W2b: the nav pane keys and the narrow drawer.</summary>
    static partial void RegisterNav(ViewRegistryBuilder views);

    /// <summary>W3: the track table, the eager row, the episode row, the surface, the lyrics lines, the artist chart, the track drawer.</summary>
    static partial void RegisterLists(ViewRegistryBuilder views);

    /// <summary>W8b: the screens (Settings, the theme picker and options, setup, release notes, what's new, feedback, crash).</summary>
    static partial void RegisterScreens(ViewRegistryBuilder views);

    /// <summary>W4a: the player bar and its parts, the device picker, the now-playing hero, the queue panel, the panel's frame, friends.</summary>
    static partial void RegisterPlayer(ViewRegistryBuilder views);

    /// <summary>W4b: the stage's root, top bar, transport, options, gallery and caption; the Card and LargeArt layouts.</summary>
    static partial void RegisterStage(ViewRegistryBuilder views);

    /// <summary>W4c: the Centered and Artist layouts, the stage hero, artist name and pane, the lyrics view, the queue's stage pane, video.</summary>
    static partial void RegisterStageMore(ViewRegistryBuilder views);

    /// <summary>W5: Home and its sections, Browse, a category and its sections, Search, no results.</summary>
    static partial void RegisterDiscovery(ViewRegistryBuilder views);

    /// <summary>W6a: the detail frame, the album, the artist and the discography.</summary>
    static partial void RegisterDetail(ViewRegistryBuilder views);

    /// <summary>W6b: the playlist, Liked Songs, the show, the episode and the concerts.</summary>
    static partial void RegisterEntities(ViewRegistryBuilder views);

    /// <summary>W7: the collection (Library, the library list, Recents, Profile, local files).</summary>
    static partial void RegisterCollection(ViewRegistryBuilder views);

    /// <summary>W8a: the overlays and the dialogs.</summary>
    static partial void RegisterOverlays(ViewRegistryBuilder views);

    /// <summary>W8b: WinUI's own module page, when it draws one in its page column (the Common block's otherwise).</summary>
    static partial void ModulePage(ref ViewTemplate<ModulePageProps>? module, ref string templateId);

    // ── the options (W-ThemeOptions) ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Navigation pane: expanded, the compact rail, or automatic by window width (NavigationView's 641 / 1008). Live: the pane's
    /// width animates over 167 ms (W2b).</summary>
    public static SelectOption<WinUiNavigationPane> NavigationPane { get; } = new(
        Id: "navigationPane", TitleKey: Strings.Theme.Winui.Options.NavigationPane, DescriptionKey: Strings.Theme.Winui.Options.NavigationPaneSub,
        Group: Strings.Theme.Winui.Options.Group.Window, Default: WinUiNavigationPane.Automatic,
        Choices:
        [
            new SelectChoice<WinUiNavigationPane>(WinUiNavigationPane.Expanded, Strings.Theme.Winui.Options.NavigationPaneExpanded),
            new SelectChoice<WinUiNavigationPane>(WinUiNavigationPane.Compact, Strings.Theme.Winui.Options.NavigationPaneCompact),
            new SelectChoice<WinUiNavigationPane>(WinUiNavigationPane.Automatic, Strings.Theme.Winui.Options.NavigationPaneAutomatic),
        ],
        Picker: PickerStyle.Combo);

    /// <summary>Tabs in the title bar. Off hides the 40 strip in every tier and leaves the 48 toolbar row; the other tabs stay open in the
    /// background and return in place when it is on again (owner Q1: the board). Live: the strip's row animates (W2a).</summary>
    public static ToggleOption Tabs { get; } = new(
        Id: "tabs", TitleKey: Strings.Theme.Winui.Options.Tabs, DescriptionKey: Strings.Theme.Winui.Options.TabsSub,
        Group: Strings.Theme.Winui.Options.Group.Window, Default: true);

    /// <summary>Artwork in track rows: playlist, Liked and search rows show a 40 cover over two lines (48); off, one 40 line. Album rows never
    /// show covers. Transaction (the row extents change).</summary>
    public static ToggleOption ArtworkInRows { get; } = new ToggleOption(
        Id: "artworkInRows", TitleKey: Strings.Theme.Winui.Options.ArtworkInRows, DescriptionKey: Strings.Theme.Winui.Options.ArtworkInRowsSub,
        Group: Strings.Settings.ThemeOptions.Group.TrackLists, Default: true) with { Apply = ApplyMode.Transaction };

    /// <summary>The layer-qualified ids Settings and the palette know the options by.</summary>
    public const string NavigationPaneQualifiedId = "structure.navigationPane", TabsQualifiedId = "structure.tabs",
        ArtworkInRowsQualifiedId = "structure.artworkInRows";

    private static readonly ThemeOptionSchema s_options = ThemeOptionSchema.Of(NavigationPane, Tabs, ArtworkInRows);

    /// <inheritdoc />
    public ThemeOptionSchema Options => s_options;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => WinUiPolicies.For(WinUiParameters.From(structure));

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => WinUiPolicies.CapabilitiesFor(WinUiParameters.From(structure));

    /// <inheritdoc />
    public RoleSet RequiredRoles => RoleSet.None;

    /// <summary>None: every WinUI option is a declared option (WinUI binds no legacy key).</summary>
    public IReadOnlyList<LegacyOptionRow> LegacyRows { get; } = [];
}
