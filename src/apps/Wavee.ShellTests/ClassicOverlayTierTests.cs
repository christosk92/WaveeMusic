// ── Wavee.ShellTests/ClassicOverlayTierTests.cs — Classic's overlays, dialogs, sheets and states at every window tier, mounted (themes plan 3.5-3.6, TC9) ──
//
// Each surface is mounted at 400 x 860 (the Compact boards), 300 x 330 (the window floor and minimum height), 1440 x 960 and 2560 x 1440 and asserted on
// geometry and on the record in use, never on pixels: it stays inside the window, a Compact target is at least 40 tall, a wide window does not stretch it,
// a window shorter than the surface scrolls the surface inside itself. The pure arithmetic is `Wavee.Tests\ClassicOverlayTiersTests`.
//
//   cards      the log-out and play-a-link cards: the width (the window less 16 at Compact, their own width elsewhere), 40-tall buttons at Compact
//   sheet      the context sheet: anchored to the bottom, under the chrome row, rows 44 and quick-action cells 64 tall, scrolling past a short window, a sub-menu page
//   states     the offline strip (40 / 36) and vacancy (a stacked column of 40-tall targets at Compact, one row elsewhere)
//   panels     the notification panel and the account menu never leave a 300-DIP window
//   dialogs    an engine ContentDialog opened through `OverlayFit.Dialog`: the card's width and its command row inside the window

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ClassicOverlayTierTests(ShellHost host)
{
    static ViewHarnessOptions At(float w, float h) => new() { Composition = ClassicTheme.Create(), WidthDip = w, HeightDip = h };

    public static TheoryData<float, float> Windows => new()
    {
        { 400f, 860f }, { 300f, 330f }, { 1440f, 960f }, { 2560f, 1440f },
    };

    static bool IsCompact(float width) => width < 528f;

    // ── measuring ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static List<NodeHandle> Nodes(ViewHarness h) => h.Subtree(h.Shell.Host.Scene.Root).ToList();

    static List<NodeHandle> WithRole(ViewHarness h, AutomationRole role)
        => Nodes(h).Where(n => h.Shell.Host.Scene.Interaction(n).Role == role).ToList();

    static void AssertInside(ViewHarness h, NodeHandle node, float w, float height, string what)
    {
        var r = h.AbsoluteBounds(node);
        Assert.True(r.X >= -0.5f && r.Y >= -0.5f && r.Right <= w + 0.5f && r.Bottom <= height + 0.5f,
            $"{what} {r.X:0.#},{r.Y:0.#} {r.W:0.#}x{r.H:0.#} leaves the {w}x{height} window");
    }

    /// <summary>The widest node narrower than the window: a card laid out under a full-window layer.</summary>
    static RectF Card(ViewHarness h, float windowWidth)
        => Nodes(h).Select(h.AbsoluteBounds).Where(r => r.W < windowWidth - 0.5f && r.W > 0f).OrderByDescending(r => r.W).First();

    // ── the bare cards ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_logout_card_fills_the_window_less_16_at_Compact_keeps_its_380_elsewhere_and_has_40_tall_buttons_at_Compact(float w, float height)
        => ViewHarness.With(host, ViewKeys.DialogLogoutConfirm,
            new LogoutConfirmProps { Cancel = static () => { }, Confirm = static () => { } }, h =>
            {
                var card = Card(h, w);
                Assert.Equal(IsCompact(w) ? w - 32f : 380f, card.W, 0.5f);
                Assert.True(card.Bottom <= height + 0.5f, $"the card ({card.H:0.#} tall) fits the {height} tall window");
                var buttons = WithRole(h, AutomationRole.Button);
                Assert.Equal(2, buttons.Count);
                foreach (var b in buttons)
                {
                    AssertInside(h, b, w, height, "a card button");
                    if (IsCompact(w)) Assert.True(h.AbsoluteBounds(b).H >= 40f, "a Compact target is at least 40 tall");
                }
                if (IsCompact(w))
                {
                    var (a, b) = (h.AbsoluteBounds(buttons[0]), h.AbsoluteBounds(buttons[1]));
                    Assert.Equal(a.W, b.W, 1f);   // the two share the row equally
                }
            }, At(w, height));

    static PlayLinkProps Link(bool browser) => new()
    {
        Placeholder = "Paste a link", Text = static () => new Signal<string>(""), Status = "", CanPlay = true, OffersBrowser = browser,
        OpenInBrowser = static () => { }, Cancel = static () => { }, Submit = static () => { },
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_play_link_card_keeps_its_field_inside_the_card_and_the_card_inside_the_window(float w, float height)
        => ViewHarness.With(host, ViewKeys.DialogPlayLink, Link(browser: false), h =>
        {
            var card = Card(h, w);
            Assert.Equal(IsCompact(w) ? w - 32f : 420f, card.W, 0.5f);
            Assert.True(card.Bottom <= height + 0.5f, $"the card ({card.H:0.#} tall) fits the {height} tall window");
            foreach (var n in Nodes(h))
            {
                var r = h.AbsoluteBounds(n);
                Assert.True(r.X >= card.X - 0.5f && r.Right <= card.Right + 0.5f || r.W >= w - 0.5f, $"a part {r.X:0.#}..{r.Right:0.#} stays inside the card {card.X:0.#}..{card.Right:0.#}");
            }
            foreach (var b in WithRole(h, AutomationRole.Button))
                if (IsCompact(w)) Assert.True(h.AbsoluteBounds(b).H >= 40f, "a Compact target is at least 40 tall");
        }, At(w, height));

    // ── the context sheet ────────────────────────────────────────────────────────────────────────────────────────────────────

    static ContextSheetProps Sheet(int rows = 6, bool sub = true)
    {
        var items = new List<MenuFlyoutItem>();
        if (sub) items.Add(MenuFlyoutItem.SubMenu("Add to playlist", [new MenuFlyoutItem("New playlist", default, true, static () => { }), new MenuFlyoutItem("Road trip")]));
        for (int i = 0; i < rows; i++)
        {
            items.Add(new MenuFlyoutItem("Row " + i, default, true, static () => { }));
            if (i == 2) items.Add(MenuFlyoutItem.Separator);
        }
        items.Add(MenuFlyoutItem.Toggle("Saved", true, static () => { }));
        return new ContextSheetProps
        {
            Header = new ContextMenuHeader(new BoxEl { Width = 38f, Height = 38f }, "Loser", "Tame Impala"),
            Primary =
            [
                new AppBarCommand("", "Play", static () => { }),
                new AppBarCommand("", "Play next", static () => { }),
                new AppBarCommand("", "Play after", static () => { }),
                new AppBarCommand("", "Saved", static () => { }, AppBarCommandKind.ToggleButton, true),
            ],
            Rows = items, Close = static () => { },
        };
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_context_sheet_sits_on_the_lower_edge_under_the_chrome_row_with_44_tall_rows_and_scrolls_inside_a_short_window(float w, float height)
        => ViewHarness.With(host, ViewKeys.OverlayContextSheet, Sheet(), h =>
        {
            h.RunFrames(30);   // the entry slide has run
            var card = h.AbsoluteBounds(h.Node("context-sheet"));
            Assert.Equal(height, card.Bottom, 1f);                                         // laid over the lower edge
            Assert.True(card.Y >= ClassicSheetRules.ChromeRow - 0.5f, $"the card ({card.Y:0.#}) stays under the 48-DIP chrome row");
            Assert.Equal(w, card.W, 0.5f);                                                  // the whole width
            Assert.True(card.H <= height - ClassicSheetRules.ChromeRow + 0.5f, "a card taller than the window clamps to it");

            // Every visible row is a 44-DIP target; the quick-action strip's cells are at least 40 (64 in the board).
            var scene = h.Shell.Host.Scene;
            var rows = h.FindPrefix("sheet-row:");
            Assert.NotEmpty(rows);
            foreach (var (n, _) in rows) Assert.Equal(ClassicSheetRules.RowHeight, h.AbsoluteBounds(n).H, 0.5f);
            var cells = h.FindPrefix("sheet-cell:");
            Assert.Equal(4, cells.Count);
            foreach (var (n, _) in cells)
            {
                Assert.True(h.AbsoluteBounds(n).H >= 40f);
                Assert.Equal(ClassicSheetRules.StripCell, h.AbsoluteBounds(n).H, 0.5f);
            }
            // Four equal columns across the card less its 8 + 8.
            Assert.Equal(cells.Select(c => h.AbsoluteBounds(c.Node).W).Max(), cells.Select(c => h.AbsoluteBounds(c.Node).W).Min(), 1f);
            _ = scene;
        }, At(w, height));

    [Fact]
    public void The_context_sheet_is_as_tall_as_its_content_in_a_tall_window_and_scrolls_when_the_window_is_short()
    {
        float tall = 0f, shortH = 0f;
        ViewHarness.With(host, ViewKeys.OverlayContextSheet, Sheet(), h => { h.RunFrames(30); tall = h.AbsoluteBounds(h.Node("context-sheet")).H; }, At(400f, 860f));
        ViewHarness.With(host, ViewKeys.OverlayContextSheet, Sheet(), h => { h.RunFrames(30); shortH = h.AbsoluteBounds(h.Node("context-sheet")).H; }, At(300f, 330f));
        Assert.True(tall < 860f - ClassicSheetRules.ChromeRow, "the content decides the height while the window is roomy");
        Assert.Equal(330f - ClassicSheetRules.ChromeRow, shortH, 1f);   // 282: the card fills what is under the chrome row and the rows scroll
    }

    [Fact]
    public void A_sub_menu_row_opens_its_rows_in_place_under_a_back_row()
        => ViewHarness.With(host, ViewKeys.OverlayContextSheet, Sheet(rows: 2), h =>
        {
            h.RunFrames(30);
            Assert.False(h.Contains("sheet-back"));
            var first = h.Node("sheet-row:0");                       // the sub-menu row leads the rows
            var r = h.AbsoluteBounds(first);
            var p = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
            h.Shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0));
            h.Shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1000));
            h.Shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1010));
            h.RunFrames(4);
            Assert.True(h.Contains("sheet-back"), "a back row heads the sub-menu page");
            Assert.Equal(2, h.FindPrefix("sheet-row:").Count);       // New playlist, Road trip
            Assert.Equal(ClassicSheetRules.RowHeight, h.AbsoluteBounds(h.Node("sheet-back")).H, 0.5f);
        }, At(400f, 860f));

    // ── the states ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_offline_strip_is_40_tall_and_edge_to_edge_at_Compact_and_36_tall_elsewhere_with_a_40_tall_Retry_at_Compact(float w, float height)
        => ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps { OnRetry = static () => { } }, h =>
        {
            var strip = h.AbsoluteBounds(h.Node("offline-strip"));
            Assert.Equal(IsCompact(w) ? 40f : 36f, strip.H, 0.5f);
            Assert.True(strip.W <= w + 0.5f, "no page-level horizontal overflow");
            foreach (var b in WithRole(h, AutomationRole.Button))
            {
                AssertInside(h, b, w, height, "Retry");
                if (IsCompact(w)) Assert.True(h.AbsoluteBounds(b).H >= 40f);
            }
        }, At(w, height));

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_offline_vacancy_stacks_40_tall_actions_at_Compact_and_keeps_one_row_elsewhere(float w, float height)
        => ViewHarness.With(host, ViewKeys.OverlayOfflineVacancy,
            new OfflineVacancyProps { Page = "Browse", OnRetry = static () => { }, GoToLibrary = static () => { } }, h =>
            {
                var scene = h.Shell.Host.Scene;
                var actions = h.Node("offline-vacancy:actions");
                var kids = new List<NodeHandle>();
                for (var c = scene.FirstChild(actions); !c.IsNull; c = scene.NextSibling(c)) kids.Add(c);
                Assert.Equal(2, kids.Count);
                var (a, b) = (h.AbsoluteBounds(kids[0]), h.AbsoluteBounds(kids[1]));
                if (IsCompact(w))
                {
                    Assert.True(b.Y >= a.Bottom - 0.5f, "Retry over the library link: a column");
                    Assert.True(a.H >= 40f && b.H >= 40f, "every Compact target is at least 40 tall");
                    Assert.True(a.W >= ClassicOverlayRules.OfflineVacancyRetryMinW - 0.5f);
                }
                else Assert.True(b.X >= a.Right - 0.5f, "Retry beside the library link: a row");
                AssertInside(h, actions, w, height, "the actions");
                var root = h.AbsoluteBounds(h.Node("offline-vacancy"));
                Assert.True(root.W <= w + 0.5f);
            }, At(w, height));

    // ── the panels ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FakeNotificationsHost : INotificationsHost
    {
        public INotificationVerbs Verbs => NotificationVerbs.Shared;
        public void SetFilter(NotifyFilter filter) { }
        public void ToggleActivity(in Notification n) { }
        public void Undo(in Notification n) { }
        public NotificationActivityTarget? ActivityTarget(in Notification n) => new("Playlist", null);
        public Func<ContextMenuModel?>? ReleaseMenu(in Notification n) => null;
    }

    static NotificationsProps Panel() => new()
    {
        Filter = NotifyFilter.All,
        Rows = Enumerable.Range(0, 12).Select(i => new Notification("s" + i, 1_000 + i, true, NotifyCategory.Social, Title: "Ada followed you " + i)).ToArray(),
        Now = 10_000, ExpandedId = "", EmptyText = "Nothing new", CanUndo = false, UpdateProgress = new FloatSignal(0f),
        PendingEdits = new Signal<int>(0), Host = new FakeNotificationsHost(),
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_notification_panel_stays_inside_the_window_and_its_feed_scrolls_inside_a_short_one(float w, float height)
        => ViewHarness.With(host, ViewKeys.OverlayNotifications, Panel(), h =>
        {
            h.RunFrames(30);
            var panel = Card(h, w);   // the panel is the widest node narrower than the window
            Assert.True(panel.W <= w + 0.5f, $"the panel ({panel.W:0.#}) is no wider than the {w} window");
            Assert.Equal(MathF.Min(380f, w - (IsCompact(w) ? 16f : 32f)), panel.W, 0.5f);
            Assert.True(panel.H <= height + 0.5f, $"the panel ({panel.H:0.#}) is no taller than the {height} window");
        }, At(w, height));

    [Theory]
    [MemberData(nameof(Windows))]
    public void The_account_menu_is_never_wider_than_the_window_less_its_margin(float w, float height)
        => ViewHarness.With(host, ViewKeys.OverlayProfileMenu,
            new ProfileMenuProps
            {
                Known = true, Name = "Ada Lovelace", ShowsTier = true, Premium = true,
                Items = [new MenuFlyoutItem("Account", default, true, static () => { }), new MenuFlyoutItem("Log out", default, true, static () => { })],
                Close = static () => { },
            }, h =>
            {
                var menu = Card(h, w);
                Assert.Equal(MathF.Min(304f, w - (IsCompact(w) ? 16f : 32f)), menu.W, 0.5f);
                Assert.True(menu.Right <= w + 0.5f);
            }, At(w, height));

    // ── a real engine ContentDialog, fitted ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Captures the overlay service the engine's host provides to the tree.</summary>
    sealed class CaptureOverlay(Action<IOverlayService> got) : Component
    {
        public override Element Render()
        {
            got(UseContext(Overlay.Service));
            return new BoxEl();
        }
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void An_engine_dialog_fitted_by_the_structure_stays_inside_the_window_and_never_stretches(float w, float height)
        => host.Run(() =>
        {
            // The facts the dialog's opener reads: no frame root resolves the tiers of a bare mount, so publish them from the window.
            TierFacts tiers = WindowTiers.Resolve(ThemeHost.Facts.Peek().Policies.Tiers, w, height, w);
            ThemeHost.PublishTiers(in tiers);
            IOverlayService? overlay = null;
            using var shell = Shell.Headless.Mount(() => OverlayHost.Create(Embed.Comp(() => new CaptureOverlay(s => overlay = s))), w, height);
            shell.RunFrames(3);
            Assert.NotNull(overlay);
            Assert.False(Controls.IsNullOverlay(overlay), "the headless host provides a real overlay service");

            ContentDialog.Show(overlay!, d =>
            {
                d.Title = "Reset everything";
                d.Message = string.Join(" ", Enumerable.Repeat("This puts the sidebar back the way it was and cannot be undone.", 12));
                d.PrimaryText = "Reset";
                d.CloseText = "Cancel";
                d.DefaultButton = ContentDialog.DefaultBtn.Close;
                OverlayFit.Dialog(d, Controls.DialogWidthCompact);
            });
            shell.RunFrames(40);

            var scene = shell.Host.Scene;
            var all = ShellProbe.Subtree(scene, scene.Root).ToList();
            var card = all.Select(scene.PresentedAbsoluteRect).Where(r => r.W < w - 0.5f && r.W > 0f).OrderByDescending(r => r.W).First();
            float expected = IsCompact(w) ? w - 32f : Controls.DialogWidthCompact;
            Assert.Equal(expected, card.W, 0.5f);
            Assert.True(card.H <= height - (IsCompact(w) ? 32f : 48f) + 0.5f, $"the card ({card.H:0.#} tall) clamps to the {height} tall window");

            var buttons = all.Where(n => scene.Interaction(n).Role == AutomationRole.Button).Select(scene.PresentedAbsoluteRect).ToList();
            Assert.Equal(2, buttons.Count);
            if (IsCompact(w))
            {
                // The engine's own command buttons at the 40-DIP target, side by side in two equal columns even in a 300-DIP window's card (a 96 floor each).
                Assert.All(buttons, b => Assert.Equal(40f, b.H, 0.5f));
                Assert.Equal(buttons[0].Y, buttons[1].Y, 0.5f);
                Assert.Equal(buttons[0].W, buttons[1].W, 0.5f);
            }
            else
            {
                // Elsewhere the engine's WinUI buttons, unchanged: 32 tall, at least 130 wide.
                Assert.All(buttons, b => Assert.Equal(32f, b.H, 0.5f));
                Assert.All(buttons, b => Assert.True(b.W >= 130f - 0.5f, $"a {b.W:0.#}-wide command"));
            }
            foreach (var b in buttons)
                Assert.True(b.X >= card.X - 0.5f && b.Right <= card.Right + 0.5f && b.Bottom <= height + 0.5f && b.Y >= 0f,
                    $"a command button {b.X:0.#},{b.Y:0.#} {b.W:0.#}x{b.H:0.#} stays in the card and the window");
            overlay!.CloseAll();
            shell.RunFrames(5);
        });
}
