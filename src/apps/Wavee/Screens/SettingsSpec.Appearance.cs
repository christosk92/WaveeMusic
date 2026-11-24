// ── Screens/SettingsSpec.Appearance.cs — Appearance: how Wavee looks (the dials and their verbs) ───────────────────────────────
//
// Role: CORE (settings rework phase 1). Theme (the picker) · ‹Theme› options (the active theme's options and the legacy rows its
// structure lists) · Mode · Size and text · Motion · Home and library (the daylist clock, the Liked Songs cover, the two opt-in list
// columns). Every write goes through the epoch it always bumped: marquee, fast navigation, the daylist clock, the Liked cover and the
// list columns → `Prefs.Appearance`. A verb returns whether the PAGE must re-read.

using FluentGpu.Dsl;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Colours;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    /// <summary>The Q7 reason a sidebar control gives while the sidebar is being edited.</summary>
    public const string FinishEditingKey = "sidebar.pane.finishEditing";

    /// <summary>The id prefix of every theme option dial (<c>appearance.option.&lt;qualifiedId&gt;</c>).</summary>
    public const string ThemeOptionPrefix = "appearance.option.";

    static SettingsCategory AppearanceCategory() => new("appearance", Strings.Settings.Categories.Appearance.Title,
        Strings.Settings.Categories.Appearance.Strip, Strings.Settings.Categories.Appearance.Subtitle, "Brush",
    [
        // The theme picker: one dial, one writer (`ThemeHost.Switch`). Its value is the index of the selected card among the cards the
        // picker shows (`ThemePickerEntry`s), so a view and the inspector read the same answer.
        Group("theme", Strings.Settings.Groups.Appearance.Theme, Strings.Settings.ThemePicker.Subtitle,
            Make("appearance.theme", K.Picker, Strings.Settings.Appearance.Title,
                SettingsWriter.OfChoice("theme.pick", SettingsVerbs.PickTheme), legacy: ["themePicker"], glyph: "Picture",
                captionKey: Strings.Settings.ThemePicker.Sub, value: _ => SettingsVerbs.ThemeIndex())),

        // The ACTIVE theme's options: its generated dials (`ThemeOptionsSection`, live) and the rows its structure still binds to today's
        // keys (T1b). A structure lists those by `IStructureLayer.LegacyRows`, so a theme with other options, or none, shows none of them.
        new SettingsGroup("options", Strings.Settings.ThemeOptions.TitleKey, null,
        [
            Make("appearance.option.sidebar.reset", K.Action, Strings.Settings.Sidebar.Reset,
                SettingsWriter.OfAction("sidebar.reset", SettingsVerbs.ResetSidebarLayout), legacy: ["sidebarReset"],
                captionKey: Strings.Settings.Sidebar.ResetSub,
                enabled: f => f.SidebarEditing ? SettingsEnablement.Off(FinishEditingKey)
                    : f.SidebarModified ? SettingsEnablement.On : SettingsEnablement.Off(null),
                silent: true, contexts: [SettingsSurfaces.SidebarMenu]),
            Make("appearance.option.sidebar.customize", K.Action, "sidebar.menu.edit",
                SettingsWriter.OfAction("sidebar.edit", SettingsVerbs.EditSidebar), legacy: ["sidebarCustomize"], glyph: "Edit",
                captionKey: "settings.sidebar.editSub", enabled: NotWhileEditing, contexts: [SettingsSurfaces.SidebarMenu]),
            Make("appearance.option.sidebar.resetEverything", K.Action, "sidebar.menu.resetEverything",
                SettingsWriter.OfAction("sidebar.resetEverything", SettingsVerbs.ResetSidebarEverything), legacy: ["sidebarResetEverything"],
                captionKey: "settings.sidebar.resetEverythingSub", enabled: NotWhileEditing, contexts: [SettingsSurfaces.SidebarMenu]),
        ]) { Live = ThemeOptionDials },

        // The core presentation prefs every theme honours (the plan's App mode and Zoom rows; marquee is a core pref, owner Q27).
        Group("mode", Strings.Settings.Groups.Appearance.Mode, null,
            Make("appearance.appMode", K.Segmented, Strings.Settings.Appearance.AppMode,
                SettingsWriter.OfChoice("appearance.mode", SettingsVerbs.SetThemeMode), legacy: ["appMode"], glyph: "Sun",
                captionKey: Strings.Settings.Appearance.ThemeSub, value: s => (int)Appearance.ModeOf(s.Get(Platform.Keys.ThemeMode)),
                choices: [Strings.Settings.Choice.System, Strings.Settings.Choice.Light, Strings.Settings.Choice.Dark], contexts: [SettingsSurfaces.ProfileMenu])),

        // Colours (#184): one colour set for light mode and one for dark, app-wide (whichever theme is active), switched by the App mode above.
        // Index 0 of each is the theme's own colours. While a set is active the accent can come from it; a family with named accents
        // (Catppuccin's fourteen) offers them for the mode on screen.
        Group("colours", Strings.Settings.Groups.Appearance.Colours, Strings.Settings.Appearance.ColoursSub,
            Make("appearance.colours.light", K.Choice, Strings.Settings.Appearance.ColoursLight,
                SettingsWriter.OfChoice("colours.light", i => SettingsVerbs.PickColours(ThemeKind.Light, i)), glyph: "Sun",
                value: _ => SettingsVerbs.ColoursIndex(ThemeKind.Light), liveChoices: () => SettingsVerbs.ColoursChoices(ThemeKind.Light)),
            // My own set (the last choice, "Custom"): its editor opens from this row while it is the pick.
            Make("appearance.colours.custom.light", K.Action, Strings.Settings.Appearance.ColoursCustomEdit,
                SettingsWriter.OfAction("colours.custom.light", static () => SettingsVerbs.EditCustomColours(ThemeKind.Light)), glyph: "Edit",
                captionKey: Strings.Settings.Appearance.ColoursCustomEditSub, visible: static _ => ColourChoice.IsCustom(ThemeKind.Light)),
            Make("appearance.colours.dark", K.Choice, Strings.Settings.Appearance.ColoursDark,
                SettingsWriter.OfChoice("colours.dark", i => SettingsVerbs.PickColours(ThemeKind.Dark, i)), glyph: "Brush",
                value: _ => SettingsVerbs.ColoursIndex(ThemeKind.Dark), liveChoices: () => SettingsVerbs.ColoursChoices(ThemeKind.Dark)),
            Make("appearance.colours.custom.dark", K.Action, Strings.Settings.Appearance.ColoursCustomEdit,
                SettingsWriter.OfAction("colours.custom.dark", static () => SettingsVerbs.EditCustomColours(ThemeKind.Dark)), glyph: "Edit",
                captionKey: Strings.Settings.Appearance.ColoursCustomEditSub, visible: static _ => ColourChoice.IsCustom(ThemeKind.Dark)),
            Make("appearance.colours.accent", K.Segmented, Strings.Settings.Appearance.ColoursAccent,
                SettingsWriter.OfChoice("colours.accent", SettingsVerbs.SetColourAccent), glyph: "Design",
                captionKey: Strings.Settings.Appearance.ColoursAccentSub, value: _ => (int)ColourChoice.AccentSource,
                visible: static _ => ColourChoice.Current.Any,
                choices: [Strings.Settings.Appearance.ColoursAccentTheme, Strings.Settings.Appearance.ColoursAccentPalette, Strings.Settings.Appearance.ColoursAccentOwn]),
            Make("appearance.colours.accentOwn", K.Action, Strings.Settings.Appearance.ColoursAccentOwnPick,
                SettingsWriter.OfAction("colours.accentOwn", static () => SettingsVerbs.ChooseOwnAccent()), glyph: "Design",
                visible: static _ => ColourChoice.Current.Any && ColourChoice.AccentSource == AccentSource.Own),
            Make("appearance.colours.accentPick", K.Choice, Strings.Settings.Appearance.ColoursAccentPick,
                SettingsWriter.OfChoice("colours.accentPick", SettingsVerbs.PickNamedAccent), glyph: "Design",
                value: _ => SettingsVerbs.NamedAccentIndex(), visible: static _ => SettingsVerbs.NamedAccentsShown(),
                liveChoices: SettingsVerbs.NamedAccentChoices),
            // Plan D4: off (the default) the window keeps the set's exact colours; on, the artwork tints it as the theme's own options say.
            // Shown while the mode on screen has a set. The stage, its lyrics and the deck keep the cover's colours either way.
            Make("appearance.colours.artwork", K.Toggle, Strings.Settings.Appearance.ColoursArtwork,
                SettingsWriter.OfToggle("colours.artwork", SettingsVerbs.SetColourArtwork), glyph: "Picture",
                captionKey: Strings.Settings.Appearance.ColoursArtworkSub, value: _ => ColourChoice.ArtworkOn,
                visible: static _ => ColourChoice.SpecOf(Tok.Theme) is not null)),

        Group("sizeAndText", Strings.Settings.Groups.Appearance.SizeAndText, null,
            // A combo, not segments: thirteen items (Auto + twelve rungs). Index 0 is "Auto (N%)" for both Auto and Dense.
            Make("appearance.zoom", K.Choice, Strings.Settings.Appearance.Zoom,
                SettingsWriter.OfChoice("zoom.pick", SettingsVerbs.PickZoom), legacy: ["zoom"], glyph: "Zoom",
                captionKey: Strings.Settings.Appearance.ZoomSub, value: _ => SettingsVerbs.ZoomIndex()),
            Make("appearance.marquee", K.Toggle, Strings.Settings.Appearance.Marquee,
                SettingsWriter.OfToggle("appearance.marquee", on => SettingsVerbs.SetAppearanceToggle(Platform.Keys.MarqueeEnabled, on)),
                legacy: ["marquee"], glyph: "Font",
                captionKey: Strings.Settings.Appearance.MarqueeSub, value: BoolOf(Platform.Keys.MarqueeEnabled))),

        Group("motion", Strings.Settings.Groups.Appearance.Motion, null,
            Make("appearance.fastNavigation", K.Toggle, Strings.Settings.Appearance.FastNavigation,
                SettingsWriter.OfToggle("appearance.fastNavigation", on => SettingsVerbs.SetAppearanceToggle(Platform.Keys.FastNavigation, on)),
                legacy: ["fastNavigation"], glyph: "Forward",
                captionKey: Strings.Settings.Appearance.FastNavigationSub, value: BoolOf(Platform.Keys.FastNavigation)),
            // Connected animations (the owner's switch, 2026-11-16): off, nothing flies or grows (`Shell.Connected`, read per navigation). Only a
            // structure whose pages fly or grow lists it (WinUI today; Classic and Zune when they fly).
            Make("appearance.connectedAnimations", K.Toggle, Strings.Settings.Appearance.ConnectedAnimations,
                SettingsWriter.OfToggle("appearance.connectedAnimations", on => SettingsVerbs.SetAppearanceToggle(Platform.Keys.ConnectedAnimations, on)),
                legacy: ["connectedAnimations"], glyph: "Picture", visible: static f => f.PagesFly,
                captionKey: Strings.Settings.Appearance.ConnectedAnimationsSub, value: BoolOf(Platform.Keys.ConnectedAnimations)),
            // Animation effects follow Windows: a fact (a theme's `PrefReferenceOption` for reduced motion draws this dial).
            Make("appearance.animationEffects", K.Fact, Strings.Settings.ThemeOptions.AnimationEffects, SettingsWriter.ReadOnly,
                legacy: ["pref.reducedMotion"], glyph: "RefineSparkle", captionKey: Strings.Settings.ThemeOptions.AnimationEffectsSub)),

        Group("homeAndLibrary", Strings.Settings.Groups.Appearance.HomeAndLibrary, null,
            // No page re-read: nothing else on the page shows the clock.
            Make("appearance.daylistClock", K.Choice, Strings.Settings.Appearance.DaylistClock,
                SettingsWriter.OfChoice("lists.daylistClock", SettingsVerbs.SetDaylistClock), legacy: ["daylistClock"], glyph: "Clock",
                captionKey: Strings.Settings.Appearance.DaylistClockSub, value: _ => (int)Prefs.Appearance.DaylistClock(),
                contexts: [SettingsSurfaces.HomeDaylist],   // the daylist tile's options draw it (HomeTileDials)
                choices: [Strings.Settings.Appearance.DaylistClockBoth, Strings.Settings.Appearance.DaylistClockUpdateTime,
                    Strings.Settings.Appearance.DaylistClockCountdown]),
            // The Liked Songs cover: the same writer as the cover's "Cover style" flyout; its value is the slot in the picker's order.
            Make("appearance.likedCover", K.Picker, Strings.Detail.LikedCover.Title,
                SettingsWriter.OfChoice("liked.coverStyle", SettingsVerbs.SetLikedCover), glyph: "Album",
                captionKey: Strings.Detail.LikedCover.Note,
                value: _ => LikedCoverRules.PickerIndexOf(LikedCoverRules.FromSetting(Prefs.Appearance.LikedCover(LikedCoverRules.StyleCount))),
                contexts: [SettingsSurfaces.LikedCover], choices: Array.ConvertAll(LikedCoverRules.PickerOrder, LikedCoverRules.NameKey)),
            // The two opt-in list columns: the same writers and the same keys as a track list's ⋯ menu (the active structure's scope
            // included, so Settings and the list never disagree).
            Make("appearance.tempoColumn", K.Toggle, Strings.Detail.TempoColumn,
                SettingsWriter.OfToggle("lists.tempoColumn", SettingsVerbs.SetTempoColumn), glyph: "List",
                value: s => s.Get(TableColumnsPref.TempoKey(ShellPolicy.Now.Lists)), contexts: [SettingsSurfaces.TrackListMenu], aliases: ["bpm"]),
            Make("appearance.playsColumn", K.Toggle, Strings.Detail.PlaysColumn,
                SettingsWriter.OfToggle("lists.playsColumn", SettingsVerbs.SetPlaysColumn), glyph: "ViewList",
                value: BoolOf(Platform.Keys.PlaysColumn), contexts: [SettingsSurfaces.TrackListMenu])),
    ]);

    /// <summary>The sidebar's layout verbs refuse while it is being edited, and say so (Q7).</summary>
    static SettingsEnablement NotWhileEditing(ISettingsFacts f)
        => f.SidebarEditing ? SettingsEnablement.Off(FinishEditingKey) : SettingsEnablement.On;

    /// <summary>The options group's live dials: the active theme's generated options and its "Reset options". A reference to a core
    /// preference is that core dial itself, which its own group lists, so it is not listed twice.</summary>
    static IReadOnlyList<SettingsDial> ThemeOptionDials()
    {
        ThemeOptionsModel model = ThemeOptionsSection.Current(Platform.Developer.Enabled.Peek());
        var dials = new List<SettingsDial>(model.Rows.Count + 1);
        foreach (GeneratedOptionRow row in model.Rows)
            if (row.Pref is null) dials.Add(row.Row);
        if (model.Reset is { } reset) dials.Add(reset);
        return dials;
    }
}

