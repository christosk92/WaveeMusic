// ── Wavee.ShellTests/LiveOptionBehaviourTests.cs — every Live option keeps its production mechanism ────────────────
//
// The Live-option pins (themes plan §2.1, LP1), written before any view moves into a theme: per option, change it through
// TODAY's writer on the real mounted frame and assert the mechanism production uses, on the nodes production keys. A
// later PR that moves a surface keeps this file green; an M-PR re-points the write from the legacy key to the theme
// option, and the assertions stay.
//
//   stage layout    the popover writer and `[` / `]` → the `stage:cover` node's Bounds FLIP spring runs (the keys
//                   step the face instead wherever the layout keeps its cover)
//   sidebar layout  the pane remounts under the other mode key (`sidebar.classic` ⇄ `sidebar.library`) with its fade
//   sidebar density the library rows change extent in place (no remount)
//   row style       the track list re-keys (`list:<scroll>:d<N>:classic|modern…`)
//   row density     the same list key's `d<N>` segment
//   page layout     the detail page swaps `detail:two-column` ⇄ `detail:vertical`
//   washes          home's `shell.material.tint` fades its brush in place to the neutral ground and the next
//                   page tints again; the artwork-keyed `shell.wash.*` legs (the recents page) leave, and enter fading
//   hide art        the rows of the keyed list lose their artwork child
//   lyrics blur     the strength reaches the rail's lines (blurred, or none at 0); the stage's lyrics never blur
//   marquee         the player-bar title (`np-title`) hosts the marquee or a plain clipped line
//
// Options without a key of their own are asserted under the nearest keyed ancestor: adding a key would change node
// matching, which is production behaviour. Every fact puts its option back, because the settings live for the process.

