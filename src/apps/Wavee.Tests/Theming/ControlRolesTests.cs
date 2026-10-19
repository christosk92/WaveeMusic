// -- Wavee.Tests/Theming/ControlRolesTests.cs -- the control roles resolve under Classic and Zune, and say what they draw today (lane WR) --
//
// Pins the app-side ControlRole lookups over the engine's control style table: PrimaryAction, SecondaryAction and Transport resolve to the
// Button.Style the style's own table gives their appearance (Classic has no entry, so the built-in Fluent button; Zune's accent disc and
// words), Follow to the app's follow tint, ListBackplate to the track rows' plate, and CommandButtonStyle.LabelMode to the rendering each
// style has today (Classic icon and label, Zune words) at every tier.
//
// NO TOKEN CHANGES HERE (the assembly runs facts in parallel and `Tok` is process-wide): every lookup takes the style's control table as an
// argument and every colour is read per kind through `ColorRole.Resolve`; nothing installs a definition.

using System.Collections;
using System.Reflection;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ControlRolesTests
{
    static readonly ClassicStyle Classic = new();
    static readonly ZuneStyle Zune = ZuneStyle.Create();

    static ThemeDefinition ClassicDef => Classic.Definition(OptionValues.Empty, AccentRamp.FluentDefault);
    static ThemeDefinition ZuneDef => Zune.Definition(OptionValues.Empty, AccentRamp.FluentDefault);

    // A control Style holds GradientSpecs whose stop arrays compare by reference, so two styles built from the same inputs are not
    // record-equal. Compare them member by member, arrays element by element.
    static void SameStyle(object? expected, object actual)
    {
        Assert.NotNull(expected);
        Assert.True(Deep(expected, actual, "style", out var why), why);
    }

    static bool Deep(object? a, object? b, string path, out string why)
    {
        why = "";
        if (a is null || b is null) { why = path + ": " + (a ?? "null") + " vs " + (b ?? "null"); return a is null && b is null; }
        var type = a.GetType();
        if (type != b.GetType()) { why = path + ": type " + type + " vs " + b.GetType(); return false; }
        if (type.IsPrimitive || type.IsEnum || a is string || a is Delegate || a is decimal)
        {
            if (a.Equals(b)) return true;
            why = path + ": " + a + " vs " + b;
            return false;
        }
        if (a is IList la && b is IList lb)
        {
            if (la.Count != lb.Count) { why = path + ": length " + la.Count + " vs " + lb.Count; return false; }
            for (int i = 0; i < la.Count; i++) if (!Deep(la[i], lb[i], path + "[" + i + "]", out why)) return false;
            return true;
        }
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.Name == "EqualityContract") continue;
            if (!Deep(prop.GetValue(a), prop.GetValue(b), path + "." + prop.Name, out why)) return false;
        }
        return true;
    }

    static ControlRoleTable Of(ThemeDefinition d) => Assert.IsType<WaveeStyleRoles>(d.AppRoles).Controls;

    // -- the vocabulary ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_roles_are_the_five_plan_names_with_stable_values()
    {
        Assert.Equal(new[] { "PrimaryAction", "SecondaryAction", "Transport", "Follow", "ListBackplate" }, Enum.GetNames<ControlRole>());
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, Enum.GetValues<ControlRole>().Select(r => (byte)r));
        Assert.Equal(new[] { "Words", "IconLabel", "Icon" }, Enum.GetNames<LabelMode>());
    }

    [Fact]
    public void Each_style_publishes_its_own_control_roles_on_its_definition()
    {
        Assert.Same(ClassicControlRoles.Table, Of(ClassicDef));
        Assert.Same(ZuneControlRoles.Table, Of(ZuneDef));
        Assert.Same(ClassicControlRoles.Table, ClassicRoles.Table.Controls);
        Assert.Same(ZuneControlRoles.Table, ZuneRoles.Table.Controls);
        Assert.NotEqual(ClassicControlRoles.Table, ZuneControlRoles.Table);
    }

    [Fact]
    public void The_fluent_set_is_the_stock_grammar_a_style_that_names_no_controls_gets()
    {
        var f = ControlRoleTable.Fluent;
        Assert.Equal(new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Medium), f.PrimaryAction);
        Assert.Equal(new ButtonRoleSpec(ButtonAppearance.Standard, ControlSize.Medium), f.SecondaryAction);
        Assert.Equal(new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Large, Round: true), f.Transport);
        Assert.NotNull(f.ListBackplate.Pill);
    }

    [Fact]
    public void A_changed_control_role_is_a_non_colour_change()
    {
        Assert.False(ClassicRoles.Table.DiffersOnlyInColours(ClassicRoles.Table with { Controls = ZuneControlRoles.Table }));
        Assert.True(ClassicRoles.Table.DiffersOnlyInColours(ClassicRoles.Table with { Controls = ClassicControlRoles.Table with { } }));
    }

    // -- buttons under Classic: the built-in Fluent buttons ------------------------------------------------------------------------

    [Fact]
    public void Classic_resolves_primary_and_secondary_to_the_built_in_buttons_it_draws_today()
    {
        var def = ClassicDef;
        Assert.False(def.Controls.Contains(ControlStyles.ButtonStyle));   // Classic restyles no button: the Fluent default is its look
        var roles = Of(def);
        SameStyle(Button.FluentStyle(ButtonAppearance.Accent, ControlSize.Medium), roles.ButtonStyle(ControlRole.PrimaryAction, def.Controls));
        SameStyle(Button.FluentStyle(ButtonAppearance.Standard, ControlSize.Medium), roles.ButtonStyle(ControlRole.SecondaryAction, def.Controls));
    }

    [Fact]
    public void Classic_transport_is_the_accent_disc_at_the_large_size()
    {
        var def = ClassicDef;
        var style = Of(def).ButtonStyle(ControlRole.Transport, def.Controls);
        Assert.Equal(ControlRoleTable.RoundCorner, style.CornerRadius);
        SameStyle(Button.FluentStyle(ButtonAppearance.Accent, ControlSize.Large) with { CornerRadius = ControlRoleTable.RoundCorner }, style);
    }

    // -- buttons under Zune: the control table's disc and words ---------------------------------------------------------------------

    [Fact]
    public void Zune_resolves_primary_and_secondary_through_its_control_table()
    {
        var def = ZuneDef;
        Assert.True(def.Controls.Contains(ControlStyles.ButtonStyle));
        var roles = Of(def);
        var primary = roles.ButtonStyle(ControlRole.PrimaryAction, def.Controls);
        SameStyle(ZuneControls.ButtonFor(ButtonAppearance.Accent, ControlSize.Medium), primary);
        Assert.Equal(ZuneControls.Round, primary.CornerRadius);                                    // the round metro disc
        Assert.Equal(36f, primary.MinHeight);
        var secondary = roles.ButtonStyle(ControlRole.SecondaryAction, def.Controls);
        SameStyle(ZuneControls.ButtonFor(ButtonAppearance.Standard, ControlSize.Medium), secondary);
        Assert.Equal(0f, secondary.CornerRadius);                                                  // a word, not a plate
    }

    [Fact]
    public void Zune_transport_is_the_40_disc()
    {
        var def = ZuneDef;
        var style = Of(def).ButtonStyle(ControlRole.Transport, def.Controls);
        SameStyle(ZuneControls.ButtonFor(ButtonAppearance.Accent, ControlSize.Large), style);   // the round is Zune's already: no override changes it
        Assert.Equal(40f, style.MinHeight);
        Assert.Equal(ZuneControls.Round, style.CornerRadius);
    }

    [Fact]
    public void A_call_site_size_overrides_the_roles_size_and_the_same_role_reads_each_styles_table()
    {
        var zune = ZuneDef;
        var classic = ClassicDef;
        SameStyle(ZuneControls.ButtonFor(ButtonAppearance.Accent, ControlSize.Small), Of(zune).ButtonStyle(ControlRole.PrimaryAction, zune.Controls, ControlSize.Small));
        SameStyle(Button.FluentStyle(ButtonAppearance.Accent, ControlSize.Small), Of(classic).ButtonStyle(ControlRole.PrimaryAction, classic.Controls, ControlSize.Small));
        // The role is the same word in both styles; only the control table differs.
        Assert.False(Deep(Of(classic).ButtonStyle(ControlRole.PrimaryAction, classic.Controls),
            Of(zune).ButtonStyle(ControlRole.PrimaryAction, zune.Controls), "style", out _));
    }

    [Fact]
    public void Follow_and_the_backplate_are_not_button_roles()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicControlRoles.Table.ButtonSpec(ControlRole.Follow));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZuneControlRoles.Table.ButtonSpec(ControlRole.ListBackplate));
    }

    // -- follow -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Follow_is_the_apps_follow_tint_under_both_styles()
    {
        var accent = ColorF.FromRgba(0x60, 0xCD, 0xFF);
        Assert.Equal(ControlSize.Medium, ClassicControlRoles.Table.Follow.Size);
        Assert.Equal(ControlSize.Medium, ZuneControlRoles.Table.Follow.Size);
        foreach (bool light in new[] { false, true })
        {
            SameStyle(ButtonRules.FollowStyle(accent, light), ClassicControlRoles.Table.FollowStyle(accent, light));
            SameStyle(ButtonRules.FollowStyle(accent, light), ZuneControlRoles.Table.FollowStyle(accent, light));
        }
    }

    // -- list backplate ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Classic_backplate_is_the_track_rows_plate()
    {
        var spec = ClassicControlRoles.Table.ListBackplate;
        // ClassicTrackRowLayout is internal to the app: its RowRadius 6 and its 3 x 16 pill (radius 1.5, lead 2) are pinned as numbers.
        Assert.Equal(6f, spec.Radius);
        Assert.Equal(Track.RowMetrics.RowInset, spec.Inset);
        Assert.NotNull(spec.Pill);
        Assert.Equal(new SelectionPill(Width: 3f, Height: 16f, Radius: 1.5f, Lead: 2f), spec.Pill!.Value);
    }

    [Fact]
    public void Zune_backplate_is_square_flush_and_pill_free()
    {
        var spec = ZuneControlRoles.Table.ListBackplate;
        Assert.Equal(ZuneRoles.Table.Shape.Control, spec.Radius);   // 0: Z-State-Rows has no corners
        Assert.Equal(0f, spec.Inset);
        Assert.Null(spec.Pill);
    }

    [Theory]
    [InlineData(ThemeKind.Dark)]
    [InlineData(ThemeKind.Light)]
    public void The_backplate_fills_are_the_styles_layer_roles(ThemeKind kind)
    {
        foreach (var roles in new[] { ClassicRoles.Table, ZuneRoles.Table })
        {
            var plate = roles.Controls.Backplate(roles.Layer, kind);
            Assert.Equal(roles.Layer.RowHover.Resolve(kind), plate.Hover);
            Assert.Equal(roles.Layer.RowPressed.Resolve(kind), plate.Pressed);
            Assert.Equal(roles.Layer.SelectedRest.Resolve(kind), plate.SelectedRest);
            Assert.Equal(roles.Layer.SelectedHover.Resolve(kind), plate.SelectedHover);
            Assert.Equal(roles.Layer.SelectedPressed.Resolve(kind), plate.SelectedPressed);
            Assert.Equal(roles.Controls.ListBackplate.Radius, plate.Radius);
            Assert.Equal(roles.Controls.ListBackplate.Pill, plate.Pill);
        }
    }

    // -- CommandButtonStyle.LabelMode ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_default_label_mode_is_what_each_style_draws_today_at_every_tier()
    {
        foreach (var tier in Enum.GetValues<WidthTier>())
        {
            Assert.Equal(LabelMode.IconLabel, ClassicControlRoles.Table.Command.For(tier));   // a glyph and its label where the call site has both
            Assert.Equal(LabelMode.Words, ZuneControlRoles.Table.Command.For(tier));          // lowercase words
            Assert.Equal(LabelMode.IconLabel, ControlRoleTable.Fluent.Command.For(tier));
        }
    }

    [Fact]
    public void IconLabel_builds_what_a_call_site_with_a_glyph_and_a_label_builds_today()
    {
        Assert.Equal(new CommandParts(true, true), CommandButtonStyle.Parts(LabelMode.IconLabel, hasGlyph: true));
        Assert.Equal(new CommandParts(false, true), CommandButtonStyle.Parts(LabelMode.IconLabel, hasGlyph: false));   // a word-only command stays a word
        Assert.Equal(new CommandParts(true, false), CommandButtonStyle.Parts(LabelMode.IconLabel, hasGlyph: true, hasLabel: false));
    }

    [Fact]
    public void Words_and_Icon_drop_the_other_part_but_never_leave_a_command_empty()
    {
        Assert.Equal(new CommandParts(false, true), CommandButtonStyle.Parts(LabelMode.Words, hasGlyph: true));
        Assert.Equal(new CommandParts(true, false), CommandButtonStyle.Parts(LabelMode.Icon, hasGlyph: true));
        Assert.Equal(new CommandParts(false, true), CommandButtonStyle.Parts(LabelMode.Icon, hasGlyph: false));         // nothing to show but the label
        Assert.Equal(new CommandParts(true, false), CommandButtonStyle.Parts(LabelMode.Words, hasGlyph: true, hasLabel: false));
    }

    [Fact]
    public void A_label_mode_can_be_chosen_by_tier()
    {
        var winUi = CommandButtonStyle.FromTier(WidthTier.Standard, LabelMode.IconLabel, below: LabelMode.Icon);
        Assert.Equal(LabelMode.Icon, winUi.For(WidthTier.Compact));
        Assert.Equal(LabelMode.Icon, winUi.For(WidthTier.Small));
        Assert.Equal(LabelMode.IconLabel, winUi.For(WidthTier.Standard));
        Assert.Equal(LabelMode.IconLabel, winUi.For(WidthTier.Huge));
        Assert.Equal(LabelMode.IconLabel, winUi.For(WidthTier.Huge4K));
        Assert.Equal(new CommandParts(true, false), winUi.PartsAt(WidthTier.Compact, hasGlyph: true));
        Assert.Equal(new CommandParts(true, true), winUi.PartsAt(WidthTier.Standard, hasGlyph: true));
    }

    // -- the live facade ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_facade_reads_the_baseline_roles_without_a_definition()
    {
        // R.Controls is Classic's table until a definition carrying another is installed (this process never installs one).
        Assert.Equal(R.Current.Controls, R.Controls.Table);
        Assert.Equal(ControlSize.Medium, R.Controls.SizeOf(ControlRole.PrimaryAction));
        Assert.Equal(ControlSize.Large, R.Controls.SizeOf(ControlRole.Transport));
    }
}
