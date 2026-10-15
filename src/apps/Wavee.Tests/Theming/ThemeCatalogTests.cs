// ── Wavee.Tests/Theming/ThemeCatalogTests.cs — installing, resolving, switching and composing themes (T1) ────────────
//
// Against FAKE layers (ThemingFixtures.cs): the core must behave for any structure and style. The one real theme tested
// here is Classic, for what this file is about: it installs and publishes `Design.Definition` carrying its roles (T2a); its registry
// is `RegistryCompletenessTests`. `Themes` is process state, so the class is in the platform collection and resets it around every fact.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using Wavee.Theming.Roles;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ThemeCatalogTests : IDisposable
{
    readonly MemoryAppSettings _settings = new();

    public ThemeCatalogTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    static ThemeComposition Plain(string id, ThemeAvailability availability = ThemeAvailability.Public)
        => Fakes.Theme(id, availability: availability);

    // ── install and resolve ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BeforeInstall_NothingIsActive()
    {
        Assert.False(ThemeHost.IsInstalled);
        Assert.Throws<InvalidOperationException>(() => ThemeHost.Current);
        Assert.Throws<InvalidOperationException>(() => ThemeCatalog.Resolve("classic"));
        Assert.Empty(ThemeCatalog.Installed);
        Assert.Same(ViewRegistry.Empty, ThemeHost.Registry);
    }

    [Fact]
    public void Install_ActivatesTheStoredTheme_AndClassicByDefault()
    {
        ThemeHost.Install(Plain("zune"), Plain("classic"));
        Assert.Equal("classic", ThemeHost.Current.Peek().Manifest.Id);

        _settings.Set(new SettingKey<string>("theme.id", "classic"), "zune");
        ThemeHost.Install(Plain("zune"), Plain("classic"));
        Assert.Equal("zune", ThemeHost.Current.Peek().Manifest.Id);
    }

    [Fact]
    public void Install_StoredIdOfAnUnknownTheme_FallsBackToClassic_AndKeepsTheStoredId()
    {
        _settings.Set(new SettingKey<string>("theme.id", "classic"), "gone");
        ThemeHost.Install(Plain("classic"), Plain("zune"));
        Assert.Equal("classic", ThemeHost.Current.Peek().Manifest.Id);
        Assert.Equal("gone", _settings.Get(new SettingKey<string>("theme.id", "")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public void Resolve_UnknownOrEmptyId_IsTheFallback(string? id)
    {
        ThemeHost.Install(Plain("zune"), Plain("classic"));
        Assert.Equal("classic", ThemeCatalog.Resolve(id).Manifest.Id);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"));
        Assert.Equal("zune", ThemeCatalog.Resolve("ZUNE").Manifest.Id);
    }

    [Fact]
    public void Resolve_DeveloperTheme_NeedsDeveloperMode()
    {
        ThemeHost.Install(Plain("classic"), Plain("lab", ThemeAvailability.Developer));
        Assert.Equal("classic", ThemeCatalog.Resolve("lab", developerMode: false).Manifest.Id);
        Assert.Equal("lab", ThemeCatalog.Resolve("lab", developerMode: true).Manifest.Id);
    }

    [Fact]
    public void Resolve_WithoutAClassic_FallsBackToTheFirstPublicTheme()
    {
        ThemeHost.Install(Plain("lab", ThemeAvailability.Developer), Plain("zune"));
        Assert.Equal("zune", ThemeCatalog.Resolve("missing", developerMode: false).Manifest.Id);
    }

    [Fact]
    public void Installed_ListsTheManifestsInInstallOrder()
    {
        ThemeHost.Install(Plain("zune"), Plain("classic"));
        Assert.Equal(new[] { "zune", "classic" }, ThemeCatalog.Installed.Select(m => m.Id));
    }

    [Fact]
    public void Install_Twice_ReplacesTheInstalledSet()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"));
        ThemeHost.Install(Plain("classic"));
        Assert.Single(ThemeCatalog.Installed);
    }

    [Fact]
    public void Install_RefusesNothingToInstall_AndDuplicateIds()
    {
        Assert.Throws<ArgumentException>(() => ThemeHost.Install());
        Assert.Throws<ArgumentException>(() => ThemeHost.Install(Plain("classic"), Plain("Classic")));
        Assert.False(ThemeHost.IsInstalled);
    }

    [Fact]
    public void Install_BuildsTheRegistryOnce()
    {
        var theme = Plain("classic");
        ThemeHost.Install(theme);
        Assert.True(theme.Registry.Contains("Test.View"));
        Assert.Same(theme.Registry, ThemeHost.Registry);
        Assert.Same(theme.Registry, theme.Registry);
    }

    // ── validation ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static string Problems(ThemeComposition theme) => string.Join(" | ", theme.Validate());

    [Fact]
    public void Validate_AcceptsAWellFormedComposition()
        => Assert.Empty(Fakes.Theme("classic", style: new FakeStyle("s", Fakes.Schema(Fakes.Toggle("dense")))).Validate());

    [Theory]
    [InlineData("Classic")]
    [InlineData("1st")]
    [InlineData("has space")]
    [InlineData("")]
    public void Validate_RejectsAnIdThatCannotBePartOfASettingKey(string id)
        => Assert.Contains("theme id", Problems(Fakes.Theme(id)));

    [Fact]
    public void Validate_RejectsAnotherContractMajor_AndANewerMinor()
    {
        var structure = new FakeStructure("s");
        var style = new FakeStyle("t");
        var manifest = Fakes.Manifest("x", "s", "t");
        Assert.Contains("contract", Problems(new ThemeComposition(manifest with { ContractMajor = ContractVersion.Major + 1 }, structure, style)));
        Assert.Contains("contract", Problems(new ThemeComposition(manifest with { ContractMinor = ContractVersion.Minor + 1 }, structure, style)));
    }

    [Fact]
    public void Validate_RejectsALayerIdThatDiffersFromTheManifest()
    {
        var theme = new ThemeComposition(Fakes.Manifest("x", "other.structure", "t"), new FakeStructure("s"), new FakeStyle("t"));
        Assert.Contains("StructureId", Problems(theme));
    }

    [Fact]
    public void Validate_RejectsAnOptionIdDeclaredTwiceAcrossLayers()
    {
        var theme = Fakes.Theme("x", new FakeStructure("x.structure", Fakes.Schema(Fakes.Toggle("dense"))),
            new FakeStyle("x.style", Fakes.Schema(Fakes.Toggle("dense"))));
        Assert.Contains("more than once", Problems(theme));
    }

    [Fact]
    public void Validate_RejectsADanglingHiddenReplacedOrOverriddenOption()
    {
        var theme = Fakes.Theme("x", hidden: new HashSet<string> { "ghost" },
            overrides: Fakes.Dict(("phantom", true)), replaced: new Dictionary<string, string> { ["a"] = "b" });
        string problems = Problems(theme);
        Assert.Contains("HiddenOptions", problems);
        Assert.Contains("DefaultOverrides", problems);
        Assert.Contains("ReplacedOptions", problems);
    }

    [Fact]
    public void Validate_RejectsAHiddenOptionOutsideTheStructure()
    {
        var hiddenStyleOption = Fakes.Toggle("narrow") with { Visibility = OptionVisibility.Hidden };
        var theme = Fakes.Theme("x", style: new FakeStyle("x.style", Fakes.Schema(hiddenStyleOption)));
        Assert.Contains("only structure options", Problems(theme));
    }

    [Fact]
    public void Install_ReportsEveryProblem()
    {
        var ex = Assert.Throws<ArgumentException>(() => ThemeHost.Install(Fakes.Theme("Bad Id")));
        Assert.Contains("theme id", ex.Message);
        Assert.False(ThemeHost.IsInstalled);
    }

    // ── switching ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switch_PersistsTheId_PublishesTheNewTheme_AndRemountsTheRoot()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"));
        var epoch = ThemeHost.RootEpoch.Peek();
        var picker = ThemePickerFacts.State.Peek();
        Assert.Equal("classic", picker.CurrentId);

        await ThemeHost.Switch("zune");

        Assert.Equal("zune", ThemeHost.Current.Peek().Manifest.Id);
        Assert.Equal("zune", _settings.Get(new SettingKey<string>("theme.id", "")));
        Assert.Equal("zune", ThemePickerFacts.State.Peek().CurrentId);
        Assert.Equal(epoch + 1, ThemeHost.RootEpoch.Peek());
        Assert.Same(ThemeCatalog.Resolve("zune").Registry, ThemeHost.Registry);
    }

    [Fact]
    public async Task Switch_ToTheActiveTheme_IsANoOp()
    {
        ThemeHost.Install(Plain("classic"), Plain("zune"));
        long epoch = ThemeHost.RootEpoch.Peek();
        await ThemeHost.Switch("classic");
        await ThemeHost.Switch("nonsense");   // resolves to the fallback, which is active
        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());
        Assert.False(_settings.WasWritten(new SettingKey<string>("theme.id", "")));
    }

    [Fact]
    public async Task Switch_PublishesTheNewFacts_ButKeepsTheTiers()
    {
        var wide = Fakes.Theme("classic");
        var narrow = Fakes.Theme("zune", new FakeStructure("zune.structure") { CapabilityFn = static _ => ShellCapabilities.Unrestricted with { Tabs = false } });
        ThemeHost.Install(wide, narrow);
        Assert.True(ThemeHost.Facts.Peek().Capabilities.Tabs);

        await ThemeHost.Switch("zune");
        Assert.False(ThemeHost.Facts.Peek().Capabilities.Tabs);
        Assert.Equal(TierFacts.Unknown, ThemeHost.Facts.Peek().Tiers);
    }

    [Fact]
    public void ThemePickerFacts_ListsInstalledAndCurrent_AndHidesDeveloperThemes()
    {
        ThemeHost.Install(Plain("classic"), Plain("lab", ThemeAvailability.Developer));
        var state = ThemePickerFacts.State.Peek();
        Assert.Equal("classic", state.CurrentId);
        Assert.Equal(2, state.Installed.Count);
        Assert.Equal(new[] { "classic" }, state.Visible(developerMode: false).Select(m => m.Id));
        Assert.Equal(2, state.Visible(developerMode: true).Count);
    }

    // ── the composition ──────────────────────────────────────────────────────────────────────────────────────────────────

    static ThemeOptionValues Values(ThemeComposition theme, IAppSettings? settings = null)
        => ThemeOptionStore.Load(theme, settings ?? new MemoryAppSettings());

    [Fact]
    public void Definition_OfAPlainPairing_IsTheStylesOwnInstance()
    {
        var style = new FakeStyle("s", live: false);
        var theme = Fakes.Theme("x", style: style);
        var definition = theme.Definition(Values(theme), AccentRamp.FluentDefault);
        Assert.Equal("s.definition", definition.Id);
        Assert.Same(definition, theme.Definition(Values(theme), AccentRamp.FluentDefault));
    }

    [Fact]
    public void Definition_WithAnAccentEffect_IsScopedToTheCompositionAndMemoized()
    {
        var style = new FakeStyle("s", live: false);
        var warm = AccentRamp.Derive(ColorF.FromRgba(0xE0, 0x60, 0x20));
        var theme = Fakes.Theme("x", style: style, fixedEffect: new CompositionEffect(Accent: _ => warm));
        var values = Values(theme);

        var first = theme.Definition(values, AccentRamp.FluentDefault);
        Assert.Equal(warm, first.Accent);
        Assert.StartsWith("s.definition|x|", first.Id);
        Assert.Same(first, theme.Definition(values, AccentRamp.FluentDefault));
        Assert.Equal(1, style.DefinitionCalls);

        theme.Definition(values, AccentRamp.Derive(ColorF.FromRgba(1, 2, 3)));   // another OS accent is another key
        Assert.Equal(2, style.DefinitionCalls);
    }

    [Fact]
    public void Definition_WithARolesEffect_MapsTheStylesRoles_AndScopesTheId()
    {
        var classic = Wavee.Themes.Classic.ClassicRoles.Table;
        var style = new FakeStyle("s", live: false) { Roles = classic };
        var theme = Fakes.Theme("x", style: style, fixedEffect: new CompositionEffect(
            Roles: r => r with { Space = r.Space with { SectionGap = 48f } }));

        var def = theme.Definition(Values(theme), AccentRamp.FluentDefault);
        var roles = Assert.IsType<WaveeStyleRoles>(def.AppRoles);
        Assert.Equal(48f, roles.Space.SectionGap);
        Assert.Equal(classic.Type, roles.Type);                                  // the rest of the table is the style's
        Assert.Equal(32f, classic.Space.SectionGap);                             // and the style's own table is not touched
        Assert.StartsWith("s.definition|x|", def.Id);
        Assert.Same(def, theme.Definition(Values(theme), AccentRamp.FluentDefault));
    }

    [Fact]
    public void Definition_WithARolesEffectThatChangesNothing_IsTheStylesInstance()
    {
        var style = new FakeStyle("s", live: false) { Roles = Wavee.Themes.Classic.ClassicRoles.Table };
        var theme = Fakes.Theme("x", style: style, fixedEffect: new CompositionEffect(Roles: static r => r with { }));
        var def = theme.Definition(Values(theme), AccentRamp.FluentDefault);
        Assert.Equal("s.definition", def.Id);
        Assert.Same(Wavee.Themes.Classic.ClassicRoles.Table, def.AppRoles);
    }

    [Fact]
    public void Definition_WithARolesEffect_OverAStyleThatPublishesNoRoles_IsRefused()
    {
        var theme = Fakes.Theme("x", style: new FakeStyle("s", live: false),
            fixedEffect: new CompositionEffect(Roles: static r => r with { }));
        var ex = Assert.Throws<InvalidOperationException>(() => theme.Definition(Values(theme), AccentRamp.FluentDefault));
        Assert.Contains("publishes no AppRoles", ex.Message);
    }

    [Fact]
    public void Definition_TwoThemesSharingAStyle_NeverShareAnInstance_WhenTheyChangeIt()
    {
        var style = new FakeStyle("shared", live: false);
        var a = Fakes.Theme("a", style: style, fixedEffect: new CompositionEffect(Accent: r => r with { Base = ColorF.FromRgba(1, 2, 3) }));
        var b = Fakes.Theme("b", style: style, fixedEffect: new CompositionEffect(Accent: r => r with { Base = ColorF.FromRgba(1, 2, 3) }));
        Assert.NotSame(a.Definition(Values(a), AccentRamp.FluentDefault), b.Definition(Values(b), AccentRamp.FluentDefault));
    }

    [Fact]
    public void Metrics_ApplyStyleThenOptionEffectsThenTheFixedEffect()
    {
        var style = new FakeStyle("s") { MetricsFn = static _ => StyleMetrics.Unset with { RowSingle = 40f } };
        var roomy = new CompositionOption(Fakes.Toggle("roomy", true), static v => new CompositionEffect(Metrics: m => m with { RowSingle = m.RowSingle + 8f }));
        var theme = Fakes.Theme("x", style: style, compositionOptions: new[] { roomy },
            fixedEffect: new CompositionEffect(Metrics: m => m with { RowWithArt = m.RowSingle + 12f }));

        var metrics = theme.Metrics(Values(theme));
        Assert.Equal(48f, metrics.RowSingle);
        Assert.Equal(60f, metrics.RowWithArt);   // the fixed effect saw the FINAL RowSingle
    }

    [Fact]
    public void Policies_AreGivenTheComposedMetrics()
    {
        StyleMetrics seen = default;
        var structure = new FakeStructure("s") { PolicyFn = (_, m) => { seen = m; return ThemePolicies.Default; } };
        var theme = Fakes.Theme("x", structure, new FakeStyle("t") { MetricsFn = static _ => StyleMetrics.Unset with { Gutter = 24f } });
        theme.Policies(Values(theme));
        Assert.Equal(24f, seen.Gutter);
    }

    [Fact]
    public void StructureParameters_AreDefaultsThenOverridesThenOptionEffectsThenTheFixedEffect()
    {
        ToggleOption Param(string id) => Fakes.Toggle(id) with { Visibility = OptionVisibility.Hidden };
        var structure = new FakeStructure("s", Fakes.Schema(Param("a"), Param("b"), Param("c"), Param("d")));
        var option = new CompositionOption(Fakes.Toggle("wide"),
            static v => new CompositionEffect(StructureParameters: Fakes.Dict(("b", true), ("c", true))));
        var theme = Fakes.Theme("x", structure, compositionOptions: new[] { option },
            fixedEffect: new CompositionEffect(StructureParameters: Fakes.Dict(("c", false))),
            overrides: Fakes.Dict(("a", true), ("c", false)));

        var structureValues = Values(theme).Structure;
        Assert.True(structureValues.Get(new OptionKey<bool>("a", false)));   // the override replaced the declared default
        Assert.True(structureValues.Get(new OptionKey<bool>("b", false)));   // an option effect set it
        Assert.False(structureValues.Get(new OptionKey<bool>("c", true)));   // the fixed effect has the last word
        Assert.False(structureValues.Get(new OptionKey<bool>("d", true)));   // the declared default
    }

    [Fact]
    public void StructureParameters_NamingANonParameter_IsAProgrammingError()
    {
        var structure = new FakeStructure("s", Fakes.Schema(Fakes.Toggle("visible")));
        var theme = Fakes.Theme("x", structure, fixedEffect: new CompositionEffect(StructureParameters: Fakes.Dict(("visible", true))));
        Assert.Throws<InvalidOperationException>(() => Values(theme));
    }

    [Fact]
    public void ExposedOptions_ExcludeParametersHiddenAndReplacedOptions()
    {
        var parameter = Fakes.Toggle("narrow") with { Visibility = OptionVisibility.Hidden };
        var structure = new FakeStructure("s", Fakes.Schema(parameter, Fakes.Toggle("tabs"), Fakes.Toggle("old")));
        var style = new FakeStyle("t", Fakes.Schema(Fakes.Slider("scale"), Fakes.Toggle("shy")));
        var replacement = new CompositionOption(Fakes.Toggle("new"), static v => new CompositionEffect());
        var theme = Fakes.Theme("x", structure, style, compositionOptions: new[] { replacement },
            hidden: new HashSet<string> { "shy" }, replaced: new Dictionary<string, string> { ["old"] = "new" });

        Assert.Equal(new[] { "tabs", "scale", "new" }, theme.ExposedOptions.All.Select(o => o.Id));
        Assert.Equal(6, theme.AllOptions.Length);
    }

    [Fact]
    public void ThemeComposition_ToString_DoesNotBuildTheRegistry_ButTheRegistryIsBuiltOnce()
    {
        var structure = new FakeStructure("s");
        var theme = Fakes.Theme("x", structure);
        Assert.Contains("x", theme.ToString());
        Assert.Equal(0, structure.RegisterCalls);

        var registry = theme.Registry;
        Assert.Same(registry, theme.Registry);
        Assert.Equal(1, structure.RegisterCalls);
    }

    // ── Classic, as the T-lane PRs grow it ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_InstallsWithItsRegisteredViews_AndTodaysDefinition()
    {
        var classic = Wavee.Themes.Classic.ClassicTheme.Create();
        Assert.Empty(classic.Validate());
        Assert.Equal("classic", classic.Manifest.Id);
        Assert.Equal(ThemeCatalog.FallbackId, classic.Manifest.Id);
        Assert.Equal(ContractVersion.Major, classic.Manifest.ContractMajor);
        // T4a registers the frame and the content states, T6a the player bar; each later T-PR adds its family (RegistryCompletenessTests, T13, pins the full list).
        Assert.True(classic.Registry.Contains(ViewKeys.ShellFrame.Id));
        Assert.True(classic.Registry.Contains(ViewKeys.ShellContentState.Id));
        Assert.True(classic.Registry.Contains(ViewKeys.PlayerBar.Id));
        Assert.Equal(new[] { "sidebar", "libraryDensity", "showCovers", "rowStyle", "hideTrackArt", "surfaces", "pageLayout", "railUniform", "clearRememberedSizes", "density" }, classic.ExposedOptions.All.Select(o => o.Id));   // M7a, CT3a (showCovers), M2, M4, M6 and M8 (structure), M3 (style); each later M-PR adds its own

        var values = Values(classic);
        var definition = classic.Definition(values, AccentRamp.FluentDefault);
        Assert.Same(definition, classic.Definition(values, AccentRamp.FluentDefault));   // one instance for every call
        Assert.Same(Wavee.Themes.Classic.ClassicRoles.Table, definition.AppRoles);
        Assert.Equal(Design.Definition with { AppRoles = Wavee.Themes.Classic.ClassicRoles.Table }, definition);   // today's definition + its roles
        Assert.Equal(ShellCapabilities.Unrestricted, classic.Capabilities(values));
        Assert.NotEqual(StyleMetrics.Unset, classic.Metrics(values));   // Classic publishes its metrics (ClassicPoliciesTests pins them)
        Assert.Same(Wavee.Themes.Classic.ClassicPolicies.Bundle, classic.Policies(values));
    }

    [Fact]
    public void Classic_AsTheOnlyTheme_IsActiveAfterInstall_WithoutTouchingTheTokens()
    {
        var before = Tok.Definition;
        var classic = Wavee.Themes.Classic.ClassicTheme.Create();
        ThemeHost.Install(classic);
        Assert.Equal("classic", ThemeHost.Current.Peek().Manifest.Id);
        Assert.Same(before, Tok.Definition);
        // Classic decides the shell's policies (the baseline, plus its window tiers, T3b) and publishes its own metrics; the
        // capabilities and the tier FACT (no frame has resolved one) are the defaults.
        var facts = ThemeHost.Facts.Peek();
        Assert.Equal(Wavee.Themes.Classic.ClassicPolicies.Bundle, facts.Policies);
        Assert.Same(Wavee.Themes.Classic.ClassicPolicies.Bundle.Tiers, facts.Policies.Tiers);   // ClassicTiers.Policy (internal): the bundle carries it
        Assert.NotSame(TierPolicy.Standard, facts.Policies.Tiers);
        Assert.Equal(ThemeFacts.Default.Capabilities, facts.Capabilities);
        Assert.Equal(ThemeFacts.Default.Tiers, facts.Tiers);
        Assert.NotEqual(ThemeFacts.Default.Metrics, facts.Metrics);
    }

    [Fact]
    public void ContractVersion_IsOnePointFive_AndRefusesAnythingElse()
    {
        // 1.1 (tuned Classic CT4): LyricsState.Offline + LyricsLinesStyle.Offline, PlayerBarSlots.VideoSplit.
        // 1.2 (tuned Classic CT1c): the omnibar model's focused-field facts. 1.3 (tuned Classic CT3a): SidebarRowShape.EntityText,
        // NavPanePolicy.ShowCovers, the row host's UnpinAction.
        // 1.4 (tuned Classic CT5c): the row lanes without a film lane, the versions and withheld-reason reads.
        // 1.5 (tuned Classic CT5b): DetailRailProps.Shown, the two-column arm's insights sheet. A 1.0 theme still runs.
        Assert.Equal("1.5", ContractVersion.Text);
        Assert.True(ContractVersion.IsCompatible(1, 0));
        Assert.True(ContractVersion.IsCompatible(1, 2));
        Assert.True(ContractVersion.IsCompatible(1, 3));
        Assert.True(ContractVersion.IsCompatible(1, 4));
        Assert.True(ContractVersion.IsCompatible(1, 5));
        Assert.False(ContractVersion.IsCompatible(1, 6));
        Assert.False(ContractVersion.IsCompatible(2, 0));
        Assert.False(ContractVersion.IsCompatible(0, 0));
    }
}
