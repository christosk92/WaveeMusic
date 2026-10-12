// ── Wavee.ShellTests/ViewHarnessTests.cs — the harness itself: a view key, a composition, options, a window size ──────
//
// The harness is the tool every contract and probe test of the theme work stands on, so it is pinned once, with a FAKE
// structure whose single view reads an option and the facts: mounted under a composition it builds that composition's
// registry, options and facts around the view; under the installed theme it uses the shell's own providers; it puts the
// process tokens back; and a key nobody registered renders nothing instead of failing the mount.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ViewHarnessTests(ShellHost host)
{
    sealed record ProbeProps(string Name);

    static readonly ViewKey<ProbeProps> Probe = new("Harness.Probe");
    static readonly ViewKey<ProbeProps> Unregistered = new("Harness.Missing");

    static readonly ToggleOption Wide = new("wide", "t.wide", null, "g", false);
    static readonly SelectOption<Pins> PinOption = new("pins", "t.pins", null, "g", Pins.Few,
        ImmutableArray.Create(new SelectChoice<Pins>(Pins.Few, "few"), new SelectChoice<Pins>(Pins.Many, "many")), PickerStyle.Radio);

    enum Pins { Few = 2, Many = 9 }

    /// <summary>One view: a keyed box 100 wide (300 with the option), as high as the pin capability says (x 10).</summary>
    sealed class HarnessStructure : IStructureLayer
    {
        public string Id => "Harness.Structure";
        public ThemeOptionSchema Options => ThemeOptionSchema.Of(Wide, PinOption);
        public RoleSet RequiredRoles => RoleSet.None;

        public void Register(ViewRegistryBuilder views)
            => views.Add(Probe, static (ProbeProps props, in ViewContext ctx) =>
            {
                bool wide = ctx.Options.Get(Wide.Key);
                var facts = ctx.Hooks.UseContext(ThemeFactsContext.Signal)!.Value;
                return new BoxEl { Key = "harness:" + props.Name, Width = wide ? 300f : 100f, Height = facts.Capabilities.MaxBandPins * 10f };
            }, "Harness.Probe.Template");

        public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ThemePolicies.Default;

        public ShellCapabilities Capabilities(OptionValues structure)
            => ShellCapabilities.Unrestricted with { MaxBandPins = (int)structure.Get(PinOption.Key) };
    }

    sealed class HarnessStyle : IStyleLayer
    {
        public static readonly ThemeDefinition Own = Tok.Definition with { Id = "harness-style" };
        public string Id => "Harness.Style";
        public ThemeOptionSchema Options => ThemeOptionSchema.Empty;
        public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => Own;
        public StyleMetrics Metrics(OptionValues style) => StyleMetrics.Unset;
    }

    static ThemeComposition Harnessed() => new(
        new ThemeManifest("harness", "harness.name", 1, 0, "Harness.Structure", "Harness.Style", null, ThemeAvailability.Developer,
            new ThemePreview("l.png", "d.png")),
        new HarnessStructure(), new HarnessStyle());

    // ── under a composition ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_view_key_mounts_under_a_composition_with_the_registered_template()
        => ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            var node = harness.Node("harness:a");
            var rect = harness.AbsoluteBounds(node);
            Assert.Equal(100f, rect.W, 0.5f);
            Assert.Equal(20f, rect.H, 0.5f);   // Pins.Few = 2 -> 2 * 10
        }, new ViewHarnessOptions { Composition = Harnessed() });

    [Fact]
    public void Option_values_given_at_mount_reach_the_template_and_the_facts()
        => ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            var rect = harness.AbsoluteBounds(harness.Node("harness:a"));
            Assert.Equal(300f, rect.W, 0.5f);
            Assert.Equal(90f, rect.H, 0.5f);
            Assert.Equal(9, harness.Facts.Peek().Capabilities.MaxBandPins);
            Assert.True(harness.Values.Structure.Get(Wide.Key));
        }, new ViewHarnessOptions
        {
            Composition = Harnessed(),
            Options = new Dictionary<string, object> { ["structure.wide"] = true, ["structure.pins"] = Pins.Many },
        });

    [Fact]
    public void SetOption_rerenders_the_view_and_republishes_the_facts()
        => ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            Assert.Equal(100f, harness.AbsoluteBounds(harness.Node("harness:a")).W, 0.5f);

            harness.SetOption("structure.wide", true);
            harness.SetOption("structure.pins", Pins.Many);

            var rect = harness.AbsoluteBounds(harness.Node("harness:a"));
            Assert.Equal(300f, rect.W, 0.5f);
            Assert.Equal(90f, rect.H, 0.5f);
        }, new ViewHarnessOptions { Composition = Harnessed() });

    [Fact]
    public void An_unknown_hidden_or_invalid_option_is_refused_at_mount()
        => host.Run(() =>
        {
            ViewHarnessOptions Requesting(string id, object value) => new() { Composition = Harnessed(), Options = new Dictionary<string, object> { [id] = value } };

            Assert.Throws<ArgumentException>(() => ViewHarness.Mount(Probe, new ProbeProps("a"), Requesting("structure.nope", true)));
            Assert.Throws<ArgumentException>(() => ViewHarness.Mount(Probe, new ProbeProps("a"), Requesting("style.wide", true)));
            Assert.Throws<ArgumentException>(() => ViewHarness.Mount(Probe, new ProbeProps("a"), Requesting("structure.wide", "yes")));
            Assert.Throws<ArgumentException>(() => ViewHarness.Mount(Probe, new ProbeProps("a"), Requesting("structure.pins", (Pins)5)));
        });

    [Fact]
    public void The_window_size_is_the_options_size_times_the_scale_and_resizes()
        => ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            Assert.Equal(1280f, harness.Shell.Window.ClientSizePx.Width, 0.5f);   // 640 DIP at scale 2
            Assert.Equal(720f, harness.Shell.Window.ClientSizePx.Height, 0.5f);

            harness.Resize(800f, 400f);
            harness.RunFrames(2);
            Assert.Equal(1600f, harness.Shell.Window.ClientSizePx.Width, 0.5f);
            Assert.True(harness.Contains("harness:a"));
        }, new ViewHarnessOptions { Composition = Harnessed(), WidthDip = 640f, HeightDip = 360f, Scale = 2f });

    [Fact]
    public void The_process_tokens_are_the_compositions_while_mounted_and_come_back_on_dispose()
        => host.Run(() =>
        {
            var before = Tok.Definition;
            using (ViewHarness.Mount(Probe, new ProbeProps("a"), new ViewHarnessOptions { Composition = Harnessed() }))
                Assert.Same(HarnessStyle.Own, Tok.Definition);
            Assert.Same(before, Tok.Definition);
        });

    [Fact]
    public void The_kind_is_applied_for_the_mount_and_restored()
        => host.Run(() =>
        {
            var before = Tok.Theme;
            var other = before == ThemeKind.Light ? ThemeKind.Dark : ThemeKind.Light;
            using (ViewHarness.Mount(Probe, new ProbeProps("a"), new ViewHarnessOptions { Kind = other }))
                Assert.Equal(other, Tok.Theme);
            Assert.Equal(before, Tok.Theme);
        });

    [Fact]
    public void A_composition_harness_never_touches_the_installed_theme_or_the_profile()
        => host.Run(() =>
        {
            var installedFacts = Wavee.Theming.ThemeHost.Facts.Peek();
            long epoch = Wavee.Theming.ThemeHost.RootEpoch.Peek();
            uint writes = Platform.SettingsEpoch;
            using (var harness = ViewHarness.Mount(Probe, new ProbeProps("a"), new ViewHarnessOptions { Composition = Harnessed() }))
                harness.SetOption("structure.wide", true);
            Assert.Same(installedFacts, Wavee.Theming.ThemeHost.Facts.Peek());
            Assert.Equal(epoch, Wavee.Theming.ThemeHost.RootEpoch.Peek());
            Assert.Equal(writes, Platform.SettingsEpoch);
            Assert.False(Platform.Settings.Has("theme.harness.structure.wide"));
        });

    // ── under the installed theme, and the real frame ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_key_nobody_registered_renders_nothing_and_does_not_fail_the_mount()
        => ViewHarness.With(host, Unregistered, new ProbeProps("ghost"), harness =>
        {
            Assert.False(harness.Contains("harness:ghost"));
            Assert.Null(harness.Composition);
        });

    [Fact]
    public void Under_the_installed_theme_the_view_resolves_through_the_shells_own_providers()
        => ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            // Classic registers no view yet, so the key is absent from the installed registry: nothing renders, nothing throws.
            Assert.False(Wavee.Theming.ThemeHost.Registry.Contains("Harness.Probe"));
            Assert.False(harness.Contains("harness:a"));
            Assert.Same(Wavee.Theming.ThemeHost.Facts, harness.Facts);
        });

    [Fact]
    public void The_real_frame_mounts_at_the_options_size()
        => host.Run(() =>
        {
            using var shell = ViewHarness.MountFrame(new ViewHarnessOptions { WidthDip = 900f, HeightDip = 600f });
            shell.RunFrames(10);
            Assert.NotEmpty(shell.FindPrefix("sidebar."));
            ShellHost.AssertNoNetwork();
        });

    [Fact]
    public void Mounting_twice_without_disposing_is_refused()
        => host.Run(() =>
        {
            using var first = ViewHarness.Mount(Probe, new ProbeProps("a"), new ViewHarnessOptions { Composition = Harnessed() });
            Assert.Throws<InvalidOperationException>(() => ViewHarness.Mount(Probe, new ProbeProps("b"), new ViewHarnessOptions { Composition = Harnessed() }));
            Assert.Same(HarnessStyle.Own, Tok.Definition);   // and the refused mount left the tokens as the first one set them
        });
}
