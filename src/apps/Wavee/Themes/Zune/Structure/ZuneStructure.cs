// ── Themes/Zune/Structure/ZuneStructure.cs — the Zune structure (themes plan 6.3; P5 Z0) ─────────────────────────────────────────────
//
// THE SKELETON. Zune's views arrive one family per lane (P5 rounds C-H). Each lane implements ITS registrar, a C# partial method declared
// below, in its own file `Structure\ZuneStructure.<Family>.cs`:
//
//     public sealed partial class ZuneStructure
//     {
//         static partial void RegisterFrame(ViewRegistryBuilder views)
//         {
//             views.Add(ViewKeys.ShellFrame, ZuneFrame.Template, "zune.shell.frame");
//         }
//     }
//
// An unimplemented registrar compiles away. `RegisterOwn` runs every registrar, then the Common blocks every theme shares and the keys
// Zune draws as Nothing (the narrow drawer and the masthead: Zune has no drawer, and its page heads are the band's row 2).
//
// THE STAND-IN (until every key is Zune's, P5 Z9). A key no theme registers renders nothing (the engine's slot host) and a missing style
// key throws (`ViewContext.StyleOf`), so a half-built Zune would be a blank, broken window. The composition (`ZuneTheme.Create`) therefore
// hands the structure a STAND-IN registration, Classic's. `Register` registers Zune's own views first, then copies every key Zune does
// not draw yet from the stand-in (`ViewRegistryBuilder.AddMissing`): picking Zune in Developer mode shows Zune's views where a family
// has landed and Classic's elsewhere, all under Zune's style, policies, tiers and capabilities (no tabs, no nav tree, no Forward).
// `new ZuneStructure()` (no stand-in) registers Zune's own views only: what the Zune registry tests read.
//
// OPTIONS (technical plan 6.3, the Z-Theme-Options board): `background` {ArtworkWash, Mix, None}, `panoramaFit` {Bleed, Fit} and
// `bandPins`, stored as `theme.zune.structure.<id>`; the structure parameters (Hidden, never shown or stored; a composition such as
// Panorama sets them) `collapseBand`, `pivotOverflow`, `seekTimes`, `panelMode` and `maxDockedPanes`, each exercised by the WVTH011
// probe matrix (`analyzer-input\WVTH011.matrix.txt`). The values reach the core through `ZunePolicies.For(ZuneParameters)`, and the
// Zune views through `ZuneOptions`.

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The structure of Zune.</summary>
public sealed partial class ZuneStructure : IStructureLayer
{
    /// <summary>The structure id.</summary>
    public const string LayerId = "Zune.Structure";

    private readonly Action<ViewRegistryBuilder>? _standIn;

    /// <summary>Zune's own views only.</summary>
    public ZuneStructure() : this(null) { }

    /// <summary>The structure with a stand-in registration (the composition passes Classic's): registered in place of Zune's own views
    /// until the engine can merge the two key by key (see the file header).</summary>
    public ZuneStructure(Action<ViewRegistryBuilder>? standIn) => _standIn = standIn;

    /// <inheritdoc />
    public string Id => LayerId;

    /// <summary>True when this instance registers the stand-in in place of Zune's own views.</summary>
    public bool UsesStandIn => _standIn is not null;

    /// <inheritdoc />
    public void Register(ViewRegistryBuilder views)
    {
        ArgumentNullException.ThrowIfNull(views);
        RegisterOwn(views);
        if (_standIn is { } standIn)
        {
            // Every key Zune does not draw yet comes from the stand-in, key by key: Zune's own views always win.
            var fallback = new ViewRegistryBuilder();
            standIn(fallback);
            views.AddMissing(fallback.Build());
        }
    }

    /// <summary>Zune's own views: every family's registrar, the Common blocks, and Nothing for the keys Zune draws no picture for.</summary>
    public static void RegisterOwn(ViewRegistryBuilder views)
    {
        ArgumentNullException.ThrowIfNull(views);
        RegisterFrame(views);
        RegisterBand(views);
        RegisterLists(views);
        RegisterPlayer(views);
        RegisterStage(views);
        RegisterStageMore(views);
        RegisterDiscovery(views);
        RegisterDetail(views);
        RegisterArtist(views);
        RegisterPlaylistLiked(views);
        RegisterShowEpisodeConcert(views);
        RegisterCollection(views);
        RegisterOverlays(views);
        RegisterScreens(views);

        // The Common blocks every theme shares: history, the Home customizer, the frame-rate overlay, diagnostics, logs, the module page.
        CommonBlocks.Register(views, ZuneModulePage.Template, "zune.module.page");
        // No drawer (the band is the navigation at every tier) and no masthead (a page's title and views are the band's row 2).
        CommonNothing.Register(views, ViewKeys.ShellNarrowDrawer);
        CommonNothing.Register(views, ViewKeys.ShellMasthead);
    }

    // The registrars, one per family lane (P5-plan "Ground rules"): each is implemented in `Structure\ZuneStructure.<Family>.cs`.

