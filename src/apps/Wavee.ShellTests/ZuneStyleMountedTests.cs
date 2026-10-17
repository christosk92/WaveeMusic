// ── Wavee.ShellTests/ZuneStyleMountedTests.cs — the Zune style mounted over Classic's structure (themes plan 6.3, Z1) ──────────────
//
// The Zune structure is a parallel lane (Z0), so this file mounts Zune's STYLE over Classic's STRUCTURE with a test-only composition
// (`ZuneOverClassic`, the ClassicAlt pattern): every view is Classic's, every role, token, radius, control style and metric is Zune's. What
// only the real mounted frame can say:
//
//   switch       Classic -> Zune's style is not colour only, so it runs on the theme transaction (the window freezes); afterwards the
//                installed definition carries Zune's roles (lowercase words, square shapes), its control table and no window material
//   accent       a swatch pick is Live: no frame freezes, and the accent roles read the new swatch for the active kind
//   density      a row-size pick is a Transaction option: it freezes, applies in place (no remount) and republishes the metrics
//
// The page stays on Home: Classic's track-table density controls read Classic's own `style.density` key, which another style's option of
// the same id does not answer (the composition is a test fixture, not a theme anyone ships). The installed themes, `theme.id` and the
// test theme's persisted options are process state: every fact puts Classic back and forgets them.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Xunit;
using static Wavee.ShellTests.ShellProbe;
using WindowMaterial = FluentGpu.Pal.WindowMaterial;

namespace Wavee.ShellTests;

/// <summary>The test-only composition: Classic's structure with Zune's style. Never shipped.</summary>
public static class ZuneOverClassic
{
    public const string Id = "zune-over-classic";

    /// <summary>The composition. Install it next to Classic: <c>ThemeHost.Install(ClassicTheme.Create(), ZuneOverClassic.Create())</c>.</summary>
    public static ThemeComposition Create() => new(
        new ThemeManifest(
            Id: Id,
            DisplayNameKey: "theme.zune-over-classic.name",
            ContractMajor: ContractVersion.Major,
            ContractMinor: ContractVersion.Minor,
            StructureId: ClassicStructure.LayerId,
            StyleId: ZuneStyle.LayerId,
            InheritsFrom: null,
            Availability: ThemeAvailability.Public,
            Preview: new ThemePreview("themes/classic-light.png", "themes/classic-dark.png"),
            DescriptionKey: null),
        new ClassicStructure(),
        ZuneStyle.Create());

    /// <summary>Forgets the test theme's persisted options and puts Classic back alone, as active (the ClassicAlt pattern). UI thread.</summary>
    public static void RestoreClassic()
    {
        ThemeOptionStore.ResetOptions(Id);
        Platform.Settings.Set(new SettingKey<string>("theme.id", ClassicTheme.Id), ClassicTheme.Id);
        ThemeHost.Install(ClassicTheme.Create());
        Design.SeedPalette(Tok.Theme);
    }
}

public sealed class ZuneStyleMountedTests(ShellHost host)
{
    void WithZuneStyle(Action<Shell.HeadlessShell> body)
    {
        host.Run(static () => ThemeHost.Install(ClassicTheme.Create(), ZuneOverClassic.Create()));
        try { host.Mounted(body); }
        finally { host.Run(ZuneOverClassic.RestoreClassic); }
    }

    /// <summary>Runs frames until <paramref name="t"/> completes, noting whether any frame was frozen.</summary>
    static void Complete(Shell.HeadlessShell shell, Task t, out bool sawFreeze, int maxFrames = 300)
    {
        sawFreeze = false;
        for (int i = 0; i < maxFrames && !t.IsCompleted; i++)
        {
            shell.RunFrames(1);
            sawFreeze |= shell.Host.IsFrozen;
        }
        Assert.True(t.IsCompletedSuccessfully, "the change completed within " + maxFrames + " frames (freeze=" + shell.Host.FreezeState + ")");
        if (t is Task<ThemeTransactionResult> tx) Assert.Equal(ThemeTransactionOutcome.Committed, tx.Result.Outcome);
    }

