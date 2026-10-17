// ── Wavee.ShellTests/ZuneStageMountedTests.cs — Zune's fullscreen stage, mounted (P5 Z4b) ───────────────────────────────────────────
//
// The real shell under Zune (`ZuneHost`) at 1440 x 960 (the Standard window tier) with the stage open through its own API (`Stage.Open`):
//
//   registered   every stage key Z4b owns is drawn by a `zune.stage.*` template; Z4c's keys keep their slots
//   plate        in Lyrics mode the transport is the flat plate across the foot (the allocator's 112, full-bleed), with its 36 seek row and the
//                56 accent disc
//   pivots       the four mode pivots are lowercase through the case role (`TextCase.Lower` at shaping), the selected one heavier
//   layouts      Visualizer mode in the Card layout draws the Card arrangement (the controls inside the card); switching to Large art draws
//                the column arrangement (the cover at its rect) WITHOUT remounting the stage's root box or the transport's layer

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneStageMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    static void OpenStage(Shell.HeadlessShell shell, Stage.Mode mode, Stage.VizLayout layout)
    {
        Prefs.Stage.SetMode((int)mode);
        Prefs.Stage.SetLayout((int)layout);
        Prefs.Stage.SetGalleryOpen(false);
        Prefs.Stage.SetSpectrum((int)Stage.SpectrumStyle.Off);
        Stage.Open(null, "test");
        Until(shell, () => !shell.Find("stage:cover").IsNull && !shell.Find("chrome:transport").IsNull, "the stage's cover and transport mounted");
        shell.RunFrames(120);   // the entrance and every spring settle
    }

    static void CloseStage(Shell.HeadlessShell shell)
    {
        Stage.Close(null, "test");
        shell.RunFrames(60);
        Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
        Prefs.Stage.SetLayout(Platform.Keys.StageLayout.Default);
        Prefs.Stage.SetGalleryOpen(Platform.Keys.StageGalleryOpen.Default);
        Prefs.Stage.SetSpectrum(Platform.Keys.StageSpectrum.Default);
    }

    static RectF Rect(Shell.HeadlessShell shell, string key) => shell.Host.Scene.PresentedAbsoluteRect(Node(shell, key));

    static NodeHandle StageRoot(Shell.HeadlessShell shell) => shell.Host.Scene.Parent(Node(shell, "stage:backdrop"));

    [Fact]
    public void Zune_draws_every_stage_key_of_its_lane()
        => host.Run(() =>
        {
            ZuneHost.Activate();
            try
            {
                (string Id, string Template)[] expected =
                [
                    (ViewKeys.StageRoot.Id, "zune.stage.root"),
                    (ViewKeys.StageTopBar.Id, "zune.stage.top-bar"),
                    (ViewKeys.StageTransport.Id, "zune.stage.transport"),
                    (ViewKeys.StageOptions.Id, "zune.stage.options"),
                    (ViewKeys.StageGallery.Id, "zune.stage.gallery"),
                    (ViewKeys.StageCaption.Id, "zune.stage.caption"),
                    (ViewKeys.StageBackdrop.Id, "zune.stage.backdrop"),
                    (ViewKeys.StageFace.Id, "zune.stage.face"),
                    (ViewKeys.StageFaceScrim.Id, "zune.stage.face-scrim"),
                    (ViewKeys.StageSpectrum.Id, "zune.stage.spectrum"),
                    (ViewKeys.StageNowPlayingCard.Id, "zune.stage.now-playing-card"),
                    (ViewKeys.StageIdentity.Id, "zune.stage.identity"),
                    (ViewKeys.StagePane.Id, "zune.stage.pane"),
                ];
                foreach (var (id, template) in expected)
                    Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
                // Z4c's keys are not Z4b's: whatever draws them, it is not a zune.stage.* template of this lane's
                Assert.NotNull(ThemeHost.Registry.TemplateIdOf(ViewKeys.StageHero.Id));
            }
            finally { ZuneHost.Restore(); }
        });

    [Fact]
    public void The_lyrics_stage_draws_the_flat_plate_with_its_seek_row_and_disc()
        => ZuneHost.Mounted(host, shell =>
        {
            OpenStage(shell, Stage.Mode.Lyrics, Stage.VizLayout.Card);
            try
            {
                Assert.Equal(WidthTier.Standard, ThemeHost.Facts.Peek().Tiers.Window);
                var root = shell.Host.Scene.PresentedAbsoluteRect(StageRoot(shell));
                Assert.Equal(W, root.W, 0.5f);
                Assert.Equal(H, root.H, 0.5f);

                var plate = Rect(shell, "stage:transport:plate");
                Assert.Equal(ZuneStageTiers.PlateH, plate.H, 0.5f);
                Assert.Equal(W, plate.W, 0.5f);                        // full-bleed: the gutter is inside the plate
                Assert.Equal(H - ZuneStageTiers.PlateH, plate.Y, 0.5f);  // at the foot

                var seek = Rect(shell, "stage:transport:seek");
                Assert.Equal(ZuneStageGeometry.SeekRowH, seek.H, 0.5f);
                Assert.True(seek.Y >= plate.Y - 0.5f && seek.Y + seek.H <= plate.Y + plate.H + 0.5f, "the seek row is on the plate");

                var disc = Rect(shell, "stage:transport:disc");
                Assert.Equal(ZuneStageTiers.Stage(WidthTier.Standard).Disc, disc.W, 0.5f);
                Assert.Equal(disc.W, disc.H, 0.5f);

                // "chrome:top" is the full-bleed layer carrying the bar's Enter/Exit (it spans the whole stage); the bar is its first child
                var top = shell.Host.Scene.PresentedAbsoluteRect(shell.Host.Scene.FirstChild(Node(shell, "chrome:top")));
                Assert.Equal(Stage.Layout.TopBarH, top.H, 0.5f);
                Assert.Equal(0f, top.Y, 0.5f);
                Assert.True(top.Y + top.H <= plate.Y, "the top bar and the plate never overlap");
            }
            finally { CloseStage(shell); }
        }, W, H);

    [Fact]
    public void The_pivots_are_lowercase_through_the_case_role()
        => ZuneHost.Mounted(host, shell =>
        {
            OpenStage(shell, Stage.Mode.Lyrics, Stage.VizLayout.Card);
            try
            {
                var scene = shell.Host.Scene;
                for (int i = 0; i < Stage.ModeRules.Count; i++)
                {
                    var word = scene.FirstChild(Node(shell, "stage:pivot:" + i));
                    Assert.False(word.IsNull, "pivot " + i + " carries its word");
                    var style = scene.Layout(word).TextStyle;
                    Assert.Equal(TextCase.Lower, style.Case);
                }
                ushort selected = scene.Layout(scene.FirstChild(Node(shell, "stage:pivot:" + (int)Stage.Mode.Lyrics))).TextStyle.Weight;
                ushort rest = scene.Layout(scene.FirstChild(Node(shell, "stage:pivot:" + (int)Stage.Mode.Visualizer))).TextStyle.Weight;
                Assert.True(selected > rest, "the selected pivot is the heavier word");
                foreach (string key in new[] { "stage:word:options", "stage:word:exit" })
                    Assert.Equal(TextCase.Lower, scene.Layout(scene.FirstChild(Node(shell, key))).TextStyle.Case);
            }
            finally { CloseStage(shell); }
        }, W, H);

    [Fact]
    public void Card_and_large_art_switch_without_remounting_the_stage_root()
        => ZuneHost.Mounted(host, shell =>
        {
            OpenStage(shell, Stage.Mode.Visualizer, Stage.VizLayout.Card);
            try
            {
                var tier = ZuneStageTiers.Stage(WidthTier.Standard);
                var layout = Stage.Layout.Seed(W, H, ZuneStageTiers.Policy.Aspect, ZuneStageTiers.StandardMetrics);
                var root = StageRoot(shell);
                var chrome = Node(shell, "chrome:transport");

                // the Card arrangement: the controls inside the card, the cover at the card's
                Node(shell, "stage:transport:card");
                var cardLook = Stage.Look.Plain(Stage.Mode.Visualizer);
                var card = ZuneStageGeometry.Arrange(in layout, in cardLook, tier, galleryShown: false);
                Assert.Equal(ZuneStageForm.Card, card.Form);
                var npcard = Rect(shell, "stage:npcard");
                Assert.Equal(card.Card.W, npcard.W, 0.5f);
                Assert.Equal(card.Card.Y, npcard.Y, 0.5f);
                var cover = Rect(shell, "stage:cover");
                Assert.Equal(card.Cover.X, cover.X, 0.5f);
                Assert.Equal(card.Cover.W, cover.W, 0.5f);
                var controls = Rect(shell, "stage:transport:controls");
                Assert.True(controls.Y >= npcard.Y && controls.Y + controls.H <= npcard.Y + npcard.H + 0.5f, "the controls sit inside the card");

                // Large art: the column arrangement, on the same root and the same transport layer
                Prefs.Stage.SetLayout((int)Stage.VizLayout.LargeArt);
                Until(shell, () => !shell.Find("stage:transport:column").IsNull, "the Large art column's controls");
                shell.RunFrames(120);
                Assert.Equal(root, StageRoot(shell));
                Assert.Equal(chrome, Node(shell, "chrome:transport"));
                var largeLook = new Stage.Look(Stage.Mode.Visualizer, Stage.VizLayout.LargeArt, Stage.SpectrumStyle.Off, false, false);
                var column = ZuneStageGeometry.Arrange(in layout, in largeLook, tier, galleryShown: false);
                Assert.Equal(ZuneStageForm.Column, column.Form);
                var big = Rect(shell, "stage:cover");
                Assert.Equal(column.Cover.X, big.X, 0.5f);
                Assert.Equal(column.Cover.Y, big.Y, 0.5f);
                Assert.Equal(column.Cover.W, big.W, 0.5f);

                // and back
                Prefs.Stage.SetLayout((int)Stage.VizLayout.Card);
                Until(shell, () => !shell.Find("stage:transport:card").IsNull, "the card's controls again");
                shell.RunFrames(120);
                Assert.Equal(root, StageRoot(shell));
            }
            finally { CloseStage(shell); }
        }, W, H);
}
