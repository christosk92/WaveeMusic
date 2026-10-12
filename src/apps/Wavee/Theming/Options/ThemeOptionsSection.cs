// ── Theming/Options/ThemeOptionsSection.cs — the generated "‹Theme› options" section (themes plan 8.1-8.2, T1b) ───────────
//
// Settings › Appearance shows the options of the ACTIVE theme without knowing which theme that is. This file turns the
// composition's exposed schema into `SettingsRow`s (the page's own document grammar: kind, title, caption, value, writer) and
// into the palette's option commands; the views (`Settings.UI.Theme.cs`, `Setup.UI.Theme.cs`) only draw what it returns.
//
//   · EXPOSED OPTIONS ONLY (`ThemeComposition.ExposedOptions`): structure parameters, options the composition hides and
//     options a composition option replaces never get a row. A Developer-visibility option is absent outside Developer mode,
//     an option whose `VisibleWhen` does not hold is absent until it does (its value is kept).
//   · EVERY WRITE IS `ThemeHost.Apply`: the writer builds an `OptionChange` and the host decides live or transaction from the
//     option's apply mode. A value the option refuses (out of range, not a choice) is logged and written nowhere.
//   · A ROW NEVER BREAKS THE PAGE: a select with no choices has no row, a stored value that is no longer valid reads as the
//     default (the store's value rules), a `VisibleWhen` that names no value reads as satisfied.
//   · LEGACY ROWS. A structure whose options are not all options yet lists the rest as `LegacyRows` (SettingsDoc rows bound to
//     today's keys); the model carries them next to the generated rows so the page shows ONE section, in its final grouping.
//
// WVTH004/005/007 allow this folder to read theme identity and options and to build `theme.*` setting names.

using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee.Theming;

/// <summary>One generated row with what a view needs to draw it (the <see cref="SettingsRow"/> alone does not carry a select's
/// choices or a slider's range).</summary>
public sealed class GeneratedOptionRow
{
    /// <summary>The row in the Settings document grammar. For a reference to a core preference this is the CORE row itself
    /// (the same instance <c>SettingsDoc</c> serves), so there is one writer for it.</summary>
    public required SettingsRow Row { get; init; }

    /// <summary>The layer-qualified option id (<c>style.density</c>); the pref's name for a reference row.</summary>
    public required string QualifiedId { get; init; }

    /// <summary>The group the row is shown under: a loc key when one exists (<see cref="ThemeOptionsSection.GroupTitle"/>), else the text.</summary>
    public required string Group { get; init; }

    /// <summary>How a change applies.</summary>
    public ApplyMode Apply { get; init; }

    /// <summary>The option asked for a palette command (<see cref="ThemeOption.Command"/>).</summary>
    public bool Command { get; init; }

    /// <summary>False for an action whose verb no core code registered (the row is greyed and gives no palette command).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The core preference a reference row points at; null for every other row.</summary>
    public CorePref? Pref { get; init; }

    /// <summary>The loc keys of a select's choices, in order; empty for every other row.</summary>
    public IReadOnlyList<string> ChoiceTitleKeys { get; init; } = Array.Empty<string>();

    /// <summary>How a select is drawn (a select Toggle-style with two choices is a <see cref="SettingsRowKind.Toggle"/> row).</summary>
    public PickerStyle Picker { get; init; } = PickerStyle.Radio;

    /// <summary>A colour option (its writer's <c>Choose</c> takes the colour as packed ARGB, <see cref="ThemeOptionsSection.UnpackColor"/>).</summary>
    public bool IsColor { get; init; }

    /// <summary>A colour option's offered swatches; empty means a free colour picker.</summary>
    public IReadOnlyList<ColorF> Swatches { get; init; } = Array.Empty<ColorF>();

    /// <summary>A slider's range, step and the loc key of its unit (null = none).</summary>
    public float Min { get; init; }
    public float Max { get; init; }
    public float Step { get; init; }
    public string? UnitKey { get; init; }

    /// <summary>The value at the time the model was built, as the row's <c>Value</c> answers it: a bool, the index of the
    /// choice, a float, the packed ARGB of a colour; null for an action or a reference.</summary>
    public object? Current { get; init; }

    /// <summary>The same shape as <see cref="Current"/>, read NOW from the active theme's option values: what a control that
    /// outlives a render (a combo box, a slider) re-seeds itself from. Meaningful for the active theme only.</summary>
    public Func<object?> Live { get; init; } = static () => null;
}