    static void Drain(Shell.HeadlessShell shell)
    {
        for (int i = 0; i < 120 && shell.Host.FreezeState != FreezePhase.None; i++) shell.RunFrames(1);
        shell.RunFrames(2);
    }

    static void ToZune(Shell.HeadlessShell shell)
    {
        shell.RunFrames(10);
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        Until(shell, () => !shell.Find("home:facet:band").IsNull, "the home page");
        shell.RunFrames(20);
        Complete(shell, ThemeHost.Switch(ZuneOverClassic.Id), out bool froze);
        Drain(shell);
        Assert.True(froze, "Classic -> Zune's style is not colour only: it runs on the theme transaction");
    }

    [Fact]
    public void Switching_to_Zunes_style_installs_its_roles_controls_and_flat_ground()
        => WithZuneStyle(shell =>
        {
            ToZune(shell);
            var definition = Tok.Definition;
            Assert.Same(ZuneStyle.DefaultDefinition, definition);
            Assert.Same(ZuneControls.Table, definition.Controls);
            Assert.Equal(WindowMaterial.None, definition.Materials.For(Tok.Theme));
            foreach (var kind in Enum.GetValues<LabelKind>())
                Assert.Equal(TextCase.Lower, R.Case.For(kind));
            Assert.Equal(2f, R.Shape.Cover);
            Assert.Equal(0f, R.Shape.Card);
            Assert.Equal(0f, Radii.Control);
            var ground = Tok.Theme == ThemeKind.Light ? ZuneTokens.LightGround : ZuneTokens.DarkGround;
            Assert.Equal(ground, R.Layer.ShellGround);
            Assert.Equal(ground, Tok.FillSolidBase);
            Assert.Equal(ZuneStyle.MetricsFor(ZuneDensity.Standard), ThemeHost.Metrics.Peek());
            Assert.Equal(Shell.RouteKind.Home, Shell.Current.Peek().Kind);
        });

    [Fact]
    public void An_accent_pick_applies_live_without_a_freeze()
        => WithZuneStyle(shell =>
        {
            ToZune(shell);
            var t = ThemeHost.Apply(new OptionChange(ZuneOverClassic.Id, "style.accent", ZuneAccent.Blue.Dark));
            for (int i = 0; i < 30; i++)
            {
                Assert.False(shell.Host.IsThemeTransactionRunning, "a Live option started a theme transaction");
                Assert.Equal(FreezePhase.None, shell.Host.FreezeState);
                shell.RunFrames(1);
            }
            Assert.True(t.IsCompletedSuccessfully);
            var blue = Tok.Theme == ThemeKind.Light ? ZuneAccent.Blue.Light : ZuneAccent.Blue.Dark;
            Assert.Equal(blue, R.Ink.AccentAction);
            Assert.Equal(blue with { A = ZuneTokens.SelectedA }, R.Layer.SelectedRest);
            Assert.Equal(ZuneAccent.Blue.Dark, Tok.Definition.Accent.Light2);
            Assert.True(ZuneStyle.DefaultDefinition.DiffersOnlyInColours(Tok.Definition));
        });

    [Fact]
    public void A_row_size_pick_applies_in_place_on_a_transaction_and_republishes_the_metrics()
        => WithZuneStyle(shell =>
        {
            ToZune(shell);
            long epoch = ThemeHost.RootEpoch.Peek();
            var scope = RootScope.Current;
            Complete(shell, ThemeHost.Apply(new OptionChange(ZuneOverClassic.Id, "style.density", ZuneDensity.Comfortable)), out bool froze);
            Drain(shell);
            Assert.True(froze, "a Transaction-mode option freezes like a switch");
            Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());   // in place: no remount
            Assert.Same(scope, RootScope.Current);
            Assert.Equal(60f, ThemeHost.Metrics.Peek().RowSingle);
            Assert.Equal(ZuneStyle.MetricsFor(ZuneDensity.Comfortable), ThemeHost.Metrics.Peek());
            Assert.Same(ZuneStyle.DefaultDefinition, Tok.Definition);   // the row size moves no role
        });
}
