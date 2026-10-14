// ── Wavee.Tests/Theming/ThemeOptionStoreTests.cs — option persistence, the value rules, Apply and ThemeState (T1) ───────
//
// Keys are `theme.<id>.<layer>.<option>`; each kind persists as bool / int / float / int(ARGB); a stored value that is
// invalid for the option reads as the default and is left in place; structure parameters and options a composition hides
// or replaces are never read from or written to settings.

using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ThemeOptionStoreTests : IDisposable
{
    readonly MemoryAppSettings _settings = new();

    public ThemeOptionStoreTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    static SettingKey<T> Key<T>(string name, T def) => new(name, def);

    // A composition with one option of every kind, spread over the three layers.
    static ThemeComposition Rich(Action<FakeStructure>? tweak = null)
    {
        var structure = new FakeStructure("rich.structure", Fakes.Schema(Fakes.DensityOption(), Fakes.Toggle("tabs", true)));
        var style = new FakeStyle("rich.style", Fakes.Schema(Fakes.Slider(), Fakes.Colour()));
        tweak?.Invoke(structure);
        var accent = new CompositionOption(Fakes.Toggle("warm"), static v => new CompositionEffect());
        return Fakes.Theme("rich", structure, style, compositionOptions: new[] { accent });
    }

    // ── names and defaults ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SettingName_IsThemeIdLayerOption()
    {
        Assert.Equal("theme.classic.style.accent", ThemeOptionStore.SettingName("classic", OptionLayer.Style, "accent"));
        Assert.Equal("theme.zune.structure.density", ThemeOptionStore.SettingName("zune", OptionLayer.Structure, "density"));
        Assert.Equal("theme.zune.composition.big", ThemeOptionStore.SettingName("zune", OptionLayer.Composition, "big"));
        Assert.Equal("theme.classic.state.rail", ThemeState.SettingName("classic", "rail"));
    }

    [Fact]
    public void Load_WithNothingStored_GivesTheDeclaredDefaults()
    {
        var values = ThemeOptionStore.Load(Rich(), _settings);
        Assert.Equal(Density.Normal, values.Structure.Get(new OptionKey<Density>("density", Density.Roomy)));
        Assert.True(values.Structure.Get(new OptionKey<bool>("tabs", false)));
        Assert.Equal(1f, values.Style.Get(new OptionKey<float>("scale", 9f)));
        Assert.Equal(ColorF.FromRgba(0x10, 0x80, 0xF0), values.Style.Get(new OptionKey<ColorF>("tint", default)));
        Assert.False(values.Composition.Get(new OptionKey<bool>("warm", true)));
        Assert.Equal(0, _settings.WrittenCount);   // reading writes nothing
    }

    [Fact]
    public void Load_ReadsEachKindFromItsKey()
    {
        _settings.Set(Key("theme.rich.structure.density", 0), (int)Density.Roomy);
        _settings.Set(Key("theme.rich.structure.tabs", true), false);
        _settings.Set(Key("theme.rich.style.scale", 1f), 1.5f);
        _settings.Set(Key("theme.rich.style.tint", 0), unchecked((int)0xFF204060u));
        _settings.Set(Key("theme.rich.composition.warm", false), true);

        var values = ThemeOptionStore.Load(Rich(), _settings);
        Assert.Equal(Density.Roomy, values.Structure.Get(new OptionKey<Density>("density", Density.Normal)));
        Assert.False(values.Structure.Get(new OptionKey<bool>("tabs", true)));
        Assert.Equal(1.5f, values.Style.Get(new OptionKey<float>("scale", 1f)));
        Assert.Equal(ColorF.FromRgba(0x20, 0x40, 0x60), values.Style.Get(new OptionKey<ColorF>("tint", default)));
        Assert.True(values.Composition.Get(new OptionKey<bool>("warm", false)));
    }

    [Theory]
    [InlineData("density", 7)]            // removed / unknown choice
    [InlineData("density", "Roomy")]      // wrong type
    public void InvalidSelectValue_ReadsAsTheDefault_AndStaysStored(string option, object stored)
    {
        _settings.Set(Key("theme.rich.structure." + option, stored), stored);
        var values = ThemeOptionStore.Load(Rich(), _settings);
        Assert.Equal(Density.Normal, values.Structure.Get(new OptionKey<Density>(option, Density.Compact)));
        Assert.True(_settings.Has("theme.rich.structure." + option));
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(0.1f)]
    [InlineData(float.NaN)]
    public void OutOfRangeSlider_ReadsAsTheDefault(float stored)
    {
        _settings.Set(Key("theme.rich.style.scale", 1f), stored);
        Assert.Equal(1f, ThemeOptionStore.Load(Rich(), _settings).Style.Get(new OptionKey<float>("scale", 9f)));
    }

    [Fact]
    public void WrongTypeToggle_ReadsAsTheDefault()
    {
        _settings.Set(Key("theme.rich.structure.tabs", 0), 5);
        Assert.True(ThemeOptionStore.Load(Rich(), _settings).Structure.Get(new OptionKey<bool>("tabs", false)));
    }

    [Fact]
    public void DefaultOverride_IsTheDefaultOfThatComposition_AndAStoredValueStillWins()
    {
        var theme = Fakes.Theme("o", new FakeStructure("o.s", Fakes.Schema(Fakes.Toggle("tabs", true))),
            overrides: Fakes.Dict(("tabs", false)));
        Assert.False(ThemeOptionStore.Load(theme, _settings).Structure.Get(new OptionKey<bool>("tabs", true)));

        _settings.Set(Key("theme.o.structure.tabs", false), true);
        Assert.True(ThemeOptionStore.Load(theme, _settings).Structure.Get(new OptionKey<bool>("tabs", false)));
    }

    [Fact]
    public void ParametersHiddenAndReplacedOptions_IgnoreWhateverIsStored()
    {
        var parameter = Fakes.Toggle("narrow") with { Visibility = OptionVisibility.Hidden };
        var structure = new FakeStructure("s", Fakes.Schema(parameter, Fakes.Toggle("shy"), Fakes.Toggle("old")));
        var big = new CompositionOption(Fakes.Toggle("big"), static v => new CompositionEffect());
        var theme = Fakes.Theme("p", structure, compositionOptions: new[] { big },
            hidden: new HashSet<string> { "shy" }, replaced: new Dictionary<string, string> { ["old"] = "big" });
        foreach (string id in new[] { "narrow", "shy", "old" })
            _settings.Set(Key("theme.p.structure." + id, false), true);

        var values = ThemeOptionStore.Load(theme, _settings).Structure;
        Assert.False(values.Get(new OptionKey<bool>("narrow", true)));
        Assert.False(values.Get(new OptionKey<bool>("shy", true)));
        Assert.False(values.Get(new OptionKey<bool>("old", true)));
    }

    // ── the live store ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Publish_WritesTheKey_AndTheNextLoadReadsIt()
    {
        var theme = Rich();
        var store = new ThemeOptionStore(theme, _settings);
        var density = theme.Find("density")!.Value;

        Assert.True(store.Publish(density, Density.Compact));
        Assert.Equal((int)Density.Compact, _settings.Get(Key("theme.rich.structure.density", -1)));
        Assert.Equal(Density.Compact, store.Snapshot().Structure.Get(new OptionKey<Density>("density", Density.Normal)));
        Assert.Equal(Density.Compact, store.Store.Get(new OptionKey<Density>("density", Density.Normal)));
        Assert.Equal(Density.Compact, ThemeOptionStore.Load(theme, _settings).Structure.Get(new OptionKey<Density>("density", Density.Normal)));
    }

    [Fact]
    public void Publish_RefusesAnInvalidValue_AnUnchangedValue_AndANonUserOption()
    {
        var parameter = Fakes.Toggle("narrow") with { Visibility = OptionVisibility.Hidden };
        var theme = Fakes.Theme("p", new FakeStructure("s", Fakes.Schema(parameter, Fakes.DensityOption())));
        var store = new ThemeOptionStore(theme, _settings);

        Assert.False(store.Publish(theme.Find("density")!.Value, "Roomy"));          // wrong type
        Assert.False(store.Publish(theme.Find("density")!.Value, (Density)9));       // not a choice
        Assert.False(store.Publish(theme.Find("density")!.Value, Density.Normal));   // already the value
        Assert.False(store.Publish(theme.Find("narrow")!.Value, true));              // a structure parameter
        Assert.Equal(0, _settings.WrittenCount);
    }

    [Fact]
    public void Snapshot_SyncsStructureParameterCells_WithTheComposedValue()
    {
        var parameter = Fakes.Toggle("narrow") with { Visibility = OptionVisibility.Hidden };
        var structure = new FakeStructure("s", Fakes.Schema(parameter));
        var wide = new CompositionOption(Fakes.Toggle("tight"),
            static v => new CompositionEffect(StructureParameters: Fakes.Dict(("narrow", v.Get(new OptionKey<bool>("tight", false))))));
        var theme = Fakes.Theme("p", structure, compositionOptions: new[] { wide });
        var store = new ThemeOptionStore(theme, _settings);
        var narrowKey = new OptionKey<bool>("narrow", false);

        Assert.False(store.Store.Get(narrowKey));
        store.Publish(theme.Find("tight")!.Value, true);
        var snapshot = store.Snapshot();

        Assert.True(snapshot.Structure.Get(narrowKey));
        Assert.True(store.Store.Get(narrowKey));                                  // the cell a view reads agrees
        Assert.False(_settings.Has("theme.p.structure.narrow"));                  // and it was never persisted
    }

    [Fact]
    public void ResetOptions_ForgetsOnlyThisThemesOptionKeys()
    {
        var theme = Rich();
        _settings.Set(Key("theme.rich.structure.density", 0), 2);
        _settings.Set(Key("theme.rich.style.scale", 1f), 1.5f);
        _settings.Set(Key("theme.rich.state.rail", 0f), 320f);       // UI state stays
        _settings.Set(Key("theme.other.structure.density", 0), 2);   // another theme stays
        _settings.Set(Key("appearance.zoom", 1f), 1.25f);            // unrelated settings stay

        ThemeOptionStore.ResetOptions(theme, _settings);

        Assert.False(_settings.Has("theme.rich.structure.density"));
        Assert.False(_settings.Has("theme.rich.style.scale"));
        Assert.True(_settings.Has("theme.rich.state.rail"));
        Assert.True(_settings.Has("theme.other.structure.density"));
        Assert.True(_settings.Has("appearance.zoom"));
    }

    [Fact]
    public void ResetOptions_ByThemeId_UsesTheInstalledSchema()
    {
        ThemeHost.Install(Rich());
        _settings.Set(Key("theme.rich.structure.density", 0), 2);
        ThemeOptionStore.ResetOptions("rich");
        Assert.False(_settings.Has("theme.rich.structure.density"));
        ThemeOptionStore.ResetOptions("not-installed");   // no-op
    }

    // ── ThemeHost.Apply ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static ThemeComposition Facted(ApplyMode density = ApplyMode.Live)
    {
        // The capability the structure publishes follows the density option, so a fact change is observable.
        var option = Fakes.DensityOption() with { Apply = density };
        var structure = new FakeStructure("f.structure", Fakes.Schema(option, Fakes.Toggle("tabs", true)))
        {
            CapabilityFn = static v => ShellCapabilities.Unrestricted with
            {
                MaxBandPins = (int)v.Get(new OptionKey<Density>("density", Density.Normal)),
                Tabs = v.Get(new OptionKey<bool>("tabs", true)),
            },
        };
        return Fakes.Theme("f", structure);
    }

    [Fact]
    public async Task Apply_LiveOption_WritesTheCell_PersistsIt_AndRepublishesFacts_WithoutARemount()
    {
        ThemeHost.Install(Facted());
        long epoch = ThemeHost.RootEpoch.Peek();
        var key = new OptionKey<Density>("density", Density.Normal);
        Assert.Equal((int)Density.Normal, ThemeHost.Facts.Peek().Capabilities.MaxBandPins);

        await ThemeHost.Apply(new OptionChange("f", "structure.density", Density.Roomy));

        Assert.Equal(Density.Roomy, ThemeHost.Options.Get(key));
        Assert.Equal((int)Density.Roomy, _settings.Get(Key("theme.f.structure.density", -1)));
        Assert.Equal((int)Density.Roomy, ThemeHost.Facts.Peek().Capabilities.MaxBandPins);
        Assert.Equal(Density.Roomy, ThemeHost.Values.Structure.Get(key));
        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());
    }

    [Fact]
    public async Task Apply_TransactionOption_PublishesInPlace_WithoutARemount()
    {
        // No window is bound here, so the transaction applies at once (the mounted path is ThemeTransactionMountedTests).
        ThemeHost.Install(Facted(ApplyMode.Transaction));
        long epoch = ThemeHost.RootEpoch.Peek();
        var key = new OptionKey<Density>("density", Density.Normal);

        await ThemeHost.Apply(new OptionChange("f", "structure.density", Density.Compact));
        Assert.Equal(Density.Compact, ThemeHost.Options.Get(key));
        Assert.Equal((int)Density.Compact, _settings.Get(Key("theme.f.structure.density", -1)));
        Assert.Equal((int)Density.Compact, ThemeHost.Facts.Peek().Capabilities.MaxBandPins);
        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());   // in place: a same-structure change keeps the slots' state (plan 5.6)

        int writes = _settings.WrittenCount;
        await ThemeHost.Apply(new OptionChange("f", "structure.density", Density.Compact));   // unchanged: nothing happens
        Assert.Equal(writes, _settings.WrittenCount);
        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());
    }

    [Fact]
    public async Task Apply_AcceptsAnIntForASlider_AndPersistsAFloat()
    {
        var theme = Fakes.Theme("f", style: new FakeStyle("f.style", Fakes.Schema(Fakes.Slider())));
        ThemeHost.Install(theme);
        await ThemeHost.Apply(new OptionChange("f", "style.scale", 2));
        Assert.Equal(2f, _settings.Get(Key("theme.f.style.scale", 0f)));
    }

    // Apply validates before it returns its task, so a rejected change throws at the call, not through the task.
    static void ApplyNow(OptionChange change) => _ = ThemeHost.Apply(change);

    [Fact]
    public void Apply_RejectsWhatCannotBeApplied()
    {
        var parameter = Fakes.Toggle("narrow") with { Visibility = OptionVisibility.Hidden };
        var theme = Fakes.Theme("f", new FakeStructure("f.structure", Fakes.Schema(Fakes.DensityOption(), parameter)));
        ThemeHost.Install(theme);

        Assert.Throws<ArgumentException>(() => ApplyNow(new OptionChange("f", "structure.nope", true)));
        Assert.Throws<ArgumentException>(() => ApplyNow(new OptionChange("f", "style.density", Density.Roomy)));   // wrong layer
        Assert.Throws<ArgumentException>(() => ApplyNow(new OptionChange("f", "structure.density", "Roomy")));
        Assert.Throws<ArgumentException>(() => ApplyNow(new OptionChange("f", "structure.density", (Density)9)));
        Assert.Throws<ArgumentException>(() => ApplyNow(new OptionChange("f", "structure.narrow", true)));
        Assert.Throws<ArgumentException>(() => ApplyNow(new OptionChange("elsewhere", "structure.density", Density.Roomy)));
        Assert.Equal(0, _settings.WrittenCount);
    }

    [Fact]
    public async Task Apply_ToAnotherInstalledTheme_PersistsAndChangesNothingElse()
    {
        ThemeHost.Install(Facted(), Fakes.Theme("g", new FakeStructure("g.structure", Fakes.Schema(Fakes.DensityOption()))));
        var facts = ThemeHost.Facts.Peek();
        long epoch = ThemeHost.RootEpoch.Peek();

        await ThemeHost.Apply(new OptionChange("g", "structure.density", Density.Compact));

        Assert.Equal((int)Density.Compact, _settings.Get(Key("theme.g.structure.density", -1)));
        Assert.Same(facts, ThemeHost.Facts.Peek());
        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());
    }

    [Fact]
    public void ARequestFromAView_LandsInApply()
    {
        ThemeHost.Install(Facted());
        // ctx.OptionRequests.Set(key, value) is the store's RequestForwarder.
        ThemeHost.Options.RequestForwarder.Set(new OptionKey<Density>("density", Density.Normal), Density.Compact);
        Assert.Equal(Density.Compact, ThemeHost.Options.Get(new OptionKey<Density>("density", Density.Normal)));
        Assert.Equal((int)Density.Compact, _settings.Get(Key("theme.f.structure.density", -1)));

        // An id no layer declares, and a value the option refuses, are logged and dropped, never thrown into a click handler.
        ThemeHost.Options.RequestForwarder.Set(new OptionKey<bool>("ghost", false), true);
        ThemeHost.Options.RequestForwarder.Set(new OptionKey<Density>("density", Density.Normal), (Density)9);
        Assert.Equal(Density.Compact, ThemeHost.Options.Get(new OptionKey<Density>("density", Density.Normal)));
    }

    [Fact]
    public async Task SwitchingBack_ReloadsThePersistedOptions()
    {
        ThemeHost.Install(Facted(), Fakes.Theme("g"));
        await ThemeHost.Apply(new OptionChange("f", "structure.density", Density.Roomy));
        await ThemeHost.Switch("g");
        await ThemeHost.Switch("f");
        Assert.Equal(Density.Roomy, ThemeHost.Options.Get(new OptionKey<Density>("density", Density.Normal)));
        Assert.Equal((int)Density.Roomy, ThemeHost.Facts.Peek().Capabilities.MaxBandPins);
    }

    // ── ThemeState ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly StateKey<float> Rail = new("rail", 320f, 240f, 480f);

    [Fact]
    public void ThemeState_DefaultsAndRoundTrips_PerTheme()
    {
        Assert.Equal(320f, ThemeState.Get(Rail, _settings, "a"));
        ThemeState.Set(Rail, 400f, _settings, "a");
        Assert.Equal(400f, ThemeState.Get(Rail, _settings, "a"));
        Assert.Equal(320f, ThemeState.Get(Rail, _settings, "b"));   // another theme has its own value
        Assert.Equal(400f, _settings.Get(Key("theme.a.state.rail", 0f)));
    }

    [Fact]
    public void ThemeState_ClampsOnWriteAndOnRead()
    {
        ThemeState.Set(Rail, 9999f, _settings, "a");
        Assert.Equal(480f, _settings.Get(Key("theme.a.state.rail", 0f)));

        _settings.Set(Key("theme.a.state.rail", 0f), 12f);   // hand-edited store
        Assert.Equal(240f, ThemeState.Get(Rail, _settings, "a"));
        _settings.Set(Key("theme.a.state.rail", 0f), float.NaN);
        Assert.Equal(320f, ThemeState.Get(Rail, _settings, "a"));
    }

    [Fact]
    public void ThemeState_FollowsTheActiveTheme()
    {
        ThemeHost.Install(Fakes.Theme("a"), Fakes.Theme("b"));
        ThemeState.Set(Rail, 400f);
        Assert.Equal(400f, _settings.Get(Key("theme.a.state.rail", 0f)));

        _ = ThemeHost.Switch("b");
        Assert.Equal(320f, ThemeState.Get(Rail));
        ThemeState.Set(Rail, 260f);
        Assert.Equal(260f, _settings.Get(Key("theme.b.state.rail", 0f)));
    }

    [Fact]
    public void ThemeState_StringsAreNotClamped()
    {
        var last = new StateKey<string>("tab", "home", "", "");
        ThemeState.Set(last, "queue", _settings, "a");
        Assert.Equal("queue", ThemeState.Get(last, _settings, "a"));
    }
}
