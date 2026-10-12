// ── Wavee.Tests/Theming/ThemeOptionsSectionTests.cs — the generated "‹Theme› options" section (T1b) ────────────────────
//
// `ThemeOptionsSection` turns a composition's exposed schema into Settings rows and palette commands without naming a theme.
// Against FAKE layers (ThemingFixtures.cs), so every case is a statement about the core: the rows follow `ExposedOptions`
// (parameters, hidden and replaced options get none), a kind gets the control it needs, a developer option waits for Developer
// mode, a `VisibleWhen` follows its condition, every write is `ThemeHost.Apply`, a value the option refuses changes nothing,
// "Reset options" forgets what is stored and puts the cells back, and a theme with nothing to configure is just empty.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ThemeOptionsSectionTests : IDisposable
{
    const string TestVerb = "test.options.verb";

    readonly MemoryAppSettings _settings = new();

    public ThemeOptionsSectionTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
        ThemeVerbs.Unregister(TestVerb);
    }

    enum Big : byte { A, B, C, D, E, F }

    static SelectOption<Density> Pick(string id, PickerStyle style, params Density[] choices)
        => new(id, "t." + id, null, "g", Density.Normal,
            choices.Select(c => new SelectChoice<Density>(c, "c." + c)).ToImmutableArray(), style);

    static SettingKey<T> Key<T>(string name, T def) => new(name, def);

    static ThemeComposition Theme(string id, ThemeOptionSchema? structure = null, ThemeOptionSchema? style = null,
        IReadOnlyList<CompositionOption>? compositionOptions = null, IReadOnlySet<string>? hidden = null,
        IReadOnlyDictionary<string, object>? overrides = null, IReadOnlyDictionary<string, string>? replaced = null,
        IReadOnlyList<LegacyOptionRow>? legacy = null)
        => Fakes.Theme(id, new FakeStructure(id + ".structure", structure) { Legacy = legacy ?? Array.Empty<LegacyOptionRow>() },
            new FakeStyle(id + ".style", style), compositionOptions: compositionOptions, hidden: hidden, overrides: overrides, replaced: replaced);

    ThemeOptionsModel Model(ThemeComposition theme, bool developer = false)
        => ThemeOptionsSection.Build(theme, ThemeOptionStore.Load(theme, _settings), developer);

    static T Peek<T>(string layerId, OptionKey<T> key)
        => layerId switch
        {
            "structure" => ThemeHost.Values.Structure.Get(key),
            "style" => ThemeHost.Values.Style.Get(key),
            _ => ThemeHost.Values.Composition.Get(key),
        };

    // ── the rows follow the exposed schema ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rows_follow_the_schema_in_order_and_each_kind_gets_the_control_it_needs()
    {
        var theme = Theme("t", Fakes.Schema(Fakes.Toggle("tabs", true), Fakes.DensityOption("density")),
            Fakes.Schema(Fakes.Slider("scale"), Fakes.Colour("tint")));
        var model = Model(theme);

        Assert.Equal(new[] { "structure.tabs", "structure.density", "style.scale", "style.tint" }, model.Rows.Select(r => r.QualifiedId));
        Assert.Equal(new[] { SettingsRowKind.Toggle, SettingsRowKind.Segmented, SettingsRowKind.Slider, SettingsRowKind.Picker },
            model.Rows.Select(r => r.Row.Kind));
        Assert.Equal(new[] { "t.tabs", "t.density", "t.scale", "t.tint" }, model.Rows.Select(r => r.Row.TitleKey));

        var slider = model.Rows[2];
        Assert.Equal((0.5f, 2f, 0.25f), (slider.Min, slider.Max, slider.Step));
        Assert.True(model.Rows[3].IsColor);
        Assert.Empty(model.Rows[3].Swatches);
        Assert.Equal(new[] { "c", "n", "r" }, model.Rows[1].ChoiceTitleKeys);
        Assert.Equal(1, model.Rows[1].Current);   // Normal
        Assert.Equal(true, model.Rows[0].Current);
    }

    [Fact]
    public void Parameters_hidden_and_replaced_options_have_no_row_and_the_replacement_has_one()
    {
        var parameter = new ToggleOption("param", "t.param", null, "g", false) with { Visibility = OptionVisibility.Hidden };
        var theme = Theme("t",
            Fakes.Schema(Fakes.Toggle("shown"), parameter, Fakes.Toggle("hiddenByTheme"), Fakes.Toggle("old")),
            compositionOptions: new[] { new CompositionOption(Fakes.Toggle("new"), static _ => new CompositionEffect()) },
            hidden: new HashSet<string> { "hiddenByTheme" },
            replaced: new Dictionary<string, string> { ["old"] = "new" });

        Assert.Equal(new[] { "structure.shown", "composition.new" }, Model(theme).Rows.Select(r => r.QualifiedId));
    }

    [Fact]
    public void A_theme_with_nothing_to_configure_is_empty_and_has_nothing_to_reset()
    {
        var model = Model(Theme("bare"));
        Assert.True(model.IsEmpty);
        Assert.Empty(model.Rows);
        Assert.Null(model.Reset);
    }

    [Fact]
    public void Legacy_rows_ride_next_to_the_generated_ones_and_keep_the_section_from_being_empty()
    {
        var legacy = new[] { new LegacyOptionRow("rowDensity", "g.one"), new LegacyOptionRow("railUniform", "g.two") };
        var onlyLegacy = Model(Theme("old", legacy: legacy));
        Assert.False(onlyLegacy.IsEmpty);
        Assert.Empty(onlyLegacy.Rows);
        Assert.Equal(legacy, onlyLegacy.Legacy);
        Assert.Null(onlyLegacy.Reset);   // no stored option: nothing for Reset to be about

        var both = Model(Theme("mixed", Fakes.Schema(Fakes.Toggle("tabs")), legacy: legacy));
        Assert.Single(both.Rows);
        Assert.Equal(2, both.Legacy.Count);
        Assert.NotNull(both.Reset);
    }

    // ── visibility ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_developer_option_waits_for_developer_mode()
    {
        var lab = Fakes.Toggle("lab") with { Visibility = OptionVisibility.Developer };
        var theme = Theme("t", Fakes.Schema(Fakes.Toggle("tabs"), lab));

        Assert.Equal(new[] { "structure.tabs" }, Model(theme, developer: false).Rows.Select(r => r.QualifiedId));
        Assert.Equal(new[] { "structure.tabs", "structure.lab" }, Model(theme, developer: true).Rows.Select(r => r.QualifiedId));
    }

    [Fact]
    public void VisibleWhen_follows_its_condition_and_keeps_the_value_while_hidden()
    {
        var tabs = Fakes.Toggle("tabs", true);
        var count = new SliderOption("count", "t.count", null, "g", 2f, 0f, 3f, 1f, null) with { VisibleWhen = new OptionCondition("tabs", true) };
        var theme = Theme("t", Fakes.Schema(tabs, count));

        Assert.Equal(new[] { "structure.tabs", "structure.count" }, Model(theme).Rows.Select(r => r.QualifiedId));

        _settings.Set(Key("theme.t.structure.count", 0f), 3f);
        _settings.Set(Key("theme.t.structure.tabs", true), false);
        Assert.Equal(new[] { "structure.tabs" }, Model(theme).Rows.Select(r => r.QualifiedId));
        Assert.Equal(3f, _settings.Get(Key("theme.t.structure.count", 0f)));   // hidden is not forgotten

        _settings.Set(Key("theme.t.structure.tabs", true), true);
        var back = Model(theme).Rows.Single(r => r.QualifiedId == "structure.count");
        Assert.Equal(3f, back.Current);
    }

    [Fact]
    public void A_condition_that_names_no_value_keeps_the_row()
    {
        var orphan = Fakes.Toggle("orphan") with { VisibleWhen = new OptionCondition("ghost", true) };
        var model = Model(Theme("t", Fakes.Schema(orphan)));
        Assert.Equal(new[] { "structure.orphan" }, model.Rows.Select(r => r.QualifiedId));
    }

    [Fact]
    public void A_condition_compares_enums_by_their_number()
    {
        var density = Fakes.DensityOption("density", Density.Roomy);
        var dependent = Fakes.Toggle("airy") with { VisibleWhen = new OptionCondition("density", (int)Density.Roomy) };
        var model = Model(Theme("t", Fakes.Schema(density, dependent)));
        Assert.Contains(model.Rows, r => r.QualifiedId == "structure.airy");
    }

    // ── select styles ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_select_is_drawn_as_its_picker_style_says_and_degrades_by_its_choice_count()
    {
        var all = new[] { Density.Compact, Density.Normal, Density.Roomy };
        var theme = Theme("t", Fakes.Schema(
            Pick("radio", PickerStyle.Radio, all),
            Pick("combo", PickerStyle.Combo, all),
            Pick("cards", PickerStyle.Cards, all),
            Pick("swatches", PickerStyle.Swatches, all),
            Pick("twoWay", PickerStyle.Toggle, Density.Compact, Density.Roomy),
            Pick("threeWay", PickerStyle.Toggle, all),
            Pick("none", PickerStyle.Radio),
            new SelectOption<Big>("many", "t.many", null, "g", Big.A,
                Enum.GetValues<Big>().Select(b => new SelectChoice<Big>(b, "c." + b)).ToImmutableArray(), PickerStyle.Radio)));

        var kinds = Model(theme).Rows.ToDictionary(r => r.QualifiedId, r => r.Row.Kind);
        Assert.Equal(SettingsRowKind.Segmented, kinds["structure.radio"]);
        Assert.Equal(SettingsRowKind.Choice, kinds["structure.combo"]);
        Assert.Equal(SettingsRowKind.Picker, kinds["structure.cards"]);
        Assert.Equal(SettingsRowKind.Picker, kinds["structure.swatches"]);
        Assert.Equal(SettingsRowKind.Toggle, kinds["structure.twoWay"]);
        Assert.Equal(SettingsRowKind.Segmented, kinds["structure.threeWay"]);   // a Toggle style needs exactly two choices
        Assert.Equal(SettingsRowKind.Choice, kinds["structure.many"]);          // too many for segments
        Assert.DoesNotContain("structure.none", kinds.Keys);                    // nothing to choose from: no dead control
    }

    // ── values ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_stored_value_that_is_no_longer_valid_reads_as_the_default_and_stays_stored()
    {
        var theme = Theme("t", Fakes.Schema(Fakes.DensityOption("density", Density.Roomy)), Fakes.Schema(Fakes.Slider("scale")));
        _settings.Set(Key("theme.t.structure.density", 0), 99);   // not a choice
        _settings.Set(Key("theme.t.style.scale", 0f), 50f);        // out of range

        var model = Model(theme);
        Assert.Equal(2, model.Rows[0].Row.Value(_settings));       // Roomy, the default
        Assert.Equal(1f, model.Rows[1].Row.Value(_settings));
        Assert.Equal(99, _settings.Get(Key("theme.t.structure.density", 0)));
    }

    [Fact]
    public void A_composition_default_override_is_the_default_the_row_reads()
    {
        var theme = Theme("t", Fakes.Schema(Fakes.Toggle("tabs", true)),
            overrides: new Dictionary<string, object> { ["tabs"] = false });
        var row = Model(theme).Rows.Single();
        Assert.Equal(false, row.Current);
        Assert.Equal(false, row.Row.Value(_settings));
    }

    // ── writing ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_write_goes_through_the_host_and_lands_in_the_cells_and_in_settings()
    {
        var theme = Theme("t", Fakes.Schema(Fakes.Toggle("tabs", true), Fakes.DensityOption("density")), Fakes.Schema(Fakes.Slider("scale"), Fakes.Colour("tint")));
        ThemeHost.Install(theme);
        var rows = ThemeOptionsSection.Current(false).Rows;

        Assert.True(rows[0].Row.Writer.Toggle!(false));
        Assert.False(Peek("structure", new OptionKey<bool>("tabs", true)));
        Assert.False(_settings.Get(Key("theme.t.structure.tabs", true)));

        Assert.True(rows[1].Row.Writer.Choose!(2));
        Assert.Equal(Density.Roomy, Peek("structure", new OptionKey<Density>("density", Density.Normal)));
        Assert.Equal((int)Density.Roomy, _settings.Get(Key("theme.t.structure.density", 0)));

        Assert.True(rows[2].Row.Writer.Slide!(1.5f));
        Assert.Equal(1.5f, Peek("style", new OptionKey<float>("scale", 1f)));

        int argb = ThemeOptionsSection.PackColor(ColorF.FromRgba(0x20, 0x40, 0x60));
        Assert.True(rows[3].Row.Writer.Choose!(argb));
        Assert.Equal(ColorF.FromRgba(0x20, 0x40, 0x60), Peek("style", new OptionKey<ColorF>("tint", default)));

        // The model rebuilt now answers the new values (what a re-render shows).
        var again = ThemeOptionsSection.Current(false).Rows;
        Assert.Equal(false, again[0].Current);
        Assert.Equal(2, again[1].Current);
        Assert.Equal(1.5f, again[2].Current);
        Assert.Equal(argb, again[3].Current);
        Assert.Equal(2, again[1].Live());
    }

    [Fact]
    public void A_value_the_option_refuses_changes_nothing_and_does_not_throw()
    {
        var theme = Theme("t", Fakes.Schema(Fakes.DensityOption("density")), Fakes.Schema(Fakes.Slider("scale")));
        ThemeHost.Install(theme);
        var rows = ThemeOptionsSection.Current(false).Rows;

        Assert.True(rows[1].Row.Writer.Slide!(9f));       // out of range (0.5 .. 2): refused by the option, logged, page re-reads
        Assert.False(rows[0].Row.Writer.Choose!(7));      // no such choice: nothing to apply
        Assert.False(rows[0].Row.Writer.Choose!(-1));

        Assert.Equal(1f, Peek("style", new OptionKey<float>("scale", 1f)));
        Assert.Equal(Density.Normal, Peek("structure", new OptionKey<Density>("density", Density.Normal)));
        Assert.False(_settings.Has("theme.t.style.scale"));
        Assert.False(_settings.Has("theme.t.structure.density"));
    }

    [Fact]
    public void A_two_way_select_toggles_between_its_two_choices()
    {
        var theme = Theme("t", Fakes.Schema(Pick("flip", PickerStyle.Toggle, Density.Compact, Density.Roomy)));
        ThemeHost.Install(theme);
        var row = ThemeOptionsSection.Current(false).Rows.Single();
        Assert.Equal(SettingsRowKind.Toggle, row.Row.Kind);
        Assert.Equal(false, row.Current);   // the default (Normal) is not the second choice

        row.Row.Writer.Toggle!(true);
        Assert.Equal(Density.Roomy, Peek("structure", new OptionKey<Density>("flip", Density.Normal)));
        Assert.Equal(true, ThemeOptionsSection.Current(false).Rows.Single().Current);

        row.Row.Writer.Toggle!(false);
        Assert.Equal(Density.Compact, Peek("structure", new OptionKey<Density>("flip", Density.Normal)));
    }

    [Fact]
    public void A_transaction_option_remounts_the_root_and_a_live_option_does_not()
    {
        var live = Fakes.Toggle("live");
        var tx = Fakes.Toggle("tx") with { Apply = ApplyMode.Transaction };
        ThemeHost.Install(Theme("t", Fakes.Schema(live, tx)));
        var rows = ThemeOptionsSection.Current(false).Rows;
        Assert.Equal(new[] { ApplyMode.Live, ApplyMode.Transaction }, rows.Select(r => r.Apply));

        long epoch = ThemeHost.RootEpoch.Peek();
        rows[0].Row.Writer.Toggle!(true);
        Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());
        rows[1].Row.Writer.Toggle!(true);
        Assert.Equal(epoch + 1, ThemeHost.RootEpoch.Peek());
    }

    // ── actions and references ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_action_runs_its_registered_verb_and_is_greyed_without_one()
    {
        var theme = Theme("t", Fakes.Schema(new ActionOption("clear", "t.clear", "g", TestVerb)));
        ThemeHost.Install(theme);

        var greyed = ThemeOptionsSection.Current(false).Rows.Single();
        Assert.False(greyed.Enabled);
        Assert.False(greyed.Row.Enabled(SettingsFacts.Live).Enabled);
        Assert.True(greyed.Row.SilentWhenDisabled);
        Assert.Empty(ThemeOptionsSection.Commands(ThemeOptionsSection.Current(false)));

        int calls = 0;
        ThemeVerbs.Register(TestVerb, () => { calls++; return true; });
        var armed = ThemeOptionsSection.Current(false).Rows.Single();
        Assert.True(armed.Enabled);
        Assert.True(armed.Row.Writer.Run!());
        Assert.Equal(1, calls);

        ThemeVerbs.Register(TestVerb, () => throw new InvalidOperationException("boom"));
        Assert.False(ThemeVerbs.Run(TestVerb));   // a failing verb is logged, never thrown into the page
        Assert.False(ThemeVerbs.Run("test.options.unknown"));
    }

    [Fact]
    public void A_reference_to_a_core_preference_is_the_core_row_itself()
    {
        var theme = Theme("t", Fakes.Schema(
            new PrefReferenceOption("zoomRef", "g", CorePref.Zoom),
            new PrefReferenceOption("modeRef", "g", CorePref.AppMode),
            new PrefReferenceOption("motionRef", "g", CorePref.ReducedMotionInfo)));
        var rows = Model(theme).Rows;

        Assert.Equal(new CorePref?[] { CorePref.Zoom, CorePref.AppMode, CorePref.ReducedMotionInfo }, rows.Select(r => r.Pref));
        Assert.Same(SettingsDoc.Row(Settings.Tab.Appearance, "zoom"), rows[0].Row);
        Assert.Same(SettingsDoc.Row(Settings.Tab.Appearance, "appMode"), rows[1].Row);
        Assert.Equal(SettingsRowKind.Fact, rows[2].Row.Kind);
        Assert.True(rows[2].Row.Writer.IsReadOnly);
        Assert.Null(Model(theme).Reset);   // references store nothing
    }

    // ── reset ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_forgets_what_is_stored_puts_the_cells_back_and_greys_itself()
    {
        var theme = Theme("t", Fakes.Schema(Fakes.Toggle("tabs", true), Fakes.DensityOption("density")));
        ThemeHost.Install(theme);
        _settings.Set(Key("theme.state.rail", 0), 7);   // unrelated settings stay
        var first = ThemeOptionsSection.Current(false);
        Assert.False(first.Reset!.Enabled(SettingsFacts.Live).Enabled);   // nothing stored yet

        first.Rows[0].Row.Writer.Toggle!(false);
        first.Rows[1].Row.Writer.Choose!(2);
        var stored = ThemeOptionsSection.Current(false);
        Assert.True(stored.Reset!.Enabled(SettingsFacts.Live).Enabled);
        Assert.NotNull(stored.Reset.Confirm);   // destructive: confirmed like every other

        Assert.True(stored.Reset.Writer.Run!());

        Assert.False(_settings.Has("theme.t.structure.tabs"));
        Assert.False(_settings.Has("theme.t.structure.density"));
        Assert.Equal(7, _settings.Get(Key("theme.state.rail", 0)));
        Assert.True(Peek("structure", new OptionKey<bool>("tabs", false)));
        Assert.Equal(Density.Normal, Peek("structure", new OptionKey<Density>("density", Density.Roomy)));
        Assert.Equal(true, ThemeOptionsSection.Current(false).Rows[0].Current);
        Assert.False(ThemeOptionsSection.Current(false).Reset!.Enabled(SettingsFacts.Live).Enabled);
    }

    [Fact]
    public void Reset_remounts_the_root_once_when_a_transaction_option_changes_back()
    {
        var tx = Fakes.Toggle("tx") with { Apply = ApplyMode.Transaction };
        ThemeHost.Install(Theme("t", Fakes.Schema(tx, Fakes.Toggle("live"))));
        var rows = ThemeOptionsSection.Current(false).Rows;
        rows[0].Row.Writer.Toggle!(true);
        rows[1].Row.Writer.Toggle!(true);
        long epoch = ThemeHost.RootEpoch.Peek();

        ThemeHost.ResetOptions("t");
        Assert.Equal(epoch + 1, ThemeHost.RootEpoch.Peek());

        ThemeHost.ResetOptions("t");   // nothing changes: no second remount
        Assert.Equal(epoch + 1, ThemeHost.RootEpoch.Peek());
    }

    [Fact]
    public void Resetting_another_theme_forgets_its_keys_only()
    {
        ThemeHost.Install(Theme("a", Fakes.Schema(Fakes.Toggle("tabs", true))), Theme("b", Fakes.Schema(Fakes.Toggle("tabs", true))));
        _settings.Set(Key("theme.b.structure.tabs", true), false);
        _settings.Set(Key("theme.a.structure.tabs", true), false);

        ThemeHost.ResetOptions("b");

        Assert.False(_settings.Has("theme.b.structure.tabs"));
        Assert.True(_settings.Has("theme.a.structure.tabs"));
        ThemeHost.ResetOptions("nobody");   // not installed: a no-op
    }

    // ── palette commands ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_options_that_ask_contribute_palette_rows_and_each_kind_gives_its_own()
    {
        var theme = Theme("t", Fakes.Schema(
            Fakes.Toggle("quiet") with { Command = true },
            Fakes.Toggle("silent"),
            Fakes.DensityOption("density") with { Command = true },
            new SliderOption("scale", "t.scale", null, "g", 1f, 0f, 2f, 0.5f, null) with { Command = true },
            new ActionOption("clear", "t.clear", "g", TestVerb)));
        ThemeVerbs.Register(TestVerb, static () => true);
        ThemeHost.Install(theme);

        var commands = ThemeOptionsSection.Commands(developerMode: false);
        Assert.Equal(new[]
        {
            "theme.structure.quiet.on", "theme.structure.quiet.off",
            "theme.structure.density.0", "theme.structure.density.1", "theme.structure.density.2",
            "theme.structure.clear",
        }, commands.Select(c => c.Id));
        Assert.All(commands, c => Assert.False(string.IsNullOrEmpty(c.Label)));
    }

    [Fact]
    public void Running_a_palette_command_applies_the_option()
    {
        ThemeHost.Install(Theme("t", Fakes.Schema(Fakes.Toggle("quiet") with { Command = true }, Fakes.DensityOption("density") with { Command = true })));
        var commands = ThemeOptionsSection.Commands(false).ToDictionary(c => c.Id);

        commands["theme.structure.quiet.on"].Run();
        Assert.True(Peek("structure", new OptionKey<bool>("quiet", false)));
        commands["theme.structure.density.0"].Run();
        Assert.Equal(Density.Compact, Peek("structure", new OptionKey<Density>("density", Density.Normal)));
        commands["theme.structure.quiet.off"].Run();
        Assert.False(Peek("structure", new OptionKey<bool>("quiet", true)));
    }

    [Fact]
    public void The_palette_has_no_theme_rows_before_a_theme_is_installed()
        => Assert.Empty(ThemeOptionsSection.Commands(developerMode: true));

    // ── groups ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_group_without_a_loc_key_is_shown_as_written()
        => Assert.Equal("Some plain group", ThemeOptionsSection.GroupTitle("Some plain group"));

    [Fact]
    public void Colours_pack_and_unpack_through_the_public_helpers()
    {
        var c = ColorF.FromRgba(0x12, 0x34, 0x56, 0x78);
        Assert.Equal(c, ThemeOptionsSection.UnpackColor(ThemeOptionsSection.PackColor(c)));
    }

    [Fact]
    public void Swatches_are_offered_as_given_and_a_missing_list_means_a_free_picker()
    {
        var swatches = ImmutableArray.Create(ColorF.FromRgba(255, 0, 0), ColorF.FromRgba(0, 255, 0));
        var withSwatches = new ColorOption("tint", "t.tint", null, "g", swatches[0], swatches);
        var model = Model(Theme("t", Fakes.Schema(withSwatches)));
        Assert.Equal(swatches.ToArray(), model.Rows.Single().Swatches.ToArray());
        Assert.Equal(ThemeOptionsSection.PackColor(swatches[0]), model.Rows.Single().Current);
    }
}