public static partial class SettingsVerbs
{
    // ── Theme ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The theme picker's pick: switch to the card at <paramref name="index"/> of the cards the picker shows. The picked
    /// theme's root mounts fresh (owner Q26), so the page never needs a re-read; picking the selected card, or an index no card
    /// has (the list changed under a stale view), does nothing.</summary>
    public static bool PickTheme(int index)
    {
        var entries = ThemePickerFacts.State.Peek().Entries(Platform.Developer.Enabled.Peek());
        if ((uint)index >= (uint)entries.Count || entries[index].Selected) return false;
        _ = ThemeHost.Switch(entries[index].Id);
        return false;
    }

    /// <summary>The index of the selected card among the cards the picker shows; -1 before a theme is installed.</summary>
    public static int ThemeIndex()
    {
        var entries = ThemePickerFacts.State.Peek().Entries(Platform.Developer.Enabled.Peek());
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Selected) return i;
        return -1;
    }

    /// <summary>0 System · 1 Light · 2 Dark: <see cref="Appearance.SetMode"/> swaps the installed Wavee palette IN PLACE (System
    /// re-reads the OS theme and accent now), persists, and asks the host for the 250 ms re-theme cross-fade (ch 27 §5).</summary>
    public static bool SetThemeMode(int mode)
    {
        Appearance.SetMode((ThemeMode)Math.Clamp(mode, 0, 2));
        return true;
    }

    // ── Colours (#184) ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The colour set pick of <paramref name="kind"/>: index 0 the theme's own colours, index i the catalog's (i - 1)th set for that mode
    /// (<see cref="ColourCatalog.For"/>). <see cref="ColourChoice.SetSpec"/> persists and applies it; an index no set has does nothing.</summary>
    public static bool PickColours(ThemeKind kind, int index)
    {
        var specs = ColourCatalog.For(kind);
        if (index < 0 || index > specs.Length + 1) return false;
        return ColourChoice.SetSpec(kind, index == 0 ? null : index == specs.Length + 1 ? CustomColours.IdOf(kind) : specs[index - 1].Id);
    }

    /// <summary>The index of <paramref name="kind"/>'s stored set in <see cref="PickColours"/>' order (0: the theme's own colours).</summary>
    public static int ColoursIndex(ThemeKind kind)
    {
        if (ColourChoice.IsCustom(kind)) return ColourCatalog.For(kind).Length + 1;
        if (ColourChoice.SpecOf(kind) is not { } spec) return 0;
        return ColourCatalog.For(kind).IndexOf(spec) + 1;
    }

    /// <summary>Where the accent comes from while a set is active: 0 the theme's, 1 the palette's, 2 my own (<see cref="AccentSource"/>). My own,
    /// picked before any was chosen, starts as the set's accent, so the pick changes nothing until it is chosen.</summary>
    public static bool SetColourAccent(int index)
    {
        if (index is < 0 or > 2) return false;
        if (index == 2 && ColourChoice.OwnAccent is null && ColourChoice.SpecOf(Tok.Theme) is { } spec) ColourChoice.SetOwnAccent(spec.Seeds.Accent);
        ColourChoice.SetAccentSource((AccentSource)index);
        return true;
    }

    /// <summary>Opens the editor of <paramref name="kind"/>'s own set. The page re-reads after Apply through the colour epoch, not here.</summary>
    public static bool EditCustomColours(ThemeKind kind)
    {
        ColourEditor.Open(SettingsContext.Overlay, kind);
        return false;
    }

    /// <summary>Opens the editor of my own accent.</summary>
    public static bool ChooseOwnAccent()
    {
        ColourEditor.OpenOwnAccent(SettingsContext.Overlay);
        return false;
    }

    /// <summary>"Artwork colours" while a set is active (plan D4).</summary>
    public static bool SetColourArtwork(bool on)
    {
        ColourChoice.SetArtwork(on);
        return true;
    }

    /// <summary>The set on screen (the current mode's), or null.</summary>
    static ColourSpec? SpecOnScreen() => ColourChoice.SpecOf(Tok.Theme);

    /// <summary>True when the palette accent row shows: the accent comes from the palette and the set on screen names several.</summary>
    public static bool NamedAccentsShown()
        => ColourChoice.AccentSource == AccentSource.Palette && SpecOnScreen() is { Accents.IsDefaultOrEmpty: false };

    /// <summary>The names of the on-screen set's accents (the family's own words, not localized).</summary>
    public static IReadOnlyList<string> NamedAccentChoices()
    {
        if (SpecOnScreen() is not { } spec || spec.Accents.IsDefaultOrEmpty) return [];
        var words = new string[spec.Accents.Length];
        for (int i = 0; i < words.Length; i++) words[i] = spec.Accents[i].Name;
        return words;
    }

    /// <summary>The index of the on-screen set's accent (its stored pick, else its default).</summary>
    public static int NamedAccentIndex()
    {
        if (SpecOnScreen() is not { } spec || spec.Accents.IsDefaultOrEmpty) return -1;
        string id = ColourChoice.NamedAccentOf(Tok.Theme) is { Length: > 0 } picked ? picked : spec.DefaultAccent ?? "";
        for (int i = 0; i < spec.Accents.Length; i++)
            if (string.Equals(spec.Accents[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Picks the on-screen set's accent at <paramref name="index"/> for the mode on screen.</summary>
    public static bool PickNamedAccent(int index)
    {
        if (SpecOnScreen() is not { } spec || (uint)index >= (uint)spec.Accents.Length) return false;
        ColourChoice.SetNamedAccent(Tok.Theme, spec.Accents[index].Id);
        return true;
    }

    /// <summary>The words of <paramref name="kind"/>'s choices: "Theme default", then each set's family and name (proper nouns, not localized).</summary>
    public static IReadOnlyList<string> ColoursChoices(ThemeKind kind)
    {
        var specs = ColourCatalog.For(kind);
        var words = new string[specs.Length + 2];
        words[0] = Loc.Get(Strings.Settings.Appearance.ColoursDefault);
        for (int i = 0; i < specs.Length; i++) words[i + 1] = specs[i].DisplayName;
        words[^1] = Loc.Get(Strings.Settings.Appearance.ColoursCustom);
        return words;
    }

    /// <summary>The Zoom combo's pick: index 0 is "Auto (N%)" (mode = Auto, never Dense, applying the suggestion the row last
    /// showed — <see cref="SettingsFacts.AutoZoom"/>); a rung writes Manual. A deliberate pick writes at once.</summary>
    public static bool PickZoom(int index)
    {
        if (index <= 0)
        {
            ZoomService.PickAuto(SettingsFacts.AutoZoom);
            return true;
        }
        int step = index - 1;
        if ((uint)step >= (uint)FluentGpu.Foundation.ZoomLadder.Steps.Length) return false;
        ZoomService.PickManual(FluentGpu.Foundation.ZoomLadder.Steps[step]);
        return true;
    }

    /// <summary>The Zoom combo's selection: the rung that matches the live zoom in Manual, else the head item (Auto and Dense).</summary>
    public static int ZoomIndex()
        => ZoomService.Mode == ZoomAutoMode.Manual
            ? 1 + Math.Max(0, Array.IndexOf(FluentGpu.Foundation.ZoomLadder.Steps, FluentGpu.Foundation.ZoomLadder.Snap(FluentGpu.FluentApp.Zoom)))
            : 0;

    /// <summary>An appearance switch: persist and bump the appearance epoch once; the page re-reads.</summary>
    public static bool SetAppearanceToggle(SettingKey<bool> key, bool on)
    {
        Platform.Settings.Set(key, on);
        Prefs.Appearance.Bump();
        return true;
    }

    /// <summary>The daylist clock (#185): the index IS the <see cref="DaylistClockMode"/> int. The write goes through the ONE
    /// appearance writer, so the playlist hero strip and the Home card swap their clock live; the page does not re-read.</summary>
    public static bool SetDaylistClock(int i)
    {
        if ((uint)i >= DaylistClockShows.ModeCount || Platform.Settings.Get(Platform.Keys.DaylistClock) == i) return false;
        Prefs.Appearance.Set(Platform.Keys.DaylistClock, i);
        return false;
    }

    /// <summary>The Liked Songs cover style at slot <paramref name="i"/> of the picker's order: the cover flyout's own writer (E15),
    /// persisted through the appearance writer, so the cover behind the flyout repaints on the bump. Idempotent and cheap, which it
    /// MUST be: the flyout's strip fires on every keyboard rove (selection follows focus), and roving IS the preview.</summary>
    public static bool SetLikedCover(int i)
    {
        var order = LikedCoverRules.PickerOrder;
        if ((uint)i >= (uint)order.Length) return false;
        Prefs.Appearance.Set(Platform.Keys.LikedCoverStyle, LikedCoverRules.ToSetting(order[i]));
        return true;
    }

    /// <summary>The BPM · Key column: the track list menu's writer (persist, one appearance bump); the page re-reads.</summary>
    public static bool SetTempoColumn(bool on)
    {
        TableColumnsPref.SetTempo(on);
        return true;
    }

    /// <summary>The Plays column: the track list menu's writer (persist, one appearance bump); the page re-reads.</summary>
    public static bool SetPlaysColumn(bool on)
    {
        TableColumnsPref.SetPlays(on);
        return true;
    }

    // ── Sidebar ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reset the current layout to its authored form, with the sidebar's own undo toast.</summary>
    public static bool ResetSidebarLayout()
    {
        Sidebar.ResetLayoutRecorded(Sidebar.Layout.Peek(),
            FluentGpu.Localization.Loc.Format("sidebar.toast.reset", ("name", Sidebar.LayoutName(Sidebar.Layout.Peek()))));
        return false;
    }

    /// <summary>"Edit sidebar…" (P4).</summary>
    public static bool EditSidebar()
    {
        Sidebar.EnterEdit();
        return false;
    }

    /// <summary>"Reset everything": the sidebar's own confirm, opened over the Settings page's overlay host.</summary>
    public static bool ResetSidebarEverything()
    {
        if (SettingsContext.Overlay is { } overlay) Sidebar.SidebarMenus.ConfirmResetEverything(overlay);
        return false;
    }
}