/// <summary>The options section of one theme at one moment.</summary>
/// <param name="ThemeId">The theme's id (what <c>ThemeHost.ResetOptions</c> takes).</param>
/// <param name="NameKey">The loc key of the theme's name (the section reads "‹name› options").</param>
/// <param name="Rows">The generated rows in schema order.</param>
/// <param name="Legacy">The rows still bound to legacy keys, in display order.</param>
/// <param name="Reset">"Reset options", or null when the theme has no stored option to forget.</param>
public sealed record ThemeOptionsModel(string ThemeId, string NameKey, IReadOnlyList<GeneratedOptionRow> Rows,
    IReadOnlyList<LegacyOptionRow> Legacy, SettingsRow? Reset)
{
    /// <summary>A theme with nothing to configure (the page shows one quiet row instead of an expander).</summary>
    public bool IsEmpty => Rows.Count == 0 && Legacy.Count == 0;

    /// <summary>No theme installed (before <c>ThemeHost.Install</c>).</summary>
    public static ThemeOptionsModel None { get; } = new("", "", Array.Empty<GeneratedOptionRow>(), Array.Empty<LegacyOptionRow>(), null);
}

/// <summary>One palette row an option contributes (<see cref="ThemeOption.Command"/>).</summary>
/// <param name="Id">Stable id (<c>theme.structure.density.2</c>).</param>
/// <param name="Label">The localised label.</param>
/// <param name="Run">Applies it. Never throws.</param>
public sealed record ThemeCommand(string Id, string Label, Action Run);

/// <summary>The core verbs an <see cref="ActionOption"/> names (<c>VerbId</c>). A core PR that owns the behaviour registers it once at
/// boot (M8: <c>clearRememberedSizes</c>); an action whose verb is not registered shows greyed.</summary>
public static class ThemeVerbs
{
    private static readonly Dictionary<string, Func<bool>> s_verbs = new(StringComparer.Ordinal);

    /// <summary>Registers (or replaces) the verb <paramref name="verbId"/>. It returns whether the page must re-read.</summary>
    public static void Register(string verbId, Func<bool> run)
    {
        ArgumentException.ThrowIfNullOrEmpty(verbId);
        ArgumentNullException.ThrowIfNull(run);
        s_verbs[verbId] = run;
    }

    /// <summary>Forgets a verb (tests).</summary>
    public static void Unregister(string verbId) => s_verbs.Remove(verbId);

    /// <summary>True when <paramref name="verbId"/> has a behaviour.</summary>
    public static bool Has(string verbId) => s_verbs.ContainsKey(verbId);

    /// <summary>Runs the verb; a verb that throws is logged and reads as "nothing to re-read". False for an unknown verb.</summary>
    public static bool Run(string verbId)
    {
        if (!s_verbs.TryGetValue(verbId, out var run)) return false;
        try { return run(); }
        catch (Exception ex)
        {
            Log.Warn("theme", "theme verb '" + verbId + "' failed", ex);
            return false;
        }
    }
}

/// <summary>Builds the options section of a composition.</summary>
public static class ThemeOptionsSection
{
    /// <summary>The Appearance-tab section title the generated rows carry (<c>Settings.Catalog</c> lists it; it is data, the view
    /// prints "‹theme› options").</summary>
    public const string SectionTitle = "Theme options";

    private const int SegmentedMax = 4;

    /// <summary>The section of the ACTIVE theme with its live option values. <see cref="ThemeOptionsModel.None"/> before install.</summary>
    public static ThemeOptionsModel Current(bool developerMode)
        => ThemeHost.IsInstalled ? Build(ThemeHost.Current.Peek(), ThemeHost.Values, developerMode) : ThemeOptionsModel.None;

