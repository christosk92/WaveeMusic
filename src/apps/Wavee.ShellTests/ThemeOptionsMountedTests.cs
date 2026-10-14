// ── Wavee.ShellTests/ThemeOptionsMountedTests.cs — Settings › Appearance and the wizard's Theme page, mounted (T1b) ──────
//
// What only a mounted page can say: the picker draws one card per entry of `ThemePickerFacts` and every card has ONE size
// (selected or not, short name or long, with or without a description), the options expander holds the active theme's
// rows in their groups (Classic: the legacy rows in their final grouping; a fake theme: the generated rows), a theme with
// nothing to configure keeps a quiet row, a row that appears or goes follows its condition, and a theme pick remounts the
// root without losing the page. The installed theme is process state, so every fact that installs fakes puts Classic back.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ThemeOptionsMountedTests(ShellHost host)
{
    const string TestVerb = "test.mounted.verb";

    enum Mood : byte { Calm = 0, Lively = 1, Wild = 2 }

    sealed class StubStructure(string id, ThemeOptionSchema options) : IStructureLayer
    {
        public string Id => id;
        public ThemeOptionSchema Options => options;
        public RoleSet RequiredRoles => RoleSet.None;
        // The frame is the structure's since T4a: a fake theme that mounts the real shell needs a frame to draw it, so the stub
        // registers Classic's views (the options under test are the stub's own).
        public void Register(ViewRegistryBuilder views) => new ClassicStructure().Register(views);
        public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ThemePolicies.Default;
        public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;
    }

    sealed class StubStyle(string id) : IStyleLayer
    {
        public string Id => id;
        public ThemeOptionSchema Options => ThemeOptionSchema.Empty;
        public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => Tok.Definition;   // the live instance: nothing to apply
        public StyleMetrics Metrics(OptionValues style) => StyleMetrics.Unset;
    }

    static ThemeComposition Fake(string id, ThemeOptionSchema? options = null, ThemeAvailability availability = ThemeAvailability.Public,
        string? nameKey = null, string? descriptionKey = null)
        => new(new ThemeManifest(id, nameKey ?? "theme." + id + ".name", ContractVersion.Major, ContractVersion.Minor,
                id + ".structure", id + ".style", null, availability, new ThemePreview("l.png", "d.png"), descriptionKey),
            new StubStructure(id + ".structure", options ?? ThemeOptionSchema.Empty), new StubStyle(id + ".style"));

    /// <summary>Installs <paramref name="themes"/> before the frame mounts (so the root's providers are theirs), mounts, runs
    /// <paramref name="body"/>, and puts Classic back whatever happens.</summary>
    void With(ThemeComposition[] themes, Action<Shell.HeadlessShell> body, float widthDip = 1180f, float heightDip = 760f)
    {
        host.Run(() => ThemeHost.Install(themes));
        try { host.Mounted(body, widthDip, heightDip); }
        finally { host.Run(RestoreClassic); }
    }

    static void RestoreClassic()
    {
        Platform.Settings.Set(new SettingKey<string>("theme.id", "classic"), "classic");
        ThemeHost.Install(ClassicTheme.Create());
        ThemeVerbs.Unregister(TestVerb);
    }

    static void OpenAppearance(Shell.HeadlessShell shell, int frames = 60)
    {
        Settings.Open(Settings.Tab.Appearance);
        shell.RunFrames(frames);
    }

    static RectF Size(Shell.HeadlessShell shell, string key) => shell.Host.Scene.Bounds(Node(shell, key));
    static RectF Abs(Shell.HeadlessShell shell, string key) => shell.Host.Scene.PresentedAbsoluteRect(Node(shell, key));

    // ── Classic ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_shows_one_picker_card_then_its_options_in_their_final_groups()
        => host.Mounted(shell =>
        {
            OpenAppearance(shell);

            var picker = Abs(shell, "settings:theme:picker");
            var card = Abs(shell, "settings:theme:card:classic");
            var options = Abs(shell, "settings:theme:options");
            Assert.Single(shell.FindPrefix("settings:theme:card:"));
            Assert.Equal(208f, card.W, 0.5f);
            Assert.Equal(172f, card.H, 0.5f);
            Assert.True(card.X >= picker.X && card.X + card.W <= picker.X + picker.W + 0.5f, "the card sits inside the picker");
            Assert.True(options.Y >= picker.Y + picker.H - 0.5f, "the options follow the picker");

            Assert.Equal(4, shell.FindPrefix("opt:group:").Count);
            foreach (string id in new[]
            {
                "sidebarReset", "sidebarCustomize", "sidebarResetEverything",
                "trackListStyle", "pageLayout", "railUniform", "railReset",
            })
                Assert.False(shell.Find("legacy:" + id).IsNull, "legacy row '" + id + "' is mounted");
            foreach (string id in new[] { "structure.sidebar", "structure.libraryDensity", "structure.hideTrackArt", "structure.surfaces", "style.density" })   // M7a, M4, M6 and M3: generated rows, no legacy row left
                Assert.False(shell.Find("opt:" + id).IsNull, "generated row '" + id + "' is mounted");
            foreach (string id in new[] { "colorWashes", "rowDensity", "hideTrackArtwork", "sidebarDesign", "sidebarDensity" })
                Assert.True(shell.Find("legacy:" + id).IsNull, "'" + id + "' is a generated option now");
        });

    [Fact]
    public void The_tab_keeps_the_boards_order_theme_options_app_mode_zoom()
        => host.Mounted(shell =>
        {
            OpenAppearance(shell);
            var picker = Abs(shell, "settings:theme:picker");
            var options = Abs(shell, "settings:theme:options");
            Assert.True(picker.Y < options.Y);
            // App mode and Zoom are the two rows right under the options card.
            var rows = shell.FindPrefix("appearance.zoom:");
            Assert.Single(rows);
            Assert.True(Abs(shell, rows[0].Key).Y > options.Y + options.H - 0.5f);
        });

    // ── the picker ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Several_themes_are_several_cards_of_one_size_whatever_their_text()
        => With(
            [
                Fake("alpha"),
                Fake("beta", descriptionKey: "theme.beta.description"),
                Fake("gamma", nameKey: "theme.gamma." + new string('x', 140), descriptionKey: "theme.gamma.description." + new string('y', 200)),
                Fake("lab", availability: ThemeAvailability.Developer),
            ],
            shell =>
            {
                OpenAppearance(shell);

                // The Developer theme waits for Developer mode; the three public ones are cards of exactly one size.
                Assert.True(shell.Find("settings:theme:card:lab").IsNull);
                var cards = new[] { "alpha", "beta", "gamma" }.Select(id => Size(shell, "settings:theme:card:" + id)).ToArray();
                Assert.All(cards, c => { Assert.Equal(208f, c.W, 0.5f); Assert.Equal(172f, c.H, 0.5f); });

                // One line while they fit...
                var ys = new[] { "alpha", "beta", "gamma" }.Select(id => Abs(shell, "settings:theme:card:" + id).Y).ToArray();
                Assert.All(ys, y => Assert.Equal(ys[0], y, 0.5f));
            }, widthDip: 1500f);

    [Fact]
    public void A_narrow_window_wraps_the_cards_instead_of_overflowing()
        => With([Fake("alpha"), Fake("beta"), Fake("gamma")], shell =>
        {
            OpenAppearance(shell);
            var picker = Abs(shell, "settings:theme:picker");
            foreach (string id in new[] { "alpha", "beta", "gamma" })
            {
                var card = Abs(shell, "settings:theme:card:" + id);
                Assert.True(card.X >= picker.X - 0.5f && card.X + card.W <= picker.X + picker.W + 0.5f, id + " stays inside the picker");
                Assert.Equal(172f, card.H, 0.5f);
            }
        }, widthDip: 520f, heightDip: 860f);

    [Fact]
    public void Moving_the_selection_does_not_move_a_pixel_of_any_card()
        => With([Fake("alpha"), Fake("beta")], shell =>
        {
            OpenAppearance(shell);
            var before = new[] { "alpha", "beta" }.Select(id => Abs(shell, "settings:theme:card:" + id)).ToArray();

            _ = ThemeHost.Switch("beta");   // the root remounts; the page is a fresh navigation
            shell.RunFrames(10);
            OpenAppearance(shell);

            Assert.Equal("beta", ThemePickerFacts.State.Peek().CurrentId);
            var after = new[] { "alpha", "beta" }.Select(id => Abs(shell, "settings:theme:card:" + id)).ToArray();
            for (int i = 0; i < before.Length; i++)
            {
                Assert.Equal(before[i].X, after[i].X, 0.5f);
                Assert.Equal(before[i].Y, after[i].Y, 0.5f);
                Assert.Equal(before[i].W, after[i].W, 0.5f);
                Assert.Equal(before[i].H, after[i].H, 0.5f);
            }
        });

    [Fact]
    public void Developer_mode_adds_the_developer_theme_card_live()
        => With([Fake("alpha"), Fake("lab", availability: ThemeAvailability.Developer)], shell =>
        {
            OpenAppearance(shell);
            Assert.True(shell.Find("settings:theme:card:lab").IsNull);

            Platform.Developer.Enabled.Value = true;
            try
            {
                shell.RunFrames(10);
                Assert.False(shell.Find("settings:theme:card:lab").IsNull);
                Assert.Equal(172f, Size(shell, "settings:theme:card:lab").H, 0.5f);
            }
            finally { Platform.Developer.Enabled.Value = false; }
            shell.RunFrames(10);
            Assert.True(shell.Find("settings:theme:card:lab").IsNull);
        });

    // ── the options ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_theme_with_nothing_to_configure_keeps_one_quiet_row()
        => With([Fake("bare")], shell =>
        {
            OpenAppearance(shell);
            Assert.False(shell.Find("settings:theme:options").IsNull);
            Assert.Empty(shell.FindPrefix("opt:"));
            Assert.True(Size(shell, "settings:theme:options").H >= 60f, "the row keeps the height of a settings card");
        });

    [Fact]
    public void Generated_rows_are_drawn_for_every_kind_inside_their_group()
    {
        ThemeVerbs.Register(TestVerb, static () => true);
        var schema = ThemeOptionSchema.Of(
            new ToggleOption("tabs", "t.tabs", "t.tabs.sub", "grp.one", true),
            new SelectOption<Mood>("mood", "t.mood", null, "grp.one", Mood.Calm,
                ImmutableArray.Create(new SelectChoice<Mood>(Mood.Calm, "c.calm"), new SelectChoice<Mood>(Mood.Lively, "c.lively"), new SelectChoice<Mood>(Mood.Wild, "c.wild")),
                PickerStyle.Cards),
            new SelectOption<Mood>("combo", "t.combo", null, "grp.two", Mood.Calm,
                ImmutableArray.Create(new SelectChoice<Mood>(Mood.Calm, "c.calm"), new SelectChoice<Mood>(Mood.Wild, "c.wild")), PickerStyle.Combo),
            new SliderOption("scale", "t.scale", null, "grp.two", 1f, 0.5f, 2f, 0.25f, null),
            new ColorOption("tint", "t.tint", null, "grp.two", ColorF.FromRgba(255, 0, 0),
                ImmutableArray.Create(ColorF.FromRgba(255, 0, 0), ColorF.FromRgba(0, 255, 0))),
            new ActionOption("clear", "t.clear", "grp.two", TestVerb),
            new PrefReferenceOption("zoomRef", "grp.two", CorePref.Zoom));

        With([Fake("rich", schema)], shell =>
        {
            OpenAppearance(shell);

            Assert.Equal(2, shell.FindPrefix("opt:group:").Count);
            foreach (string key in new[]
            {
                "opt:structure.tabs", "opt:structure.mood", "opt:structure.combo", "opt:structure.scale", "opt:structure.tint",
                "opt:structure.clear", "opt:structure.zoomRef", "opt:reset",
            })
                Assert.False(shell.Find(key).IsNull, key + " is mounted");
            Assert.Equal(3, shell.FindPrefix("opt:card:structure.mood:").Count);   // one card per choice, one size
            var cards = shell.FindPrefix("opt:card:structure.mood:").Select(c => shell.Host.Scene.Bounds(c.Node)).ToArray();
            Assert.All(cards, c => { Assert.Equal(116f, c.W, 0.5f); Assert.Equal(56f, c.H, 0.5f); });
        });
    }

    [Fact]
    public void A_conditional_row_comes_and_goes_with_its_condition_and_the_group_follows()
    {
        var schema = ThemeOptionSchema.Of(
            new ToggleOption("tabs", "t.tabs", null, "grp", true),
            new ToggleOption("extra", "t.extra", null, "grp", false) with { VisibleWhen = new OptionCondition("tabs", true) });

        With([Fake("cond", schema)], shell =>
        {
            OpenAppearance(shell);
            Assert.False(shell.Find("opt:structure.extra").IsNull);
            float open = Size(shell, "opt:group:grp").H;

            _ = ThemeHost.Apply(new OptionChange("cond", "structure.tabs", false));
            shell.RunFrames(90);   // the group's height tween settles
            Assert.True(shell.Find("opt:structure.extra").IsNull, "the row left with its condition");
            Assert.True(Size(shell, "opt:group:grp").H < open, "the group gave the row's height back");

            _ = ThemeHost.Apply(new OptionChange("cond", "structure.tabs", true));
            shell.RunFrames(90);
            Assert.False(shell.Find("opt:structure.extra").IsNull);
            Assert.Equal(open, Size(shell, "opt:group:grp").H, 0.5f);
        });
    }

    [Fact]
    public void A_write_from_outside_the_page_reaches_the_rows()
    {
        var schema = ThemeOptionSchema.Of(new ToggleOption("tabs", "t.tabs", null, "grp", true));
        With([Fake("live", schema)], shell =>
        {
            OpenAppearance(shell);
            Assert.Equal(true, ThemeOptionsSection.Current(false).Rows.Single().Current);

            _ = ThemeHost.Apply(new OptionChange("live", "structure.tabs", false));   // the palette's path
            shell.RunFrames(5);

            Assert.False(shell.Find("opt:structure.tabs").IsNull);
            Assert.Equal(false, ThemeOptionsSection.Current(false).Rows.Single().Current);
            ThemeHost.ResetOptions("live");
            shell.RunFrames(5);
            Assert.Equal(true, ThemeOptionsSection.Current(false).Rows.Single().Current);
        });
    }

    // ── the wizard's Theme page ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_wizard_theme_page_is_the_picker_and_the_options_on_the_plate_frame()
        => host.Run(() =>
        {
            using var shell = Shell.Headless.Mount(static () => Slots.View(ViewKeys.SetupTheme, Setup.ThemeProps) with { Key = "setup:page:theme" }, 640f, 700f);
            shell.RunFrames(20);

            Assert.False(shell.Find("setup:frame:" + (int)Setup.WizardPage.Theme).IsNull);   // the Layout page's frame, kept
            Assert.Single(shell.FindPrefix("settings:theme:card:"));
            Assert.False(shell.Find("opt:structure.sidebar").IsNull);                         // the layout cards: what the Layout step showed, now Classic's first option
            Assert.True(shell.Find("legacy:trackListStyle").IsNull);                          // a legacy row that is not a first-run decision
            ShellHost.AssertNoNetwork();
        });
}
