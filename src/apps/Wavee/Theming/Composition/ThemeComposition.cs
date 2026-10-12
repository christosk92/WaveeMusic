// ── Theming/Composition/ThemeComposition.cs — structure x style, and the counted overrides on top (themes plan 5.3) ─────
//
// The ONLY entry points core uses to turn option values into a definition, metrics, policies and capabilities. The order
// is fixed (plan 5.3):
//   (1) style.Metrics(o.Style) and style.Definition(o.Style, osAccent);
//   (2) every composition option's effect, in declaration order;
//   (3) FixedEffect last, so a FixedEffect formula sees the final metrics;
//   (4) structure parameters = the declared defaults, then DefaultOverrides, then the option effects, then FixedEffect.
//
// IDENTITY. When no effect changes the definition (roles or accent), the style's own instance is returned UNCHANGED, so a
// theme that is a plain pairing (Classic) installs exactly the definition its style publishes. When an effect does change
// it, the result carries a composition-scoped `Id` (style definition id + manifest id + option stamps), so two themes that
// share a style never share a memoized definition instance.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Theming;

/// <summary>What a composition option does to the composed layers. Every member is a counted override: a theme lists them,
/// a review reads them.</summary>
public sealed record CompositionEffect(
    Func<WaveeStyleRoles, WaveeStyleRoles>? Roles = null,
    Func<StyleMetrics, StyleMetrics>? Metrics = null,
    Func<AccentRamp, AccentRamp>? Accent = null,
    IReadOnlyDictionary<string, object>? StructureParameters = null);

/// <summary>An option a composition declares itself (not its structure's or its style's) and what it does.</summary>
public sealed record CompositionOption(ThemeOption Option, Func<OptionValues, CompositionEffect> Effect);

/// <summary>An option together with the layer that declares it.</summary>
public readonly record struct LayerOption(OptionLayer Layer, ThemeOption Option)
{
    /// <summary>The layer name as it appears in setting keys and in <see cref="OptionChange.LayerQualifiedId"/>.</summary>
    public string LayerName => Layer switch { OptionLayer.Structure => "structure", OptionLayer.Style => "style", _ => "composition" };

    /// <summary><c>structure.density</c>: the id <see cref="OptionChange"/> and the option store use.</summary>
    public string QualifiedId => LayerName + "." + Option.Id;
}

