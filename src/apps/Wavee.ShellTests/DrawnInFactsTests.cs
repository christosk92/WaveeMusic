// ── Wavee.ShellTests/DrawnInFactsTests.cs — a view reads the tiers it is DRAWN IN, never the process's last window (themes plan 3.2) ──────
//
// A composition mounted by the view harness at a size other than the app window's draws in its OWN facts (`ThemeFactsContext`, the harness's
// signal), and the process's `ThemeHost.Facts` keep the app window's. Every Classic component and core host reads the facts it is drawn in
// (`ctx.Tier()` in a template, `TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal))` in a component), so a Compact mount gets the
// Compact record while the process's window is Standard:
//
//   detail   the hero's action row (Tiers.Detail.cs: the Compact 40 target, which the Play split carries in its key)
//   artist   the photo lightbox's Export and Close (Tiers.Artist.cs: 40 tall at Compact)
//   stage    the core stage surface's allocator budgets from the Compact metrics (the 104 transport card, not Standard's 112)

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.ShellTests;

public sealed class DrawnInFactsTests(ShellHost host)
{
    const float W = 400f, H = 860f;

    /// <summary>Mount <paramref name="key"/> under Classic at a Compact 400 x 860 while the PROCESS's tiers say a Standard 1180 x 760 window, run
    /// <paramref name="body"/>, then put the process's tiers back.</summary>
    void CompactUnderAStandardProcess<TProps>(ViewKey<TProps> key, TProps props, Action<ViewHarness> body, Action? before = null) where TProps : class
        => host.Run(() =>
        {
            before?.Invoke();
            var saved = ThemeHost.Facts.Peek().Tiers;
            var standard = WindowTiers.Resolve(ThemeHost.Facts.Peek().Policies.Tiers, 1180f, 760f, 1180f);
            ThemeHost.PublishTiers(in standard);
            try
            {
                using var harness = ViewHarness.Mount(key, props, new ViewHarnessOptions { Composition = ClassicTheme.Create(), WidthDip = W, HeightDip = H });
                Assert.Equal(WidthTier.Standard, ThemeHost.Facts.Peek().Tiers.Window);   // the process's window
                Assert.Equal(WidthTier.Compact, harness.Facts.Peek().Tiers.Window);       // the facts the mount is drawn in
                body(harness);
            }
            finally { ThemeHost.PublishTiers(in saved); }
            ShellHost.AssertNoNetwork();
        });

    // ── detail ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static DetailHeroProps Hero()
    {
        var spec = new Detail.VerticalSpec(
            new Detail.Identity { Subject = EntityUri.Parse("spotify:playlist:pl0"), Kind = DetailKind.Playlist, Title = "Fixture" },
            Detail.Config.Playlist, new Detail.FrameActions(), new Detail.FrameSlots(), static () => ColorF.Transparent);
        var parts = new Detail.HeroParts(
            Toolbar: null, BandActions: new BoxEl(), SelectionBar: null, SelectionVisible: new Signal<bool>(false),
            CompactInteractive: new Signal<bool>(false), SearchField: null, SearchExpanded: new Signal<bool>(false),
            ColumnWidth: W, CompactLeft: 16f, HeroHeight: 0f, OnHeroMeasured: null);
        return new DetailHeroProps(spec, parts);
    }

    [Fact]
    public void A_compact_mount_draws_the_detail_heros_compact_action_row()
        => CompactUnderAStandardProcess(ViewKeys.DetailHero, Hero(), harness =>
        {
            harness.RunFrames(10);
            Assert.Equal(40f, ClassicDetailTiers.Of(harness.Facts.Peek().Tiers).Target);
            var splits = harness.FindPrefix("ctxplay:");
            Assert.NotEmpty(splits);
            // The Play split states a target over the tuned 32 in its key (`ClassicPlaySplit.PlaySplit`): ":40" is the Compact record's.
            Assert.All(splits, s => Assert.EndsWith(":40", s.Key));
        });

    // ── artist ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly string[] s_photos = ["https://i.scdn.co/image/fixture-a", "https://i.scdn.co/image/fixture-b"];

    [Fact]
    public void A_compact_mount_draws_the_artist_lightboxs_compact_buttons()
    {
        var selected = new Signal<int>(0);
        var props = new ArtistLightboxProps
        {
            Photos = s_photos, Selected = selected, Select = i => selected.Value = i, Saving = new Signal<bool>(false),
            Export = static (_, _) => { }, Close = static () => { }, Attach = static (_, _) => { },
        };
        CompactUnderAStandardProcess(ViewKeys.ArtistLightbox, props, harness =>
        {
            harness.RunFrames(10);
            var scene = harness.Shell.Host.Scene;
            // Export and Close: the top chrome's two buttons (the one 72-DIP row at the top of the viewer).
            var top = harness.Subtree(scene.Root)
                .Where(n => scene.Interaction(n).Role == AutomationRole.Button)
                .Select(harness.AbsoluteBounds)
                .Where(r => r.W > 0f && r.H > 0f && r.Y < 72f)
                .ToList();
            Assert.True(top.Count >= 2, $"the top chrome's Export and Close ({top.Count} buttons found)");
            Assert.All(top, r => Assert.True(r.H >= 40f - 0.5f, $"a Compact lightbox button is {r.H} tall, under 40"));
        });
    }

    // ── stage ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_compact_mount_budgets_the_stage_from_the_compact_metrics()
        => CompactUnderAStandardProcess(ViewKeys.StageRoot, Stage.RootProps, harness =>
        {
            var compact = Stage.Layout.Seed(W, H, metrics: ClassicStageTiers.Policy.MetricsFor(WidthTier.Compact));
            var standard = Stage.Layout.Seed(W, H, metrics: ClassicStageTiers.Policy.MetricsFor(WidthTier.Standard));
            Assert.NotEqual(standard.TransportH, compact.TransportH);   // else this fact could not tell the two apart

            for (int i = 0; i < 240 && harness.Find("chrome:transport").IsNull; i++) harness.RunFrames(1);
            harness.RunFrames(60);
            var scene = harness.Shell.Host.Scene;
            var transport = scene.PresentedAbsoluteRect(scene.FirstChild(harness.Node("chrome:transport")));
            Assert.Equal(compact.TransportH, transport.H, 0.5f);
        }, before: static () => Prefs.Stage.SetMode((int)Stage.Mode.Lyrics));
}
