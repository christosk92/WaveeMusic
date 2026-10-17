// ── Wavee.Tests/Theming/ThemingFixtures.cs — fake layers and compositions for the theming core tests ───────────────────
//
// The core must work for ANY structure and style, so its tests run against fakes that declare exactly the options, views
// and effects a fact needs. Nothing here names a real theme. `Themes` is process state: every test class that installs
// compositions lives in the platform collection (serial) and resets it on both sides.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Roles;

namespace Wavee.Tests.Theming;

enum Density : byte { Compact = 0, Normal = 1, Roomy = 2 }

sealed record TestProps(string Title);

static class TestKeys
{
    public static readonly ViewKey<TestProps> View = new("Test.View");
}

/// <summary>A structure with the options, views, policies and capabilities the test sets.</summary>
sealed class FakeStructure(string id, ThemeOptionSchema? options = null) : IStructureLayer
{
    public string Id => id;
    public ThemeOptionSchema Options { get; set; } = options ?? ThemeOptionSchema.Empty;
    public bool RegistersView { get; set; } = true;
    public int RegisterCalls { get; private set; }
    public Func<OptionValues, ShellCapabilities> CapabilityFn { get; set; } = static _ => ShellCapabilities.Unrestricted;
    public Func<OptionValues, StyleMetrics, ThemePolicies> PolicyFn { get; set; } = static (_, _) => ThemePolicies.Default;
    public RoleSet RequiredRoles => RoleSet.None;

    /// <summary>The rows this structure still binds to legacy keys (the options page lists them next to the generated ones).</summary>
    public IReadOnlyList<LegacyOptionRow> Legacy { get; set; } = Array.Empty<LegacyOptionRow>();
    public IReadOnlyList<LegacyOptionRow> LegacyRows => Legacy;

    public void Register(ViewRegistryBuilder views)
    {
        RegisterCalls++;
        if (RegistersView)
            views.Add(TestKeys.View, static (TestProps p, in ViewContext ctx) => new BoxEl(), "Test.View.Template");
    }

    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => PolicyFn(structure, metrics);
    public ShellCapabilities Capabilities(OptionValues structure) => CapabilityFn(structure);
}

/// <summary>A style whose definition is either the live <c>Tok.Definition</c> instance (so applying it never touches the
/// process-wide token state) or its own copy (for tests that read the definition without applying it).</summary>
sealed class FakeStyle(string id, ThemeOptionSchema? options = null, bool live = true) : IStyleLayer
{
    private readonly ThemeDefinition _own = Tok.Definition with { Id = id + ".definition", AppRoles = null };
    private ThemeDefinition? _withRoles;

    public string Id => id;
    public ThemeOptionSchema Options { get; set; } = options ?? ThemeOptionSchema.Empty;
    public Func<OptionValues, StyleMetrics> MetricsFn { get; set; } = static _ => StyleMetrics.Unset;
    public int DefinitionCalls { get; private set; }

    /// <summary>The roles the style's own definition carries (a style that is not <c>live</c>); null = none. Set before the first
    /// <see cref="Definition"/> call: one definition instance is returned per roles instance.</summary>
    public WaveeStyleRoles? Roles { get; set; }

    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent)
    {
        DefinitionCalls++;
        if (live) return Tok.Definition;
        if (Roles is null) return _own;
        return _withRoles is { } built && ReferenceEquals(built.AppRoles, Roles) ? built : _withRoles = _own with { AppRoles = Roles };
    }

    public StyleMetrics Metrics(OptionValues style) => MetricsFn(style);
}

static class Fakes
{
    public static ThemeManifest Manifest(string id, string structureId, string styleId, ThemeAvailability availability = ThemeAvailability.Public)
        => new(id, "theme." + id + ".name", Wavee.Theming.Contracts.ContractVersion.Major, Wavee.Theming.Contracts.ContractVersion.Minor,
            structureId, styleId, null, availability, new ThemePreview("l.png", "d.png"));

    /// <summary>A composition of a fresh structure and style named after <paramref name="id"/>.</summary>
    public static ThemeComposition Theme(string id, FakeStructure? structure = null, FakeStyle? style = null,
        ThemeAvailability availability = ThemeAvailability.Public,
        IReadOnlyList<CompositionOption>? compositionOptions = null, CompositionEffect? fixedEffect = null,
        IReadOnlySet<string>? hidden = null, IReadOnlyDictionary<string, object>? overrides = null,
        IReadOnlyDictionary<string, string>? replaced = null)
    {
        structure ??= new FakeStructure(id + ".structure");
        style ??= new FakeStyle(id + ".style");
        return new ThemeComposition(Manifest(id, structure.Id, style.Id, availability), structure, style,
            compositionOptions, fixedEffect, hidden, overrides, replaced);
    }

    public static ToggleOption Toggle(string id, bool def = false)
        => new(id, "t." + id, null, "g", def);

    public static SelectOption<Density> DensityOption(string id = "density", Density def = Density.Normal)
        => new(id, "t." + id, null, "g", def,
            ImmutableArray.Create(new SelectChoice<Density>(Density.Compact, "c"), new SelectChoice<Density>(Density.Normal, "n"), new SelectChoice<Density>(Density.Roomy, "r")),
            PickerStyle.Radio);

    public static SliderOption Slider(string id = "scale", float def = 1f)
        => new(id, "t." + id, null, "g", def, 0.5f, 2f, 0.25f, null);

    public static ColorOption Colour(string id = "tint")
        => new(id, "t." + id, null, "g", ColorF.FromRgba(0x10, 0x80, 0xF0), ImmutableArray<ColorF>.Empty);

    public static ThemeOptionSchema Schema(params ThemeOption[] options) => ThemeOptionSchema.Of(options);

    public static IReadOnlyDictionary<string, object> Dict(params (string Key, object Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);
}
