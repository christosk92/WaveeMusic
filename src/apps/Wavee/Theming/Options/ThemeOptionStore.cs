// ── Theming/Options/ThemeOptionStore.cs — persistence and cells of one composition's options ───────────────────────────
//
// One instance per ACTIVE composition. It owns the engine's `OptionStore` (the reactive cells views read through
// `ctx.Options`), persists user options under `theme.<themeId>.<layer>.<optionId>` through an `IAppSettings`, and hands
// out snapshots (`ThemeOptionValues`) for the layers' pure functions. The static `Load` / `ResetOptions` are the plan's
// entry points for "read all values of a theme" and "forget them".
//
// WHAT IS PERSISTED. Only USER options (`ThemeComposition.IsUserOption`): structure parameters, options the composition
// hides and options a composition option replaces are never read from settings and never written; they always answer the
// composition's default (parameters: the value the composition's effects compute), so a value stored by an earlier
// composition or a downgrade cannot leak into them.
//
// WVTH007 allows `theme.*` keys to be built here, in ThemeBootstrap and in ThemeHost.cs only.

using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>The option cells and the persistence of one composition.</summary>
public sealed class ThemeOptionStore : IOptionPersistence
{
    private readonly ThemeComposition _theme;
    private readonly IAppSettings _settings;
    private readonly Dictionary<string, LayerOption> _byId = new(StringComparer.Ordinal);
    private readonly ThemeOptionValues _loaded;

    /// <summary>Creates the store for <paramref name="theme"/>, reading the persisted values from <paramref name="settings"/>.</summary>
    public ThemeOptionStore(ThemeComposition theme, IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(settings);
        _theme = theme;
        _settings = settings;
        foreach (var lo in theme.AllOptions) _byId[lo.Option.Id] = lo;
        _loaded = LoadValues(theme, settings);
        Store = new OptionStore(this);
    }

    /// <summary>The engine's cells, backed by this persistence. Provided to the tree as <see cref="OptionStore.Context"/>.</summary>
    public OptionStore Store { get; }

    /// <summary>The composition the store belongs to.</summary>
    public ThemeComposition Theme => _theme;

    /// <summary>The setting name of an option: <c>theme.&lt;themeId&gt;.&lt;layer&gt;.&lt;optionId&gt;</c>.</summary>
    public static string SettingName(string themeId, OptionLayer layer, string optionId)
        => "theme." + themeId + "." + LayerName(layer) + "." + optionId;

    private static string LayerName(OptionLayer layer) => layer switch
    {
        OptionLayer.Structure => "structure",
        OptionLayer.Style => "style",
        _ => "composition",
    };

    private string NameOf(LayerOption lo) => SettingName(_theme.Manifest.Id, lo.Layer, lo.Option.Id);

    // ── IOptionPersistence: called by the engine store when a cell is first read, and on a persisting publish ───────────

    /// <inheritdoc />
    public bool TryLoad(string optionId, out object? raw)
    {
        if (_byId.TryGetValue(optionId, out var lo) && lo.Option.HoldsValue)
        {
            raw = _loaded.Of(lo.Layer).GetBoxed(optionId);
            return raw is not null;
        }
        raw = null;
        return false;
    }

    /// <inheritdoc />
    public void Save(string optionId, object? raw)
    {
        if (_byId.TryGetValue(optionId, out var lo) && _theme.IsUserOption(lo))
            lo.Option.Write(_settings, NameOf(lo), raw);
    }

    // ── snapshots ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The current values of all three layers, read from the cells. Structure parameters are recomputed from the
    /// composition's effects, then written back into their cells (without persisting), so a view that reads a parameter
    /// through <c>ctx.Options</c> sees the same value the structure's functions do.</summary>
    public ThemeOptionValues Snapshot()
    {
        var values = _theme.Build(lo => lo.Option.Peek(Store));
        foreach (var lo in _theme.AllOptions)
        {
            if (!lo.Option.HoldsValue || _theme.IsUserOption(lo)) continue;
            object? value = values.Of(lo.Layer).GetBoxed(lo.Option.Id);
            if (value is not null) lo.Option.Publish(Store, value, persist: false);
        }
        return values;
    }

    /// <summary>Publishes <paramref name="value"/> to the user option <paramref name="lo"/> (persisting it). False when the
    /// value is invalid for the option or unchanged.</summary>
    public bool Publish(LayerOption lo, object? value)
    {
        if (!_theme.IsUserOption(lo)) return false;
        if (!lo.Option.TryCoerce(value, out object? coerced)) return false;
        return lo.Option.Publish(Store, coerced, persist: true);
    }

    // ── static entry points ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The persisted values of <paramref name="theme"/> from <c>Platform.Settings</c>, typed by the schemas, with
    /// the value rules applied and the structure parameters resolved.</summary>
    public static ThemeOptionValues Load(ThemeComposition theme) => Load(theme, Platform.Settings);

    /// <summary>As <see cref="Load(ThemeComposition)"/> over <paramref name="settings"/>.</summary>
    public static ThemeOptionValues Load(ThemeComposition theme, IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(settings);
        return LoadValues(theme, settings);
    }

    private static ThemeOptionValues LoadValues(ThemeComposition theme, IAppSettings settings)
        => theme.Build(lo => lo.Option.Read(settings, SettingName(theme.Manifest.Id, lo.Layer, lo.Option.Id), theme.DefaultOf(lo)));

    /// <summary>Forgets every persisted option of the installed theme <paramref name="themeId"/> (its UI state under
    /// <c>theme.&lt;id&gt;.state.*</c> stays). No-op for an id that is not installed.</summary>
    public static void ResetOptions(string themeId)
    {
        ThemeComposition? theme = ThemeCatalog.Find(themeId);
        if (theme is not null) ResetOptions(theme, Platform.Settings);
    }

    /// <summary>Forgets every persisted option of <paramref name="theme"/> in <paramref name="settings"/>: the legacy keys
    /// and every other theme's keys stay.</summary>
    public static void ResetOptions(ThemeComposition theme, IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var lo in theme.AllOptions)
        {
            if (!lo.Option.HoldsValue) continue;
            settings.Remove(SettingName(theme.Manifest.Id, lo.Layer, lo.Option.Id));
        }
    }
}