using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Roles;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class LiveOptionBehaviourTests(ShellHost host)
{
    // ── the stage ─────────────────────────────────────────────────────────────────────────────────────────────────

    static void OpenStage(Shell.HeadlessShell shell, Stage.Mode mode, Stage.VizLayout layout)
    {
        Prefs.Stage.SetMode((int)mode);
        Prefs.Stage.SetLayout((int)layout);
        Stage.Open(null, "test");
        Until(shell, () => !shell.Find("stage:cover").IsNull, "the stage's cover mounted");
        shell.RunFrames(120);   // the entrance and every spring settle
    }

    static void CloseStage(Shell.HeadlessShell shell)
    {
        Stage.Close(null, "test");
        shell.RunFrames(30);
        Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
        Prefs.Stage.SetLayout(Platform.Keys.StageLayout.Default);
    }

    [Fact]
    public void Stage_layout_from_the_popover_morphs_the_cover_with_its_bounds_spring()
        => host.Mounted(shell =>
        {
            OpenStage(shell, Stage.Mode.Visualizer, Stage.VizLayout.Card);
            try
            {
                var cover = Node(shell, "stage:cover");
                Assert.True(shell.Host.Animation.TryGetTransition(cover, out LayoutTransition flip), "stage:cover declares no layout transition");
                Assert.Equal(TransitionChannels.Bounds, flip.Channels);
                Assert.False(shell.Host.Animation.HasTracks(cover), "the cover was still moving before the change");

                Prefs.Stage.SetLayout((int)Stage.VizLayout.LargeArt);   // the options popover's writer (Stage.Options.UI.cs)
                shell.RunFrames(2);
                cover = Node(shell, "stage:cover");
                Assert.True(shell.Host.Animation.HasTracks(cover), "Card → Large art snapped the cover instead of springing it");
            }
            finally { CloseStage(shell); }
        });

    /// <summary>The stage's key map lives on its root box (`OnKeyDown`), but the open moves focus to the component's
    /// host node, ABOVE that box, where a key never reaches it: the keys work once focus is inside the stage, which is
    /// where a click on the stage leaves it. The fact puts focus there instead of pinning the open's focus target.</summary>
    static void FocusStage(Shell.HeadlessShell shell)
    {
        shell.Host.Input.SetFocus(shell.Host.Scene.Parent(Node(shell, "stage:backdrop")));
        shell.RunFrames(1);
    }

    [Fact]
    public void Stage_keys_step_the_artist_layout_with_the_cover_spring_and_the_face_elsewhere()
        => host.Mounted(shell =>
        {
            Play("spotify:track:tr40");   // the Artist layout steps layouts only with a header to draw (an artist playing)
            shell.RunFrames(30);
            OpenStage(shell, Stage.Mode.Visualizer, Stage.VizLayout.Artist);
            try
            {
                FocusStage(shell);
                Assert.False(shell.Host.Animation.HasTracks(Node(shell, "stage:cover")));
                Key(shell, Stage.ModeRules.NextFaceKey);
                shell.RunFrames(2);
                Assert.Equal((int)Stage.LayoutRules.Step(Stage.VizLayout.Artist, 1), Platform.Settings.Get(Platform.Keys.StageLayout));
                Assert.True(shell.Host.Animation.HasTracks(Node(shell, "stage:cover")), "] snapped the cover instead of springing it");

                // In Large art the same key steps the FACE behind the cover; the layout stays.
                shell.RunFrames(120);
                int face = Platform.Settings.Get(Platform.Keys.StageVisualizer);
                int layout = Platform.Settings.Get(Platform.Keys.StageLayout);
                Key(shell, Stage.ModeRules.NextFaceKey);
                shell.RunFrames(2);
                Assert.Equal(layout, Platform.Settings.Get(Platform.Keys.StageLayout));
                Assert.Equal((int)Visualizer.Catalog.Step((Visualizer.Kind)face, 1), Platform.Settings.Get(Platform.Keys.StageVisualizer));
            }
            finally
            {
                CloseStage(shell);
                Prefs.Stage.SetVisualizer(Platform.Keys.StageVisualizer.Default);
            }
        });

    // ── the sidebar ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sidebar_layout_remounts_the_pane_under_the_other_mode_key_with_its_fade()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            var before = Sidebar.Layout.Peek();
            var other = before == SidebarLayoutId.Library ? SidebarLayoutId.Classic : SidebarLayoutId.Library;
            string beforeKey = before == SidebarLayoutId.Library ? "sidebar.library" : "sidebar.classic";
            string otherKey = other == SidebarLayoutId.Library ? "sidebar.library" : "sidebar.classic";
            Node(shell, beforeKey);
            try
            {
                Sidebar.SwitchLayout(other);
                shell.RunFrames(1);
                var mode = Node(shell, otherKey);
                Assert.True(shell.Host.Animation.HasTracks(mode), otherKey + " mounted without its entrance fade");
                shell.RunFrames(60);
                Assert.True(shell.Find(beforeKey).IsNull, beforeKey + " is still mounted");
            }
            finally
            {
                Sidebar.SwitchLayout(before);
                shell.RunFrames(60);
            }
        });

    [Fact]
    public void The_sidebar_options_drive_the_pane_like_the_verbs_do_and_a_verb_stores_the_option()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            var layout = Sidebar.Layout.Peek();
            var density = Sidebar.Density.Peek();
            var other = layout == SidebarLayoutId.Library ? SidebarLayoutId.Classic : SidebarLayoutId.Library;
            string otherKey = other == SidebarLayoutId.Library ? "sidebar.library" : "sidebar.classic";
            try
            {
                // Settings' cards, the palette's option commands and a theme switch write the option; the frame's effect brings the sidebar after it.
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.SidebarOptionId, other));
                shell.RunFrames(2);
                Assert.Equal(other, Sidebar.Layout.Peek());
                Assert.Equal(other, ShellPolicy.Now.NavPane.Layout);
                Node(shell, otherKey);

                // A verb of the sidebar (the pane's menu) stores the choice in the option.
                Sidebar.SetDensity(SidebarDensity.Compact);
                Assert.Equal(SidebarDensity.Compact, ThemeHost.Values.Structure.Get(ClassicStructure.LibraryDensity.Key));
                Assert.Equal(SidebarDensity.Compact, ShellPolicy.Now.NavPane.Density);
                Sidebar.SwitchLayout(layout);
                Assert.Equal(layout, ThemeHost.Values.Structure.Get(ClassicStructure.Sidebar.Key));

                // The density option moved without a verb: the pane follows.
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.LibraryDensityOptionId, SidebarDensity.Default));
                shell.RunFrames(2);
                Assert.Equal(SidebarDensity.Default, Sidebar.Density.Peek());
            }
            finally
            {
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.SidebarOptionId, layout));
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.LibraryDensityOptionId, density));
                shell.RunFrames(60);
            }
        });

    [Fact]
    public void A_layout_option_change_while_the_sidebar_is_edited_ends_the_edit_first()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            var layout = Sidebar.Layout.Peek();
            var other = layout == SidebarLayoutId.Library ? SidebarLayoutId.Classic : SidebarLayoutId.Library;
            try
            {
                Sidebar.EnterEdit();
                shell.RunFrames(2);
                Assert.True(Sidebar.Editing.Peek());
                Sidebar.SwitchLayout(other);                    // the sidebar's own verb refuses while editing
                Assert.Equal(layout, Sidebar.Layout.Peek());

                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.SidebarOptionId, other));   // a command-palette row does not
                shell.RunFrames(2);
                Assert.False(Sidebar.Editing.Peek());
                Assert.Equal(other, Sidebar.Layout.Peek());
            }
            finally
            {
                Sidebar.ExitEdit();
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.SidebarOptionId, layout));
                shell.RunFrames(60);
            }
        });

    [Fact]
    public void Sidebar_density_changes_the_library_rows_extent_in_place()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            string modeKey = Sidebar.Layout.Peek() == SidebarLayoutId.Library ? "sidebar.library" : "sidebar.classic";
            var before = Sidebar.Density.Peek();
            var other = before == SidebarDensity.Compact ? SidebarDensity.Default : SidebarDensity.Compact;
            var mode = Node(shell, modeKey);
            var list = ScrollUnder(shell, mode);
            Assert.False(list.IsNull, "the sidebar has no scrolling list");
            Assert.True(shell.Host.Scene.TryGetScroll(list, out var s0));
            try
            {
                Sidebar.SetDensity(other);
                shell.RunFrames(30);
                Assert.Equal(mode, Node(shell, modeKey));   // the same pane: density never remounts it
                Assert.True(shell.Host.Scene.TryGetScroll(list, out var s1));
                if (other == SidebarDensity.Compact) Assert.True(s1.ContentH < s0.ContentH, $"compact rows did not shrink: {s0.ContentH} → {s1.ContentH}");
                else Assert.True(s1.ContentH > s0.ContentH, $"default rows did not grow: {s0.ContentH} → {s1.ContentH}");
            }
            finally
            {
                Sidebar.SetDensity(before);
                shell.RunFrames(30);
            }
        });

    // ── the track list ────────────────────────────────────────────────────────────────────────────────────────────

    static (NodeHandle Node, string Key) LikedList(Shell.HeadlessShell shell)
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
        Until(shell, () => shell.FindPrefix("list:").Count == 1, "the liked list mounted");
        shell.RunFrames(60);
        return Single(shell, "list:");
    }

    [Fact]
    public void Row_style_re_keys_the_track_list()
        => host.Mounted(shell =>
        {
            int before = Platform.Settings.Get(Platform.Keys.TrackRowStyle);
            var (_, key) = LikedList(shell);
            Assert.EndsWith(before == 1 ? "classic" : "modern", SkinSegment(key));
            try
            {
                Prefs.Appearance.Set(Platform.Keys.TrackRowStyle, before == 1 ? 0 : 1);   // Settings › Appearance › Track rows
                shell.RunFrames(30);
                var (_, next) = Single(shell, "list:");
                Assert.NotEqual(key, next);
                Assert.EndsWith(before == 1 ? "modern" : "classic", SkinSegment(next));
            }
            finally
            {
                Prefs.Appearance.Set(Platform.Keys.TrackRowStyle, before);
                shell.RunFrames(30);
            }
        });

    [Fact]
    public void Row_density_re_keys_the_track_list()
        => host.Mounted(shell =>
        {
            int before = Platform.Settings.Get(Platform.Keys.RowDensity);
            int other = before == 0 ? 2 : 0;
            var (_, key) = LikedList(shell);
            Assert.Contains(":d" + before + ":", key);
            try
            {
                Prefs.Appearance.Set(Platform.Keys.RowDensity, other);   // Settings › Appearance › Density
                shell.RunFrames(30);
                var (_, next) = Single(shell, "list:");
                Assert.Contains(":d" + other + ":", next);
            }
            finally
            {
                Prefs.Appearance.Set(Platform.Keys.RowDensity, before);
                shell.RunFrames(30);
            }
        });

    /// <summary>The skin segment of a list key: `list:&lt;scroll&gt;:[vh:]d&lt;N&gt;:classic|modern…` (Track.Table.cs).</summary>
    static string SkinSegment(string key)
    {
        string[] parts = key.Split(':');
        int d = Array.FindIndex(parts, p => p.Length > 1 && p[0] == 'd' && char.IsAsciiDigit(p[1]));
        Assert.True(d >= 0 && d + 1 < parts.Length, "no density segment in " + key);
        return parts[d + 1];
    }

    [Fact]
    public void Hide_art_removes_the_artwork_from_the_rows_of_the_keyed_list()
        => host.Mounted(shell =>
        {
            bool before = Platform.Settings.Get(Platform.Keys.HideTrackArtwork);
            Prefs.Appearance.Set(Platform.Keys.HideTrackArtwork, false);
            try
            {
                var (shownList, shownKey) = LikedList(shell);
                int shown = CountUnder(shell, shownList);
                Prefs.Appearance.Set(Platform.Keys.HideTrackArtwork, true);   // Settings › Appearance › Track artwork
                shell.RunFrames(30);
                var (hiddenList, hiddenKey) = Single(shell, "list:");
                int hidden = CountUnder(shell, hiddenList);
                Assert.Equal(shownKey, hiddenKey);   // the art leaves the rows; the list itself is not re-keyed
                Assert.True(hidden < shown, $"the rows kept their artwork: {shown} nodes shown, {hidden} hidden");
            }
            finally
            {
                Prefs.Appearance.Set(Platform.Keys.HideTrackArtwork, before);
                shell.RunFrames(30);
            }
        });

    // ── the detail page ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Page_layout_swaps_the_detail_arm()
        => host.Mounted(shell =>
        {
            int before = Platform.Settings.Get(Platform.Keys.DetailPageLayout);
            Prefs.DetailHero.Set(Platform.Keys.DetailPageLayout, Prefs.DetailHero.Automatic);
            try
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
                Until(shell, () => !shell.Find("detail:two-column").IsNull, "the wide liked page mounted its two-column arm");
                Prefs.DetailHero.Set(Platform.Keys.DetailPageLayout, Prefs.DetailHero.Hero);   // Settings › Appearance › Page layout
                Until(shell, () => !shell.Find("detail:vertical").IsNull, "Hero swapped the page to its vertical arm");
                shell.RunFrames(30);
                Assert.True(shell.Find("detail:two-column").IsNull, "the two-column arm stayed mounted under Hero");
                Prefs.DetailHero.Set(Platform.Keys.DetailPageLayout, Prefs.DetailHero.Automatic);
                Until(shell, () => !shell.Find("detail:two-column").IsNull, "Automatic swapped the wide page back to two columns");
            }
            finally
            {
                Prefs.DetailHero.Set(Platform.Keys.DetailPageLayout, before);
                shell.RunFrames(30);
            }
        });

    // ── the washes ────────────────────────────────────────────────────────────────────────────────────────────────

    static void SetWashes(bool on)
    {
        // Settings › Appearance › Classic options › Color washes: the generated row's writer, ThemeHost.Apply on Classic's Surfaces option.
        _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, "structure.surfaces", on ? SurfaceWash.Subtle : SurfaceWash.Off));
    }

    [Fact]
    public void Washes_remove_the_artwork_legs_and_bring_them_back_fading()
        => host.Mounted(shell =>
        {
            bool before = ClassicStructure.WashesOn(ThemeHost.Values.Structure.Get(ClassicStructure.Surfaces.Key));
            SetWashes(true);
            try
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents));   // the page that publishes an artwork wash (Recents.Page.cs)
                UntilBackground(shell, () => shell.FindPrefix("shell.wash.").Count > 0, "the recents page's artwork wash mounted");
                shell.RunFrames(60);

                // Off: the legs leave with their host box (no exit fade of their own: production today, not pinned).
                SetWashes(false);
                Until(shell, () => shell.FindPrefix("shell.wash.").Count == 0, "the wash legs left");

                SetWashes(true);
                UntilBackground(shell, () => shell.FindPrefix("shell.wash.").Count > 0, "the wash legs came back");
                var back = shell.FindPrefix("shell.wash.");
                Assert.All(back, leg => Assert.True(shell.Host.Animation.HasTracks(leg.Node), leg.Key + " appeared without its entrance fade"));
            }
            finally
            {
                SetWashes(before);
                shell.RunFrames(30);
            }
        });

    [Fact]
    public void Washes_fade_the_material_tint_in_place()
        => host.Mounted(shell =>
        {
            bool before = ClassicStructure.WashesOn(ThemeHost.Values.Structure.Get(ClassicStructure.Surfaces.Key));
            SetWashes(true);
            try
            {
                // Home tints the chrome from its cards' artwork (HomeScreen's ShellTint), and the fake seed has every one of
                // them: a page that has to fetch would reach the network.
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
                var tint = Node(shell, "shell.material.tint");
                var neutral = R.Wash.NeutralGround;
                Until(shell, () => !Same(shell.Host.Scene.Paint(tint).Fill, neutral), "the cover tinted the chrome");
                shell.RunFrames(120);

                SetWashes(false);
                bool fading = false;
                Until(shell, () =>
                {
                    fading |= shell.Host.Scene.TryGetBrushAnim(tint, out _);
                    return Same(shell.Host.Scene.Paint(tint).Fill, neutral);
                }, "the tint reached the neutral ground");
                Assert.Equal(tint, Node(shell, "shell.material.tint"));   // never remounted: it fades in place
                Assert.True(fading, "the tint snapped to the neutral ground");

                // Back on, the next page's cover tints the chrome again. (The page already showing stays neutral until a
                // navigation: production today, not pinned here.)
                shell.RunFrames(120);
                SetWashes(true);
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents));
                shell.RunFrames(60);
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
                Until(shell, () => !Same(shell.Host.Scene.Paint(Node(shell, "shell.material.tint")).Fill, neutral), "the next page tinted the chrome again");
            }
            finally
            {
                SetWashes(before);
                shell.RunFrames(30);
            }
        });

    static bool Same(FluentGpu.Foundation.ColorF a, FluentGpu.Foundation.ColorF b)
        => MathF.Abs(a.R - b.R) < 0.002f && MathF.Abs(a.G - b.G) < 0.002f && MathF.Abs(a.B - b.B) < 0.002f && MathF.Abs(a.A - b.A) < 0.002f;

    // ── lyrics blur ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lyrics_blur_strength_reaches_the_rail_lines_and_never_the_stage()
        => host.Mounted(shell =>
        {
            int before = Lyrics.Prefs.BlurStrength(onStage: false);   // the READ value (a stored legacy -1 reads 100): the setter has no Auto to restore
            try
            {
                Lyrics.Prefs.SetBlurStrength(100);   // Settings › Lyrics › Blur
                Assert.Equal(100, Lyrics.Prefs.BlurStrength(onStage: false));
                Assert.Equal(0, Lyrics.Prefs.BlurStrength(onStage: true));   // the stage's lyrics never blur

                // Past the fixture's first line (Lyrics.Fixtures: 4 s), so a line is in focus and the others blur around it.
                Play("spotify:track:tr40", fromMs: 20_000);
                if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics))
                    Shell.Ui.Toggle(Shell.RailMode.Lyrics);   // the lyrics button's writer
                // The fixture's lyrics load off the UI thread: a frame budget races that worker on a busy machine (slow runs failed here).
                UntilBackground(shell, () => MaxSigmaUnder(shell, "lyrics:rail") > 0f, "a rail lyrics line painted a blur at strength 100");

                Lyrics.Prefs.SetBlurStrength(0);
                Until(shell, () => MaxSigmaUnder(shell, "lyrics:rail") == 0f, "every rail lyrics line dropped its blur at strength 0", 900);
            }
            finally
            {
                if (Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics) Shell.Ui.Toggle(Shell.RailMode.Lyrics);
                Lyrics.Prefs.SetBlurStrength(before);
                shell.RunFrames(30);
            }
        });

    /// <summary>The largest blur a painted node under the keyed <paramref name="key"/> carries.</summary>
    static float MaxSigmaUnder(Shell.HeadlessShell shell, string key)
    {
        float max = 0f;
        var scene = shell.Host.Scene;
        foreach (var (root, _) in shell.FindPrefix(key))
            foreach (var n in Subtree(scene, root)) max = Math.Max(max, scene.Paint(n).BlurSigma);
        return max;
    }

    // ── marquee ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Marquee_switches_the_player_title_between_the_marquee_and_a_plain_line()
        => host.Mounted(shell =>
        {
            bool before = Platform.Settings.Get(Platform.Keys.MarqueeEnabled);
            try
            {
                SetMarquee(true);
                shell.RunFrames(30);
                int marquee = CountUnder(shell, Node(shell, "np-title"));
                SetMarquee(false);
                shell.RunFrames(30);
                int plain = CountUnder(shell, Node(shell, "np-title"));
                Assert.True(marquee > plain, $"the marquee title is no host: {marquee} nodes on, {plain} off");
            }
            finally
            {
                SetMarquee(before);
                shell.RunFrames(30);
            }
        });

    static void SetMarquee(bool on)
    {
        Platform.Settings.Set(Platform.Keys.MarqueeEnabled, on);   // Settings › Appearance › Scrolling titles (AppearanceToggle)
        Prefs.Appearance.Bump();
    }
}
