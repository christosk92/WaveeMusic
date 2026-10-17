// ── Wavee.ShellTests/ZuneOverlaysMountedTests.cs — Zune's overlays and dialogs, mounted (themes plan 6.3, P5 Z8a) ───────────────────────────────────
//
// The account chip and menu, the context menu's tile, header and round-verb strip, the Compact context sheet, the notification panel, the runtime banner, the update toast's card,
// the offline strip and vacancy and the dialog bodies (log out, play a link, the signature sheet, the destination picker, the credits sheet) are Zune's own since Z8a. The core
// keeps every overlay handle, dismiss rule, focus trap, the toast queue, the dialog shells and every verb and decision; what only a mounted tree can say:
//
//   registered   Zune is the registered template of every overlay and dialog key (its registrations win over the Classic stand-in key by key)
//   menu         a track context menu opens through the engine with Zune's header and its round-verb strip between the header and the rows; the first verb runs and closes the menu
//   sheet        at Compact a context sheet is a card on the window's lower edge, its verbs 40 across; a verb closes the sheet FIRST, then acts
//   toast        a toast shows bottom right, 12 above the play bar, 360 wide
//   offline      the strip is 36 tall (40 at Compact), the retry word exists only when the host can retry
//   dialog       the log-out card mounts with its commands as words (an accent disc and a word), fills a Compact window less 16
//   panel        a row per decided notification under its key and five filter words; the panel never outgrows a 300-DIP window
//   account      one menu row per decided row under a header that names the account
//
// The tier records are data of the structure (`ZuneOverlayTiers`); a fact here asserts the record IN USE through a node, never pixels. Where something loads, the wait is real time.
// The installed themes, `theme.id` and Developer mode are process state: every fact puts Classic back alone (`ZuneHost`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneOverlaysMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderZune(float width = 1440f, float height = 960f) => new() { Composition = ZuneTheme.Create(), WidthDip = width, HeightDip = height };

    static int WithRole(ViewHarness harness, params AutomationRole[] roles)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Count(n => Array.IndexOf(roles, scene.Interaction(n).Role) >= 0);
    }

    static void Click(Shell.HeadlessShell shell, NodeHandle node, int button = 0)
    {
        var r = shell.Host.Scene.AbsoluteRect(node);
        var p = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: 2000));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, button, 0, TimestampMs: 2010));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, button, 0, TimestampMs: 2060));
        shell.RunFrames(3);
    }

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_is_the_registered_template_of_every_overlay_and_dialog_key()
        => host.Run(() =>
        {
            ZuneHost.Activate();
            try
            {
                (string Id, string Template)[] expected =
                [
                    (ViewKeys.OverlayProfileChip.Id, "zune.overlay.profile-chip"),
                    (ViewKeys.OverlayProfileMenu.Id, "zune.overlay.profile-menu"),
                    (ViewKeys.OverlayNotifications.Id, "zune.overlay.notifications"),
                    (ViewKeys.OverlayRuntimeBanner.Id, "zune.overlay.runtime-banner"),
                    (ViewKeys.OverlayToast.Id, "zune.overlay.toast"),
                    (ViewKeys.OverlayOfflineStrip.Id, "zune.overlay.offline-strip"),
                    (ViewKeys.OverlayOfflineVacancy.Id, "zune.overlay.offline-vacancy"),
                    (ViewKeys.MenuContext.Id, "zune.menu.context-tile"),
                    (ViewKeys.OverlayContextSheet.Id, "zune.overlay.context-sheet"),
                    (ViewKeys.DialogLogoutConfirm.Id, "zune.dialog.logout-confirm"),
                    (ViewKeys.DialogPlayLink.Id, "zune.dialog.play-link"),
                    (ViewKeys.DialogSignature.Id, "zune.dialog.signature"),
                    (ViewKeys.DialogFolderPicker.Id, "zune.dialog.folder-picker"),
                    (ViewKeys.DialogSheet.Id, "zune.dialog.sheet"),
                ];
                foreach (var (id, template) in expected)
                    Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
            }
            finally { ZuneHost.Restore(); }
        });

    // ── the track context menu ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A box that raises the engine's context menu with <see cref="Factory"/>'s model on a right press.</summary>
    sealed class MenuTarget : Component
    {
        public required Func<ContextMenuModel?> Factory;

        public override Element Render()
        {
            var svc = UseContext(Overlay.Service);
            return new BoxEl
            {
                Direction = 1,
                Children = [new BoxEl { Key = "ztarget", Width = 120f, Height = 32f, Role = AutomationRole.Button, OnClick = static () => { } }.WithContextMenu(svc, Factory)],
            };
        }
    }

    [Fact]
    public void A_track_context_menu_opens_with_the_header_and_the_round_verb_strip_and_its_first_verb_runs_and_closes_it()
        => host.Run(() =>
        {
            ZuneHost.Activate();
            try
            {
                int played = 0, queued = 0;
                ContextMenuModel? Model() => new ContextMenuModel(
                    [
                        new AppBarCommand(Icons.Play, "play", () => played++),
                        new AppBarCommand(Icons.Add, "add to queue", () => queued++),
                    ],
                    [
                        new MenuFlyoutItem("Go to album", default, true, static () => { }),
                        MenuFlyoutItem.Separator,
                        new MenuFlyoutItem("Go to artist", default, true, static () => { }),
                    ],
                    new ContextMenuHeader(new BoxEl { Width = 40f, Height = 40f }, "Nightcall", "Kavinsky · OutRun"));

                TierFacts tiers = WindowTiers.Resolve(ThemeHost.Facts.Peek().Policies.Tiers, 1440f, 960f, 1440f);
                ThemeHost.PublishTiers(in tiers);
                using var shell = Shell.Headless.Mount(() => OverlayHost.Create(Embed.Comp(() => new MenuTarget { Factory = Model })), 1440f, 960f);
                shell.RunFrames(4);

                Click(shell, Node(shell, "ztarget"), button: 1);   // a right press
                shell.RunFrames(40);

                var scene = shell.Host.Scene;
                var header = Node(shell, ZuneOverlayRules.MenuHeaderKey);
                var strip = Node(shell, ZuneOverlayRules.StripKey);
                var play = Node(shell, ZuneOverlayRules.StripVerbKeyPrefix + "0");
                var queue = Node(shell, ZuneOverlayRules.StripVerbKeyPrefix + "1");
                var firstRow = Subtree(scene, scene.Root).First(n => scene.Interaction(n).Role == AutomationRole.MenuItem);

                // The header, then the strip, then the rows (the rows stay a plain menu).
                Assert.True(scene.AbsoluteRect(header).Y < scene.AbsoluteRect(strip).Y, "the header is above the strip");
                Assert.True(scene.AbsoluteRect(strip).Y < scene.AbsoluteRect(firstRow).Y, "the strip is above the rows");
                // The verbs are 32 across (a pointer menu) and 18 apart.
                var a = scene.AbsoluteRect(play);
                var b = scene.AbsoluteRect(queue);
                Assert.Equal(ZuneOverlayRules.StripDisc, a.W, 0.5f);
                Assert.Equal(ZuneOverlayRules.StripDisc, a.H, 0.5f);
                Assert.Equal(ZuneOverlayRules.StripGap, b.X - (a.X + a.W), 0.5f);

                Click(shell, play);
                shell.RunFrames(40);
                Assert.Equal(1, played);
                Assert.Equal(0, queued);
                Assert.True(shell.Find(ZuneOverlayRules.StripKey).IsNull, "the menu closed after the verb ran");
                ShellHost.AssertNoNetwork();
            }
            finally { ZuneHost.Restore(); }
        });

    // ── the Compact context sheet ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void At_Compact_the_context_sheet_sits_on_the_lower_edge_with_40_verbs_and_a_verb_closes_the_sheet_before_it_acts()
    {
        var order = new List<string>();
        var props = new ContextSheetProps
        {
            Header = new ContextMenuHeader(null, "Nightcall", "Kavinsky"),
            Primary = [new AppBarCommand(Icons.Play, "play", () => order.Add("play")), new AppBarCommand(Icons.Add, "add to queue", () => order.Add("queue"))],
            Rows =
            [
                new MenuFlyoutItem("Go to album", default, true, () => order.Add("album")),
                MenuFlyoutItem.Separator,
                new MenuFlyoutItem("Go to artist", default, true, () => order.Add("artist")),
            ],
            Close = () => order.Add("close"),
        };
        ViewHarness.With(host, ViewKeys.OverlayContextSheet, props, harness =>
        {
            harness.RunFrames(60);   // the entrance has run
            var scene = harness.Shell.Host.Scene;
            var card = scene.PresentedAbsoluteRect(harness.Node(ZuneOverlayRules.SheetCardKey));
            Assert.Equal(860f, card.Y + card.H, 1f);   // the card sits on the window's lower edge
            Assert.Equal(400f, card.W, 1f);            // and fills its width
            Assert.True(card.Y >= 48f - 0.5f, "the title row stays clear above the sheet");
            Assert.Equal(2, harness.FindPrefix(ZuneOverlayRules.SheetRowKeyPrefix).Count);

            var play = scene.AbsoluteRect(harness.Node(ZuneOverlayRules.StripVerbKeyPrefix + "0"));
            Assert.True(play.W >= 40f - 0.5f && play.H >= 40f - 0.5f, "a Compact verb is a 40-DIP target");

            Click(harness.Shell, harness.Node(ZuneOverlayRules.StripVerbKeyPrefix + "0"));
            Assert.Equal(["close", "play"], order);   // closed first, then acted
        }, UnderZune(400f, 860f));
    }

    // ── a toast ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_toast_shows_bottom_right_12_above_the_play_bar_360_wide()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(30);   // the overlay lane has docked itself
            var handle = Toast.Show("added 3 to queue", new ToastOptions { DurationMs = 0f, ActionLabel = "open queue", OnAction = static () => { } });
            Assert.True(handle.IsOpen, "the lane is mounted and took the toast");
            shell.RunFrames(90);   // the enter has settled

            var scene = shell.Host.Scene;
            var cards = shell.FindPrefix("toast:");
            Assert.Single(cards);
            var rect = scene.PresentedAbsoluteRect(cards[0].Node);
            float bar = ThemeHost.Facts.Peek().Policies.PlayerBar.Height;   // what the overlay lane docks above
            Assert.Equal(960f - bar - 12f, rect.Y + rect.H, 1.5f);   // 12 above the bar
            Assert.Equal(1440f - 24f, rect.X + rect.W, 1.5f);         // 24 in from the right edge
            Assert.Equal(360f, rect.W, 1.5f);                         // the board's width
            handle.Close();
            Toast.CloseAll();
        }, 1440f, 960f);

    // ── the offline strip ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_offline_strip_is_36_tall_and_40_at_Compact_and_its_retry_word_exists_only_when_the_host_can_retry()
    {
        float standard = 0f, compact = 0f;
        int withRetry = 0, withoutRetry = 0;
        ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps { OnRetry = static () => { } }, h =>
        {
            standard = h.AbsoluteBounds(h.Node(ZuneOverlayRules.OfflineStripKey)).H;
            withRetry = WithRole(h, AutomationRole.Button);
        }, UnderZune());
        ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps(), h => withoutRetry = WithRole(h, AutomationRole.Button), UnderZune());
        ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps { OnRetry = static () => { } },
            h => compact = h.AbsoluteBounds(h.Node(ZuneOverlayRules.OfflineStripKey)).H, UnderZune(400f, 860f));
        Assert.Equal(36f, standard, 0.5f);
        Assert.Equal(40f, compact, 0.5f);
        Assert.Equal(1, withRetry);
        Assert.Equal(0, withoutRetry);
    }

    // ── the log-out card ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_logout_card_mounts_with_its_commands_as_words_and_fills_a_Compact_window_less_16()
    {
        int confirmed = 0, cancelled = 0;
        var props = new LogoutConfirmProps { Cancel = () => cancelled++, Confirm = () => confirmed++ };
        ViewHarness.With(host, ViewKeys.DialogLogoutConfirm, props, h =>
        {
            Assert.Equal(2, WithRole(h, AutomationRole.Button));   // the accent disc with its word, and "cancel"
            var commands = h.AbsoluteBounds(h.Node(ZuneOverlayRules.CommandsKey));
            var plate = h.AbsoluteBounds(h.Node(ZuneOverlayRules.PlateKey));
            Assert.Equal(420f, plate.W, 0.5f);
            Assert.True(commands.W <= plate.W, "the commands stay inside the plate");
            Click(h.Shell, h.Shell.FindPrefix(ZuneOverlayRules.CommandsKey).First().Node);   // a press on the row itself is inert
            Assert.Equal(0, confirmed + cancelled);
        }, UnderZune());
        ViewHarness.With(host, ViewKeys.DialogLogoutConfirm, props, h =>
        {
            var plate = h.AbsoluteBounds(h.Node(ZuneOverlayRules.PlateKey));
            Assert.Equal(400f - 32f, plate.W, 0.5f);   // the window less 16 a side
            var scene = h.Shell.Host.Scene;
            foreach (var n in h.Subtree(scene.Root).Where(n => scene.Interaction(n).Role == AutomationRole.Button))
                Assert.True(scene.AbsoluteRect(n).H >= 40f - 0.5f, "a Compact command is a 40-DIP target");
        }, UnderZune(400f, 860f));
    }

    // ── the notification panel ───────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FakeNotificationsHost : INotificationsHost
    {
        public INotificationVerbs Verbs => NotificationVerbs.Shared;
        public void SetFilter(NotifyFilter filter) { }
        public void ToggleActivity(in Notification n) { }
        public void Undo(in Notification n) { }
        public NotificationActivityTarget? ActivityTarget(in Notification n) => new("Playlist", null);
        public Func<ContextMenuModel?>? ReleaseMenu(in Notification n) => null;
    }

    static readonly Notification[] Feed =
    [
        new("s1", 1_000, true, NotifyCategory.Social, Title: "Ada followed you"),
        new("r1", 2_000, false, NotifyCategory.NewRelease, Title: "A new album", Creator: "Ada", ReleaseKind: NewReleaseKind.Episode),
        new("a1", 3_000, false, NotifyCategory.Activity, Title: "Added 3 songs", Body: "to Road trip"),
    ];

    static NotificationsProps Panel(IReadOnlyList<Notification> rows) => new()
    {
        Filter = NotifyFilter.All, Rows = rows, Now = 10_000, ExpandedId = "", EmptyText = "Nothing new",
        CanUndo = false, UpdateProgress = new FloatSignal(0f), PendingEdits = new Signal<int>(0),
        Host = new FakeNotificationsHost(),
    };

    [Fact]
    public void The_panel_draws_a_row_per_decided_notification_and_five_filter_words_and_never_outgrows_a_300_window()
    {
        ViewHarness.With(host, ViewKeys.OverlayNotifications, Panel(Feed), h =>
        {
            h.RunFrames(60);   // the rows' entrance has run
            Assert.True(h.Contains("ntf:s1"), "a social row");
            Assert.True(h.Contains("ntf:r1"), "a release row");
            Assert.True(h.Contains("ntf:act:a1"), "an activity row");
            Assert.Equal(5, h.FindPrefix(ZuneOverlayRules.FilterKeyPrefix).Count);
            Assert.Equal(380f, h.AbsoluteBounds(h.Node(ZuneOverlayRules.PanelKey)).W, 0.5f);
        }, UnderZune());
        ViewHarness.With(host, ViewKeys.OverlayNotifications, Panel(Feed), h =>
        {
            h.RunFrames(60);
            var panel = h.AbsoluteBounds(h.Node(ZuneOverlayRules.PanelKey));
            Assert.True(panel.W <= 300f - 16f + 0.5f, "the panel is never wider than the window less the tier's margin");
            foreach (var (node, _) in h.FindPrefix(ZuneOverlayRules.FilterKeyPrefix))
                Assert.True(h.AbsoluteBounds(node).H >= 40f - 0.5f, "a Compact filter word is a 40-DIP target");
        }, UnderZune(300f, 660f));
    }

    // ── the account menu ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_account_menu_draws_a_row_per_decided_row_under_a_header_that_names_the_account()
    {
        var props = new ProfileMenuProps
        {
            Known = true, Name = "Ada Lovelace", ShowsTier = true, Premium = true,
            Items = Enumerable.Range(0, 3).Select(static i => new MenuFlyoutItem("Row " + i, default, true, static () => { })).ToArray(),
            Close = static () => { },
        };
        ViewHarness.With(host, ViewKeys.OverlayProfileMenu, props, h =>
        {
            Assert.Equal(3, WithRole(h, AutomationRole.MenuItem));
            Assert.True(h.Contains(ZuneOverlayRules.ProfileHeaderKey));
        }, UnderZune());
    }
}