/// <summary>A theme: a structure and a style under a manifest, with the counted overrides the composition adds.</summary>
public sealed record ThemeComposition(
    ThemeManifest Manifest, IStructureLayer Structure, IStyleLayer Style,
    IReadOnlyList<CompositionOption>? CompositionOptions = null,
    CompositionEffect? FixedEffect = null,
    IReadOnlySet<string>? HiddenOptions = null,
    IReadOnlyDictionary<string, object>? DefaultOverrides = null,
    IReadOnlyDictionary<string, string>? ReplacedOptions = null)
{
    // The record's value equality must not see lazily built state, so the state lives in a side table keyed by identity.
    private static readonly ConditionalWeakTable<ThemeComposition, Runtime> s_runtime = new();

    private sealed class Runtime
    {
        public ViewRegistry? Registry;
        public ImmutableArray<LayerOption> All;
        public ThemeOptionSchema? Exposed;
        public string? MemoSignature;
        public AccentRamp MemoAccent;
        public ThemeDefinition? MemoDefinition;
    }

    private Runtime State => s_runtime.GetValue(this, static _ => new Runtime());

    // The synthesized ToString would print the computed properties below (and build the registry doing it): print the id.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Id = ").Append(Manifest.Id);
        return true;
    }

    /// <summary>The views and style tables of the structure, built once on first use (<c>ThemeHost.Install</c> forces it).</summary>
    public ViewRegistry Registry
    {
        get
        {
            var rt = State;
            if (rt.Registry is { } built) return built;
            var builder = new ViewRegistryBuilder();
            Structure.Register(builder);
            return rt.Registry = builder.Build();
        }
    }

    /// <summary>Every option of the three layers with its layer: structure, then style, then composition.</summary>
    public ImmutableArray<LayerOption> AllOptions
    {
        get
        {
            var rt = State;
            if (!rt.All.IsDefault) return rt.All;
            var all = ImmutableArray.CreateBuilder<LayerOption>();
            foreach (var o in Structure.Options.All) all.Add(new LayerOption(OptionLayer.Structure, o));
            foreach (var o in Style.Options.All) all.Add(new LayerOption(OptionLayer.Style, o));
            if (CompositionOptions is { } co)
                foreach (var c in co) all.Add(new LayerOption(OptionLayer.Composition, c.Option));
            return rt.All = all.ToImmutable();
        }
    }

    /// <summary>The options Settings shows: structure, style and composition options minus the structure parameters, the
    /// ones <see cref="HiddenOptions"/> hides and the ones a composition option replaces.</summary>
    public ThemeOptionSchema ExposedOptions
    {
        get
        {
            var rt = State;
            if (rt.Exposed is { } cached) return cached;
            var exposed = ImmutableArray.CreateBuilder<ThemeOption>();
            foreach (var lo in AllOptions)
                if (IsExposed(lo)) exposed.Add(lo.Option);
            return rt.Exposed = new ThemeOptionSchema(exposed.ToImmutable());
        }
    }

    /// <summary>The option with the plain <paramref name="optionId"/> (ids are unique across the layers), or null.</summary>
    public LayerOption? Find(string optionId)
    {
        foreach (var lo in AllOptions)
            if (string.Equals(lo.Option.Id, optionId, StringComparison.Ordinal)) return lo;
        return null;
    }

    /// <summary>The option with the layer-qualified id (<c>style.accent</c>), or null.</summary>
    public LayerOption? FindQualified(string qualifiedId)
    {
        foreach (var lo in AllOptions)
            if (string.Equals(lo.QualifiedId, qualifiedId, StringComparison.Ordinal)) return lo;
        return null;
    }

    /// <summary>True when Settings shows the option: not a structure parameter, not hidden by this composition and not replaced
    /// by a composition option. An option that holds no value (an action, a reference to a core preference) is shown too: it has
    /// a row but nothing is stored for it (<see cref="IsUserOption"/> is about stored values).</summary>
    public bool IsExposed(LayerOption lo)
    {
        if (lo.Option.Visibility == OptionVisibility.Hidden) return false;
        if (HiddenOptions is { } hidden && hidden.Contains(lo.Option.Id)) return false;
        if (ReplacedOptions is { } replaced && replaced.ContainsKey(lo.Option.Id)) return false;
        return true;
    }

    /// <summary>True when the USER can set the option: it holds a value, is not a structure parameter, is not hidden by this
    /// composition and is not replaced by a composition option. Only these are read from and written to settings.</summary>
    public bool IsUserOption(LayerOption lo)
    {
        if (!lo.Option.HoldsValue || lo.Option.Visibility == OptionVisibility.Hidden) return false;
        if (HiddenOptions is { } hidden && hidden.Contains(lo.Option.Id)) return false;
        if (ReplacedOptions is { } replaced && replaced.ContainsKey(lo.Option.Id)) return false;
        return true;
    }

    /// <summary>The default of the option in THIS composition: the override when there is a valid one, else the declared default.</summary>
    public object? DefaultOf(LayerOption lo)
    {
        if (DefaultOverrides is { } overrides && overrides.TryGetValue(lo.Option.Id, out object? o) && lo.Option.TryCoerce(o, out object? coerced))
            return coerced;
        return lo.Option.DefaultBoxed;
    }

    /// <summary>Builds the values of the three layers: <paramref name="readUser"/> for each user option, the composition's
    /// default for every other option that holds a value, then the structure parameters (declared defaults, overrides,
    /// option effects, fixed effect, in that order) laid over the hidden structure options.</summary>
    public ThemeOptionValues Build(Func<LayerOption, object?> readUser)
    {
        ArgumentNullException.ThrowIfNull(readUser);
        var structure = new Dictionary<string, object?>(StringComparer.Ordinal);
        var style = new Dictionary<string, object?>(StringComparer.Ordinal);
        var composition = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var lo in AllOptions)
        {
            if (!lo.Option.HoldsValue) continue;
            object? value = IsUserOption(lo) ? readUser(lo) : DefaultOf(lo);
            var target = lo.Layer == OptionLayer.Structure ? structure : lo.Layer == OptionLayer.Style ? style : composition;
            target[lo.Option.Id] = value;
        }
        var compositionValues = OptionValues.Create(composition);
        OverlayParameters(structure, compositionValues);
        return new ThemeOptionValues(OptionValues.Create(structure), OptionValues.Create(style), compositionValues);
    }

    private void OverlayParameters(Dictionary<string, object?> structure, OptionValues compositionValues)
    {
        if (CompositionOptions is { } options)
        {
            foreach (var co in options)
                OverlayOne(structure, co.Effect(compositionValues).StructureParameters);
        }
        if (FixedEffect is { } fixedEffect)
            OverlayOne(structure, fixedEffect.StructureParameters);
    }

    private void OverlayOne(Dictionary<string, object?> structure, IReadOnlyDictionary<string, object>? parameters)
    {
        if (parameters is null) return;
        foreach (var kv in parameters)
        {
            ThemeOption? option = Structure.Options.Find(kv.Key);
            if (option is null || option.Visibility != OptionVisibility.Hidden)
                throw new InvalidOperationException($"Theme '{Manifest.Id}': '{kv.Key}' is not a structure parameter of {Structure.Id} (only Hidden structure options can be set by a composition).");
            if (!option.TryCoerce(kv.Value, out object? coerced))
                throw new InvalidOperationException($"Theme '{Manifest.Id}': structure parameter '{kv.Key}' cannot take the value '{kv.Value}'.");
            structure[kv.Key] = coerced;
        }
    }

    /// <summary>The definition for these option values and the OS accent (see the file header for the order and the identity rule).</summary>
    public ThemeDefinition Definition(ThemeOptionValues o, AccentRamp osAccent)
    {
        ArgumentNullException.ThrowIfNull(o);
        var rt = State;
        string signature = o.Signature;
        if (rt.MemoDefinition is { } memo && rt.MemoSignature == signature && rt.MemoAccent.Equals(osAccent)) return memo;

        ThemeDefinition def = Style.Definition(o.Style, osAccent);
        WaveeStyleRoles? roles = def.AppRoles as WaveeStyleRoles;
        AccentRamp accent = def.Accent;
        bool changed = false;
        if (CompositionOptions is { } options)
        {
            foreach (var co in options)
                changed |= ApplyColourEffects(co.Effect(o.Composition), def, ref roles, ref accent);
        }
        if (FixedEffect is { } fixedEffect)
            changed |= ApplyColourEffects(fixedEffect, def, ref roles, ref accent);

        if (changed)
            def = def with { Id = def.Id + "|" + Manifest.Id + "|" + signature, AppRoles = roles ?? def.AppRoles, Accent = accent };

        rt.MemoSignature = signature;
        rt.MemoAccent = osAccent;
        rt.MemoDefinition = def;
        return def;
    }

    private bool ApplyColourEffects(CompositionEffect effect, ThemeDefinition def, ref WaveeStyleRoles? roles, ref AccentRamp accent)
    {
        bool changed = false;
        if (effect.Roles is { } mapRoles)
        {
            if (def.AppRoles is not null && def.AppRoles is not WaveeStyleRoles)
                throw new InvalidOperationException($"Theme '{Manifest.Id}': a composition effect maps roles, but the style's AppRoles is a {def.AppRoles.GetType().Name}, not WaveeStyleRoles.");
            WaveeStyleRoles before = roles ?? WaveeStyleRoles.Empty;
            WaveeStyleRoles after = mapRoles(before);
            if (!Equals(before, after) || roles is null)
            {
                roles = after;
                changed = true;
            }
        }
        if (effect.Accent is { } mapAccent)
        {
            AccentRamp after = mapAccent(accent);
            if (!after.Equals(accent))
            {
                accent = after;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>The metrics for these option values: the style's, then each effect's, then the fixed effect's.</summary>
    public StyleMetrics Metrics(ThemeOptionValues o)
    {
        ArgumentNullException.ThrowIfNull(o);
        StyleMetrics metrics = Style.Metrics(o.Style);
        if (CompositionOptions is { } options)
        {
            foreach (var co in options)
                if (co.Effect(o.Composition).Metrics is { } map) metrics = map(metrics);
        }
        if (FixedEffect?.Metrics is { } fixedMap) metrics = fixedMap(metrics);
        return metrics;
    }

    /// <summary>The structure's policies for these option values (given the composed metrics).</summary>
    public ThemePolicies Policies(ThemeOptionValues o)
    {
        ArgumentNullException.ThrowIfNull(o);
        StyleMetrics metrics = Metrics(o);
        return Structure.Policies(o.Structure, in metrics);
    }

    /// <summary>What the shell offers for these option values.</summary>
    public ShellCapabilities Capabilities(ThemeOptionValues o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return Structure.Capabilities(o.Structure);
    }

    /// <summary>The facts core publishes for these option values.</summary>
    public ThemeFacts Facts(ThemeOptionValues o) => new(Policies(o), Capabilities(o), Metrics(o), TierFacts.Unknown);

    /// <summary>Everything wrong with the composition, empty when it can be installed: the manifest, the contract version,
    /// the layer ids, the option ids and every reference a composition makes to an option.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        string id = Manifest.Id ?? "";
        if (!IsValidThemeId(id)) problems.Add($"theme id '{id}' must be lowercase letters, digits and dashes, starting with a letter");
        if (!ContractVersion.IsCompatible(Manifest.ContractMajor, Manifest.ContractMinor))
            problems.Add($"theme '{id}' was built against contract {Manifest.ContractMajor}.{Manifest.ContractMinor}, this core implements {ContractVersion.Text}");
        if (!string.Equals(Manifest.StructureId, Structure.Id, StringComparison.Ordinal))
            problems.Add($"theme '{id}': manifest StructureId '{Manifest.StructureId}' is not the structure's Id '{Structure.Id}'");
        if (!string.Equals(Manifest.StyleId, Style.Id, StringComparison.Ordinal))
            problems.Add($"theme '{id}': manifest StyleId '{Manifest.StyleId}' is not the style's Id '{Style.Id}'");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lo in AllOptions)
        {
            string oid = lo.Option.Id;
            if (string.IsNullOrWhiteSpace(oid) || oid.AsSpan().IndexOfAny(' ', '\t') >= 0)
                problems.Add($"theme '{id}': {lo.LayerName} option id '{oid}' is empty or contains whitespace");
            else if (!seen.Add(oid))
                problems.Add($"theme '{id}': option id '{oid}' is declared more than once across the layers");
            if (lo.Option.Visibility == OptionVisibility.Hidden && lo.Layer != OptionLayer.Structure)
                problems.Add($"theme '{id}': hidden option '{oid}' is a {lo.LayerName} option; only structure options can be structure parameters");
            if (lo.Option.Visibility == OptionVisibility.Hidden && !lo.Option.HoldsValue)
                problems.Add($"theme '{id}': hidden option '{oid}' holds no value");
        }

        if (HiddenOptions is { } hidden)
            foreach (string h in hidden)
                if (Find(h) is null) problems.Add($"theme '{id}': HiddenOptions names '{h}', which no layer declares");

        if (DefaultOverrides is { } overrides)
        {
            foreach (var kv in overrides)
            {
                if (Find(kv.Key) is not { } target) problems.Add($"theme '{id}': DefaultOverrides names '{kv.Key}', which no layer declares");
                else if (!target.Option.TryCoerce(kv.Value, out _)) problems.Add($"theme '{id}': DefaultOverrides value for '{kv.Key}' is not a valid value of that option");
            }
        }

        if (ReplacedOptions is { } replaced)
        {
            foreach (var kv in replaced)
            {
                if (Find(kv.Key) is not { } replacedOption || replacedOption.Layer == OptionLayer.Composition)
                    problems.Add($"theme '{id}': ReplacedOptions key '{kv.Key}' is not a structure or style option");
                if (Find(kv.Value) is not { Layer: OptionLayer.Composition })
                    problems.Add($"theme '{id}': ReplacedOptions value '{kv.Value}' is not a composition option");
            }
        }
        return problems;
    }

    /// <summary>True for lowercase letters, digits and dashes, starting with a letter.</summary>
    public static bool IsValidThemeId(string id)
    {
        if (string.IsNullOrEmpty(id) || id[0] < 'a' || id[0] > 'z') return false;
        foreach (char c in id)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        return true;
    }
}
