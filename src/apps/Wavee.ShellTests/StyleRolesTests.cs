// ── Wavee.ShellTests/StyleRolesTests.cs — the roles follow the definition: boot, swap, flip, a mounted view (T2a) ────────
//
// `R` reads `Tok.Definition.AppRoles` at every call and caches nothing, so a definition swap must reach the NEXT read and the
// engine's epoch re-render must show it on screen. That needs `Tok.Use`, which is process-wide state: it lives here, in the one
// process whose facts never run in parallel, and every fact puts the definition back. (The values of Classic's table are
// pinned against `Design` in `Wavee.Tests\Theming\ClassicRolesTests.cs`.)

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.ShellTests;

public sealed class StyleRolesTests(ShellHost host)
{
    /// <summary>A style that differs from Classic in one type role, one space role and one motion role.</summary>
    static WaveeStyleRoles Alt() => ClassicRoles.Table with
    {
        Type = ClassicRoles.Table.Type with { TrackTitle = new TextRole(TypeRung.BodyStrong, Size: 21f, LineHeight: 30f) },
        Space = ClassicRoles.Table.Space with { SectionGap = 48f },
        Motion = ClassicRoles.Table.Motion with { ScaleSubtle = new ScaleTier(1.1f, 0.9f) },
    };

    // ── the process ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_booted_shell_runs_on_classics_roles()
        => host.Run(() =>
        {
            Assert.Same(ClassicRoles.Table, Tok.Definition.AppRoles);
            Assert.Same(ClassicRoles.Table, R.Current);
            Assert.Same(ThemeHost.ActiveDefinition, Tok.Definition);   // the appearance seed installed the composition's definition
        });