    /// <summary>Z2a: the frame, the title bar, search, the palette, the history menu, the content states, the page head, the panel's frame.</summary>
    static partial void RegisterFrame(ViewRegistryBuilder views);

    /// <summary>Z2b: the band (the nav pane keys) and the material.</summary>
    static partial void RegisterBand(ViewRegistryBuilder views);

    /// <summary>Z3: the track table, the eager row, the episode row, the surface, the lyrics lines, the artist chart, the track drawer.</summary>
    static partial void RegisterLists(ViewRegistryBuilder views);

    /// <summary>Z4a: the player bar and its parts, the device picker, the now-playing hero, the friends panel, the lyrics header, next up, the queue panel.</summary>
    static partial void RegisterPlayer(ViewRegistryBuilder views);

    /// <summary>Z4b: the stage's frame, the Card and LargeArt layouts.</summary>
    static partial void RegisterStage(ViewRegistryBuilder views);

    /// <summary>Z4c: the rest of the stage (hero, artist name and pane, the queue's stage pane, the lyrics view, video).</summary>
    static partial void RegisterStageMore(ViewRegistryBuilder views);

    /// <summary>Z5: Home and its zones, Home.Quickplay, Browse, a category, Search.</summary>
    static partial void RegisterDiscovery(ViewRegistryBuilder views);

    /// <summary>Z6a1: the detail frame and the album.</summary>
    static partial void RegisterDetail(ViewRegistryBuilder views);

    /// <summary>Z6a2: the artist page and its family.</summary>
    static partial void RegisterArtist(ViewRegistryBuilder views);

    /// <summary>Z6b1: the playlist and Liked Songs.</summary>
    static partial void RegisterPlaylistLiked(ViewRegistryBuilder views);

    /// <summary>Z6b2: the show, the episode and the concerts.</summary>
    static partial void RegisterShowEpisodeConcert(ViewRegistryBuilder views);

    /// <summary>Z7: the collection (Library, Recents, Profile).</summary>
    static partial void RegisterCollection(ViewRegistryBuilder views);

    /// <summary>Z8a: the overlays and the dialogs.</summary>
    static partial void RegisterOverlays(ViewRegistryBuilder views);

    /// <summary>Z8b: the screens (Settings, the theme picker and options, setup, what's new, release notes, feedback, crash).</summary>
    static partial void RegisterScreens(ViewRegistryBuilder views);

    // ── the options ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Background: what sits behind page titles. Artwork wash tints the top of albums, artists and playlists from their cover; mix
    /// drifts artist photos behind Home and now playing; none is the flat ground. Live (the wash fades). Read by the material (Z2b).</summary>
    public static SelectOption<ZuneBackground> Background { get; } = new(
        Id: "background", TitleKey: Strings.Theme.Zune.Options.Background, DescriptionKey: Strings.Theme.Zune.Options.BackgroundSub,
        Group: Strings.Theme.Zune.Options.Group.Pages, Default: ZuneBackground.ArtworkWash,
        Choices:
        [
            new SelectChoice<ZuneBackground>(ZuneBackground.ArtworkWash, Strings.Theme.Zune.Options.BackgroundArtworkWash),
            new SelectChoice<ZuneBackground>(ZuneBackground.Mix, Strings.Theme.Zune.Options.BackgroundMix),
            new SelectChoice<ZuneBackground>(ZuneBackground.None, Strings.Theme.Zune.Options.BackgroundNone),
        ],
        Picker: PickerStyle.Radio);

    /// <summary>Panorama titles: they bleed off the edge, or fit the window. A Transaction option (it changes the arrangement); a structure
    /// option because bleed is arrangement (BP 1.11). Panorama fixes it to Fit and hides it.</summary>
    public static SelectOption<PanoramaFit> PanoramaFitOption { get; } = new SelectOption<PanoramaFit>(
        Id: "panoramaFit", TitleKey: Strings.Theme.Zune.Options.PanoramaFit, DescriptionKey: Strings.Theme.Zune.Options.PanoramaFitSub,
        Group: Strings.Theme.Zune.Options.Group.Pages, Default: PanoramaFit.Bleed,
        Choices:
        [
            new SelectChoice<PanoramaFit>(PanoramaFit.Bleed, Strings.Theme.Zune.Options.PanoramaFitBleed),
            new SelectChoice<PanoramaFit>(PanoramaFit.Fit, Strings.Theme.Zune.Options.PanoramaFitFit),
        ],
        Picker: PickerStyle.Radio) with { Apply = ApplyMode.Transaction };

    /// <summary>Pins in the band (from `sidebar.zune.pins`): up to six pinned items beside the pivots, at Standard and Huge (the window tier
    /// decides too: Small and Compact show none). Live (the pins enter and leave).</summary>
    public static ToggleOption BandPins { get; } = new(
        Id: "bandPins", TitleKey: Strings.Theme.Zune.Options.BandPins, DescriptionKey: Strings.Theme.Zune.Options.BandPinsSub,
        Group: Strings.Theme.Zune.Options.Group.Band, Default: true);