    /// <summary>The section of <paramref name="theme"/> for these option values and Developer-mode state.</summary>
    public static ThemeOptionsModel Build(ThemeComposition theme, ThemeOptionValues values, bool developerMode)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(values);
        var rows = new List<GeneratedOptionRow>();
        bool anyValue = false;       // the theme stores at least one option: Reset has something to be about
        bool anyPersisted = false;   // ...and at least one is stored right now: Reset has something to forget
        foreach (ThemeOption option in theme.ExposedOptions.All)
        {
            if (theme.Find(option.Id) is not { } lo) continue;
            if (option.HoldsValue)
            {
                anyValue = true;
                anyPersisted |= Platform.Settings.Has(ThemeOptionStore.SettingName(theme.Manifest.Id, lo.Layer, option.Id));
            }
            if (option.Visibility == OptionVisibility.Developer && !developerMode) continue;
            if (!ConditionHolds(lo, values)) continue;
            if (RowOf(theme, lo, values) is { } row) rows.Add(row);
        }
        IReadOnlyList<LegacyOptionRow> legacy = theme.Structure.LegacyRows;
        return new ThemeOptionsModel(theme.Manifest.Id, theme.Manifest.DisplayNameKey, rows, legacy,
            anyValue ? ResetRow(theme.Manifest.Id, anyPersisted) : null);
    }

    /// <summary>The group's title: its loc key's text when there is one, else the group as written.</summary>
    public static string GroupTitle(string group) => Localization.Has(group) ? Loc.Get(group) : group;

    /// <summary>The colour behind a packed ARGB value (<see cref="GeneratedOptionRow.Current"/>, a colour row's <c>Choose</c> argument).</summary>
    public static ColorF UnpackColor(int argb) => ColorOption.Unpack(argb);

    /// <summary>The packed ARGB of <paramref name="color"/>.</summary>
    public static int PackColor(ColorF color) => ColorOption.Pack(color);

    // ── the palette ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The palette rows the active theme's options contribute: a toggle gives On and Off, a select one row per choice, an
    /// action one row. Sliders, colours and references give none.</summary>
    public static IReadOnlyList<ThemeCommand> Commands(bool developerMode)
        => ThemeHost.IsInstalled ? Commands(Current(developerMode)) : Array.Empty<ThemeCommand>();

    /// <summary>The palette rows of <paramref name="model"/>'s rows that asked for one.</summary>
    public static IReadOnlyList<ThemeCommand> Commands(ThemeOptionsModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var commands = new List<ThemeCommand>();
        foreach (GeneratedOptionRow row in model.Rows)
        {
            if (!row.Command) continue;
            string title = Loc.Get(row.Row.TitleKey);
            string id = "theme." + row.QualifiedId;
            SettingsWriter writer = row.Row.Writer;
            switch (row.Row.Kind)
            {
                case SettingsRowKind.Toggle when writer.Toggle is { } toggle:
                    commands.Add(new ThemeCommand(id + ".on", Strings.Palette.ThemeOptionOn(title), Safe(id, () => toggle(true))));
                    commands.Add(new ThemeCommand(id + ".off", Strings.Palette.ThemeOptionOff(title), Safe(id, () => toggle(false))));
                    break;
                case SettingsRowKind.Action when writer.Run is { } run:
                    if (row.Enabled) commands.Add(new ThemeCommand(id, title, Safe(id, () => run())));
                    break;
                case SettingsRowKind.Choice or SettingsRowKind.Segmented or SettingsRowKind.Picker when writer.Choose is { } choose && !row.IsColor:
                    for (int i = 0; i < row.ChoiceTitleKeys.Count; i++)
                    {
                        int index = i;
                        string label = Strings.Palette.ThemeOptionChoice(title, Loc.Get(row.ChoiceTitleKeys[i]));
                        commands.Add(new ThemeCommand(id + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture), label,
                            Safe(id, () => choose(index))));
                    }
                    break;
            }
        }
        return commands;
    }

    /// <summary>A palette command never throws into the palette: a failure is logged and the row does nothing.</summary>
    private static Action Safe(string id, Action run) => () =>
    {
        try { run(); }
        catch (Exception ex) { Log.Warn("theme", "palette command '" + id + "' failed", ex); }
    };

    // ── rows ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static GeneratedOptionRow? RowOf(ThemeComposition theme, LayerOption lo, ThemeOptionValues values)
    {
        ThemeOption option = lo.Option;
        string name = ThemeOptionStore.SettingName(theme.Manifest.Id, lo.Layer, option.Id);
        object? fallback = theme.DefaultOf(lo);
        object? live = values.Of(lo.Layer).GetBoxed(option.Id) ?? fallback;
        bool twoWay = option.SelectPicker == PickerStyle.Toggle && option.ChoiceTitleKeys.Count == 2;
        Func<object?> liveNow = () => ThemeHost.IsInstalled ? Shape(option, ThemeHost.Values.Of(lo.Layer).GetBoxed(option.Id) ?? fallback, twoWay) : null;

        switch (option)
        {
            case ToggleOption:
                return Generated(lo, option, Make(lo, SettingsRowKind.Toggle, option,
                    SettingsWriter.OfToggle(Verb(theme, lo), on => Apply(theme, lo, on)),
                    s => option.Read(s, name, fallback) is true), current: Shape(option, live, twoWay), live: liveNow);

            case ColorOption color:
                return Generated(lo, option, Make(lo, SettingsRowKind.Picker, option,
                    SettingsWriter.OfChoice(Verb(theme, lo), argb => Apply(theme, lo, ColorOption.Unpack(argb))),
                    s => Shape(option, option.Read(s, name, fallback), twoWay)),
                    current: Shape(option, live, twoWay), live: liveNow, isColor: true, swatches: SwatchesOf(color));

            case SliderOption slider:
                return Generated(lo, option, Make(lo, SettingsRowKind.Slider, option,
                    SettingsWriter.OfSlider(Verb(theme, lo), v => Apply(theme, lo, v)),
                    s => option.Read(s, name, fallback)),
                    current: Shape(option, live, twoWay), live: liveNow, min: slider.Min, max: slider.Max, step: slider.Step, unitKey: slider.UnitKey);

            case ActionOption action:
            {
                bool known = ThemeVerbs.Has(action.VerbId);
                string verb = action.VerbId;
                SettingsRow row = Make(lo, SettingsRowKind.Action, option,
                    SettingsWriter.OfAction(Verb(theme, lo), () => ThemeVerbs.Run(verb)), NoValue, enabled: known);
                return Generated(lo, option, row, enabled: known);
            }

            case PrefReferenceOption reference:
                return ReferenceRow(lo, reference);
        }

        if (option.SelectPicker is { } picker)
        {
            IReadOnlyList<string> titles = option.ChoiceTitleKeys;
            if (titles.Count == 0) return null;   // nothing to choose from: no row beats a dead control
            if (twoWay)
            {
                return Generated(lo, option, Make(lo, SettingsRowKind.Toggle, option,
                    SettingsWriter.OfToggle(Verb(theme, lo), on => ApplyChoice(theme, lo, on ? 1 : 0)),
                    s => option.ChoiceIndexOf(option.Read(s, name, fallback)) == 1),
                    current: Shape(option, live, twoWay), live: liveNow, choices: titles, picker: picker);
            }
            SettingsRowKind kind = picker switch
            {
                PickerStyle.Cards or PickerStyle.Swatches => SettingsRowKind.Picker,
                PickerStyle.Combo => SettingsRowKind.Choice,
                _ => titles.Count <= SegmentedMax ? SettingsRowKind.Segmented : SettingsRowKind.Choice,
            };
            return Generated(lo, option, Make(lo, kind, option,
                SettingsWriter.OfChoice(Verb(theme, lo), i => ApplyChoice(theme, lo, i)),
                s => option.ChoiceIndexOf(option.Read(s, name, fallback))),
                current: Shape(option, live, twoWay), live: liveNow, choices: titles, picker: picker);
        }
        return null;   // a kind this build cannot draw (a later contract bump adds it with its control)
    }

    /// <summary>A raw option value in the shape a row answers: a toggle's bool, a select's choice index (a two-way select's
    /// "second choice is on"), a slider's float, a colour's packed ARGB.</summary>
    private static object? Shape(ThemeOption option, object? raw, bool twoWay)
    {
        switch (option)
        {
            case ToggleOption: return raw is true;
            case ColorOption: return raw is ColorF c ? (object?)ColorOption.Pack(c) : null;
            case SliderOption: return raw;
        }
        if (option.SelectPicker is null) return null;
        int index = option.ChoiceIndexOf(raw);
        if (twoWay) return index == 1;
        return index;
    }

    private static GeneratedOptionRow Generated(LayerOption lo, ThemeOption option, SettingsRow row, object? current = null,
        IReadOnlyList<string>? choices = null, PickerStyle picker = PickerStyle.Radio, bool isColor = false,
        IReadOnlyList<ColorF>? swatches = null, float min = 0f, float max = 0f, float step = 0f, string? unitKey = null,
        bool enabled = true, Func<object?>? live = null)
        => new()
        {
            Row = row, QualifiedId = lo.QualifiedId, Group = option.Group, Apply = option.Apply, Command = option.Command,
            Enabled = enabled, ChoiceTitleKeys = choices ?? Array.Empty<string>(), Picker = picker, IsColor = isColor,
            Swatches = swatches ?? Array.Empty<ColorF>(), Min = min, Max = max, Step = step, UnitKey = unitKey, Current = current,
            Live = live ?? (static () => null),
        };

    private static IReadOnlyList<ColorF> SwatchesOf(ColorOption color)
    {
        if (color.Swatches.IsDefaultOrEmpty) return Array.Empty<ColorF>();
        return color.Swatches;
    }

    private static GeneratedOptionRow ReferenceRow(LayerOption lo, PrefReferenceOption reference)
    {
        SettingsRow row = reference.Pref switch
        {
            CorePref.AppMode => SettingsDoc.Row(Settings.Tab.Appearance, "appMode"),
            CorePref.Zoom => SettingsDoc.Row(Settings.Tab.Appearance, "zoom"),
            CorePref.Marquee => SettingsDoc.Row(Settings.Tab.Appearance, "marquee"),
            CorePref.StageFace => SettingsDoc.Row(Settings.Tab.Appearance, "stageVisualizer"),
            _ => new SettingsRow
            {
                Tab = Settings.Tab.Appearance, Section = SectionTitle, Id = "pref.reducedMotion", Kind = SettingsRowKind.Fact,
                TitleKey = Strings.Settings.ThemeOptions.AnimationEffects, Writer = SettingsWriter.ReadOnly,
                Caption = static _ => Strings.Settings.ThemeOptions.AnimationEffectsSub,
            },
        };
        return new GeneratedOptionRow { Row = row, QualifiedId = lo.QualifiedId, Group = reference.Group, Pref = reference.Pref };
    }

    private static readonly Func<IAppSettings, object?> NoValue = static _ => null;

    private static SettingsRow Make(LayerOption lo, SettingsRowKind kind, ThemeOption option, SettingsWriter writer,
        Func<IAppSettings, object?> value, bool enabled = true)
        => new()
        {
            Tab = Settings.Tab.Appearance, Section = SectionTitle, Id = lo.QualifiedId, Kind = kind, TitleKey = option.TitleKey,
            Caption = option.DescriptionKey is { } caption ? _ => caption : static _ => null,
            Value = value, Writer = writer,
            Enabled = enabled ? static _ => SettingsEnablement.On : static _ => SettingsEnablement.Off(null),
            SilentWhenDisabled = !enabled,
            Visible = option.Visibility == OptionVisibility.Developer ? static f => f.DeveloperMode : static _ => true,
        };

    private static string Verb(ThemeComposition theme, LayerOption lo) => "theme." + theme.Manifest.Id + "." + lo.QualifiedId;

    /// <summary>"Reset options": greyed (silently) while nothing is stored, so a destructive-looking no-op is never offered.</summary>
    private static SettingsRow ResetRow(string themeId, bool enabled)
        => new()
        {
            Tab = Settings.Tab.Appearance, Section = SectionTitle, Id = "theme.reset", Kind = SettingsRowKind.Action,
            TitleKey = Strings.Settings.ThemeOptions.Reset,
            Caption = static _ => Strings.Settings.ThemeOptions.ResetSub,
            Enabled = enabled ? static _ => SettingsEnablement.On : static _ => SettingsEnablement.Off(null),
            SilentWhenDisabled = !enabled,
            Writer = SettingsWriter.OfAction("theme.resetOptions", () =>
            {
                ThemeHost.ResetOptions(themeId);
                return true;
            }),
            Confirm = new SettingsConfirm(Strings.Settings.ThemeOptions.ResetConfirmTitle, Strings.Settings.ThemeOptions.ResetConfirmBody,
                Strings.Settings.ThemeOptions.ResetAction),
        };

    // ── writers ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE write path: <c>ThemeHost.Apply</c> with the layer-qualified id. A value the option refuses, or an option
    /// the theme no longer declares, is logged and changes nothing; the page re-reads either way.</summary>
    private static bool Apply(ThemeComposition theme, LayerOption lo, object value)
    {
        try
        {
            _ = ThemeHost.Apply(new OptionChange(theme.Manifest.Id, lo.QualifiedId, value));
        }
        catch (ArgumentException ex)
        {
            Log.Warn("theme", "option '" + lo.QualifiedId + "' refused a value from Settings", ex);
        }
        return true;
    }

    private static bool ApplyChoice(ThemeComposition theme, LayerOption lo, int index)
        => lo.Option.ChoiceAt(index) is { } value ? Apply(theme, lo, value) : false;

    // ── conditions ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>True when the option's <c>VisibleWhen</c> holds. A condition that names no value of the option's layer cannot be
    /// evaluated, so the row stays (an option is never lost to a typo in a condition).</summary>
    private static bool ConditionHolds(LayerOption lo, ThemeOptionValues values)
    {
        if (lo.Option.VisibleWhen is not { } condition) return true;
        object? actual = values.Of(lo.Layer).GetBoxed(condition.OptionId);
        return actual is null || SameValue(actual, condition.EqualsValue);
    }

    private static bool SameValue(object actual, object expected)
    {
        if (Equals(actual, expected)) return true;
        return TryNumber(actual, out double a) && TryNumber(expected, out double b) && a == b;
    }

    private static bool TryNumber(object value, out double number)
    {
        switch (value)
        {
            case Enum e: number = Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture); return true;
            case int or long or short or byte or float or double:
                number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); return true;
            default: number = 0; return false;
        }
    }
}