    [Fact]
    public void A_definition_swap_reaches_the_next_read_and_bumps_both_epochs()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            int epoch = Tok.Epoch, style = Tok.StyleEpoch;
            try
            {
                Tok.Use(original with { AppRoles = Alt() }, kind);
                Assert.Equal(48f, R.Space.SectionGap);
                Assert.Equal(21f, R.Type.TrackTitle("x").Size);
                Assert.Equal(30f, R.Type.TrackTitle("x").LineHeight);
                Assert.Equal(1.1f, R.Motion.ScaleSubtle.HoverTarget);
                Assert.Equal((epoch + 1, style + 1), (Tok.Epoch, Tok.StyleEpoch));   // a non-colour role: applied as a unit

                Tok.Use(original, kind);
                Assert.Same(ClassicRoles.Table, R.Current);
                Assert.Equal(32f, R.Space.SectionGap);
                Assert.Equal(14f, R.Type.TrackTitle("x").Size);
                Assert.Equal(1.02f, R.Motion.ScaleSubtle.HoverTarget);
                Assert.Equal((epoch + 2, style + 2), (Tok.Epoch, Tok.StyleEpoch));
            }
            finally { Restore(original, kind); }
        });

    [Fact]
    public void An_equal_valued_table_is_adopted_without_an_epoch()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            int epoch = Tok.Epoch, style = Tok.StyleEpoch;
            try
            {
                var copy = original with { AppRoles = ClassicRoles.Table with { Space = ClassicRoles.Table.Space with { } } };
                Tok.Use(copy, kind);   // a different instance, equal by value: nothing changed
                Assert.Same(copy, Tok.Definition);
                Assert.Equal((epoch, style), (Tok.Epoch, Tok.StyleEpoch));
            }
            finally { Restore(original, kind); }
        });

    [Fact]
    public void A_definition_that_carries_no_roles_reads_the_process_baseline()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            try
            {
                Tok.Use(original with { AppRoles = null }, kind);
                Assert.Null(Tok.Definition.AppRoles);
                Assert.Same(ClassicRoles.Table, R.Current);
                Assert.Equal(32f, R.Space.SectionGap);
            }
            finally { Restore(original, kind); }
        });

    [Fact]
    public void A_light_dark_flip_keeps_the_roles_of_the_active_theme()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            ThemeKind other = kind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark;
            int style = Tok.StyleEpoch;
            try
            {
                Design.SeedPalette(other);   // what every light/dark verb calls
                Assert.Equal(other, Tok.Theme);
                Assert.Same(ClassicRoles.Table, Tok.Definition.AppRoles);
                Assert.Same(ThemeHost.ActiveDefinition, Tok.Definition);
                Assert.Equal(style, Tok.StyleEpoch);   // a colour-only change: the roles did not move
            }
            finally { Restore(original, kind); }
        });

    static void Restore(ThemeDefinition definition, ThemeKind kind)
    {
        if (!ReferenceEquals(Tok.Definition, definition) || Tok.Theme != kind) Tok.Use(definition, kind);
    }

    // ── a mounted view ───────────────────────────────────────────────────────────────────────────────────────────────────

    sealed record ProbeProps(string Name);

    static readonly ViewKey<ProbeProps> Probe = new("Roles.Probe");

    /// <summary>One view: a keyed box as wide as the TrackTitle role's size and as high as the section gap.</summary>
    sealed class ProbeStructure : IStructureLayer
    {
        public string Id => "Roles.Structure";
        public ThemeOptionSchema Options => ThemeOptionSchema.Empty;
        public RoleSet RequiredRoles => RoleSet.None;

        public void Register(ViewRegistryBuilder views)
            => views.Add(Probe, static (ProbeProps props, in ViewContext ctx) =>
                new BoxEl { Key = "roles:" + props.Name, Width = R.Type.TrackTitle("x").Size, Height = R.Space.SectionGap }, "Roles.Probe.Template");

        public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ThemePolicies.Default;
        public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;
    }

    /// <summary>A style that is the process definition carrying the given roles.</summary>
    sealed class ProbeStyle(WaveeStyleRoles roles) : IStyleLayer
    {
        readonly ThemeDefinition _definition = Tok.Definition with { Id = "roles-probe", AppRoles = roles };

        public string Id => "Roles.Style";
        public ThemeOptionSchema Options => ThemeOptionSchema.Empty;
        public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => _definition;
        public StyleMetrics Metrics(OptionValues style) => StyleMetrics.Unset;
    }

    static ThemeComposition Probed(WaveeStyleRoles roles) => new(
        new ThemeManifest("roles-probe", "roles.name", ContractVersion.Major, ContractVersion.Minor, "Roles.Structure", "Roles.Style", null,
            ThemeAvailability.Developer, new ThemePreview("l.png", "d.png")),
        new ProbeStructure(), new ProbeStyle(roles));

    [Fact]
    public void A_view_mounted_under_a_theme_reads_that_themes_roles()
    {
        ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            var rect = harness.AbsoluteBounds(harness.Node("roles:a"));
            Assert.Equal(14f, rect.W, 0.5f);
            Assert.Equal(32f, rect.H, 0.5f);
        }, new ViewHarnessOptions { Composition = Probed(ClassicRoles.Table) });

        ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            var rect = harness.AbsoluteBounds(harness.Node("roles:a"));
            Assert.Equal(21f, rect.W, 0.5f);
            Assert.Equal(48f, rect.H, 0.5f);
        }, new ViewHarnessOptions { Composition = Probed(Alt()) });
    }

    [Fact]
    public void A_mounted_view_re_renders_with_the_new_roles_when_the_definition_changes()
        => ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            var before = harness.AbsoluteBounds(harness.Node("roles:a"));
            Assert.Equal(14f, before.W, 0.5f);
            Assert.Equal(32f, before.H, 0.5f);

            int style = Tok.StyleEpoch;
            Tok.Use(Tok.Definition with { AppRoles = Alt() }, Tok.Theme);   // the harness puts the original back on dispose
            Assert.Equal(style + 1, Tok.StyleEpoch);
            harness.RunFrames(3);

            var after = harness.AbsoluteBounds(harness.Node("roles:a"));
            Assert.Equal(21f, after.W, 0.5f);
            Assert.Equal(48f, after.H, 0.5f);
        }, new ViewHarnessOptions { Composition = Probed(ClassicRoles.Table) });
}