    /// <summary>Structure parameter: the band collapses to row 1 on scroll (Panorama sets false).</summary>
    public static ToggleOption CollapseBand { get; } = new ToggleOption(
        Id: "collapseBand", TitleKey: Strings.Theme.Zune.Options.Parameter.CollapseBand, DescriptionKey: null,
        Group: Strings.Theme.Zune.Options.Group.Band, Default: true) with { Visibility = OptionVisibility.Hidden };

    /// <summary>Structure parameter: the band's row 1 scrolls under an edge fade, or opens the rest from a "more" item (Panorama).</summary>
    public static SelectOption<PivotOverflow> PivotOverflowParameter { get; } = new SelectOption<PivotOverflow>(
        Id: "pivotOverflow", TitleKey: Strings.Theme.Zune.Options.Parameter.PivotOverflow, DescriptionKey: null,
        Group: Strings.Theme.Zune.Options.Group.Band, Default: PivotOverflow.EdgeFadeScroll,
        Choices:
        [
            new SelectChoice<PivotOverflow>(PivotOverflow.EdgeFadeScroll, Strings.Theme.Zune.Options.Parameter.EdgeFadeScroll),
            new SelectChoice<PivotOverflow>(PivotOverflow.MoreMenu, Strings.Theme.Zune.Options.Parameter.MoreMenu),
        ],
        Picker: PickerStyle.Combo) with { Visibility = OptionVisibility.Hidden };

    /// <summary>Structure parameter: the play bar's times sit in the face column, or at both ends of the seek row (Panorama).</summary>
    public static SelectOption<SeekTimes> SeekTimesParameter { get; } = new SelectOption<SeekTimes>(
        Id: "seekTimes", TitleKey: Strings.Theme.Zune.Options.Parameter.SeekTimes, DescriptionKey: null,
        Group: Strings.Theme.Zune.Options.Group.Band, Default: SeekTimes.InFace,
        Choices:
        [
            new SelectChoice<SeekTimes>(SeekTimes.InFace, Strings.Theme.Zune.Options.Parameter.InFace),
            new SelectChoice<SeekTimes>(SeekTimes.AtSliderEnds, Strings.Theme.Zune.Options.Parameter.AtSliderEnds),
        ],
        Picker: PickerStyle.Combo) with { Visibility = OptionVisibility.Hidden };

    /// <summary>Structure parameter: the right panel docks inline when there is room, or is always an overlay (Panorama exposes it as a
    /// composition option).</summary>
    public static SelectOption<ZunePanelMode> PanelModeParameter { get; } = new SelectOption<ZunePanelMode>(
        Id: "panelMode", TitleKey: Strings.Theme.Zune.Options.Parameter.PanelMode, DescriptionKey: null,
        Group: Strings.Theme.Zune.Options.Group.Band, Default: ZunePanelMode.InlineWhenRoom,
        Choices:
        [
            new SelectChoice<ZunePanelMode>(ZunePanelMode.InlineWhenRoom, Strings.Theme.Zune.Options.Parameter.InlineWhenRoom),
            new SelectChoice<ZunePanelMode>(ZunePanelMode.AlwaysOverlay, Strings.Theme.Zune.Options.Parameter.AlwaysOverlay),
        ],
        Picker: PickerStyle.Combo) with { Visibility = OptionVisibility.Hidden };

    /// <summary>Structure parameter: how many panes the right panel docks at once at most (queue · lyrics · friends at Huge 4K: 3;
    /// Panorama sets 2). The rungs `pane.second` / `pane.third` say where each may dock; this caps them.</summary>
    public static SliderOption MaxDockedPanes { get; } = new SliderOption(
        Id: "maxDockedPanes", TitleKey: Strings.Theme.Zune.Options.Parameter.MaxDockedPanes, DescriptionKey: null,
        Group: Strings.Theme.Zune.Options.Group.Band, Default: 3f, Min: 1f, Max: 3f, Step: 1f, UnitKey: null)
        with { Visibility = OptionVisibility.Hidden };

    /// <summary>The layer-qualified ids Settings and the palette know the user options by.</summary>
    public const string BackgroundQualifiedId = "structure.background", PanoramaFitQualifiedId = "structure.panoramaFit",
        BandPinsQualifiedId = "structure.bandPins";

    private static readonly ThemeOptionSchema s_options = ThemeOptionSchema.Of(Background, PanoramaFitOption, BandPins,
        CollapseBand, PivotOverflowParameter, SeekTimesParameter, PanelModeParameter, MaxDockedPanes);

    /// <inheritdoc />
    public ThemeOptionSchema Options => s_options;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ZunePolicies.For(ZuneParameters.From(structure));

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => ZunePolicies.Capabilities;

    /// <inheritdoc />
    public RoleSet RequiredRoles => RoleSet.None;

    /// <summary>None: every Zune option is a declared option (Zune binds no legacy key).</summary>
    public IReadOnlyList<LegacyOptionRow> LegacyRows { get; } = [];
}
