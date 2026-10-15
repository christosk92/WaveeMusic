// ── Wavee.ShellTests/OverlaysMountedTests.cs — the overlays and dialog bodies, mounted (themes plan 6.1, T11a) ─────────────────────
//
// The account chip and its menu, the notification panel, the runtime banner, the update toast's card, the offline strip, the context menu's
// tile and the dialog bodies the app builds (the log-out card, the play-a-link card, the signature sheet, the destination picker, the credits
// sheet) are the structure's since T11a. The core keeps every overlay handle, dismiss rule, focus trap, the toast queue, the dialog shells and
// every verb and decision (`Wavee.Tests\OverlayContractTests.cs` pins the pure ones); `ViewHarness` mounts each view alone with fixture props.
// What only a mounted tree can say:
//
//   registered   Classic is the registered template of every key
//   chip         the chip is ONE button, reports its node for the menu's anchor, and the named form is wider than the bare one
//   menu         one menu row per decided row; an unknown tier draws no tier line
//   panel        a row per decided notification under its key; an expanded activity row wraps its card and its detail; an empty feed draws none
//   banner       a button pair while shown, nothing while gated off (it never reflows the page)
//   offline      the retry button exists only when the host can retry
//   cards        the log-out card's two buttons, the play-a-link card's buttons (the browser escape only after a web url failed)
//   picker       a row per filtered item, keyed; the filtered-empty line draws none
//   sheets       the credits sheet's three states and the signature rows

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class OverlaysMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static int Count(ViewHarness harness) => CountUnder(harness.Shell, harness.Shell.Host.Scene.Root);

    static int WithRole(ViewHarness harness, params AutomationRole[] roles)
    {
        var scene = harness.Shell.Host.Scene;
        return harness.Subtree(scene.Root).Count(n => Array.IndexOf(roles, scene.Interaction(n).Role) >= 0);
    }

    static int Buttons(ViewHarness harness) => WithRole(harness, AutomationRole.Button);

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_overlay_and_dialog_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.OverlayProfileChip.Id, "classic.overlay.profile-chip"),
                (ViewKeys.OverlayProfileMenu.Id, "classic.overlay.profile-menu"),
                (ViewKeys.OverlayNotifications.Id, "classic.overlay.notifications"),
                (ViewKeys.OverlayRuntimeBanner.Id, "classic.overlay.runtime-banner"),
                (ViewKeys.OverlayToast.Id, "classic.overlay.toast"),
                (ViewKeys.OverlayOfflineStrip.Id, "classic.overlay.offline-strip"),
                (ViewKeys.OverlayOfflineVacancy.Id, "classic.overlay.offline-vacancy"),
                (ViewKeys.MenuContext.Id, "classic.menu.context-tile"),
                (ViewKeys.DialogLogoutConfirm.Id, "classic.dialog.logout-confirm"),
                (ViewKeys.DialogPlayLink.Id, "classic.dialog.play-link"),
                (ViewKeys.DialogSignature.Id, "classic.dialog.signature"),
                (ViewKeys.DialogFolderPicker.Id, "classic.dialog.folder-picker"),
                (ViewKeys.DialogSheet.Id, "classic.dialog.sheet"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the chip and the menu ────────────────────────────────────────────────────────────────────────────────────────────────

    static ProfileChipProps Chip(bool known, bool showName, Action<NodeHandle>? realized = null) => new()
    {
        Known = known, Name = known ? "Ada Lovelace" : "", ShowName = showName, Presenting = false, NameCap = 76f,
        Open = static () => { }, Realized = realized ?? (static _ => { }),
    };

    [Fact]
    public void The_chip_is_one_button_that_reports_its_node_for_the_menus_anchor()
    {
        NodeHandle realized = default;
        ViewHarness.With(host, ViewKeys.OverlayProfileChip, Chip(known: true, showName: false, h => realized = h), harness =>
        {
            Assert.Equal(1, Buttons(harness));
            Assert.False(realized.IsNull, "the chip hands its node to the core, which anchors the menu to it");
        }, UnderClassic());
    }

    [Fact]
    public void The_named_chip_is_wider_than_the_bare_one_and_an_unknown_identity_keeps_a_skeleton_in_the_name_slot()
    {
        int bare = 0, named = 0, bareUnknown = 0, skeleton = 0;
        ViewHarness.With(host, ViewKeys.OverlayProfileChip, Chip(known: true, showName: false), h => bare = Count(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.OverlayProfileChip, Chip(known: true, showName: true), h => named = Count(h), UnderClassic());
        // An unknown identity draws a plain circle for the avatar (fewer nodes than a PersonPicture), so its name slot is
        // measured against the unknown bare chip, not the known one.
        ViewHarness.With(host, ViewKeys.OverlayProfileChip, Chip(known: false, showName: false), h => bareUnknown = Count(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.OverlayProfileChip, Chip(known: false, showName: true), h => skeleton = Count(h), UnderClassic());
        Assert.True(named > bare, $"the name is drawn beside the avatar: {bare} -> {named}");
        Assert.True(skeleton > bareUnknown, $"the unknown name is a quiet bar, not nothing: {bareUnknown} -> {skeleton}");
    }

    static ProfileMenuProps Menu(bool showsTier, int rows) => new()
    {
        Known = true, Name = "Ada Lovelace", ShowsTier = showsTier, Premium = true,
        Items = Enumerable.Range(0, rows).Select(static i => new MenuFlyoutItem("Row " + i, default, true, static () => { })).ToArray(),
        Close = static () => { },
    };

    [Fact]
    public void The_account_menu_draws_one_row_per_decided_row()
        => ViewHarness.With(host, ViewKeys.OverlayProfileMenu, Menu(showsTier: true, rows: 3),
            harness => Assert.Equal(3, WithRole(harness, AutomationRole.MenuItem)), UnderClassic());

    [Fact]
    public void An_unknown_tier_draws_no_tier_line()
    {
        int with = 0, without = 0;
        ViewHarness.With(host, ViewKeys.OverlayProfileMenu, Menu(showsTier: true, rows: 1), h => with = Count(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.OverlayProfileMenu, Menu(showsTier: false, rows: 1), h => without = Count(h), UnderClassic());
        Assert.True(with > without, $"the tier line sits under the name: {without} -> {with}");
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

    static NotificationsProps Panel(IReadOnlyList<Notification> rows, string expanded = "") => new()
    {
        Filter = NotifyFilter.All, Rows = rows, Now = 10_000, ExpandedId = expanded, EmptyText = "Nothing new",
        CanUndo = false, UpdateProgress = new FloatSignal(0f), PendingEdits = new Signal<int>(0),
        Host = new FakeNotificationsHost(),
    };

    [Fact]
    public void The_panel_draws_a_card_per_decided_row_under_its_key_and_five_filter_pills()
        => ViewHarness.With(host, ViewKeys.OverlayNotifications, Panel(Feed), harness =>
        {
            harness.RunFrames(60);   // the rows' entrance has run
            Assert.True(harness.Contains("ntf:s1"), "a social row");
            Assert.True(harness.Contains("ntf:r1"), "a release row");
            Assert.True(harness.Contains("ntf:act:a1"), "an activity row");
            Assert.False(harness.Contains("ntf:actwrap:a1"), "collapsed: no wrapper, no detail");
            Assert.Equal(5, harness.FindPrefix("notify-filter:").Count);
        }, UnderClassic());

    [Fact]
    public void An_expanded_activity_row_wraps_its_card_and_its_detail()
        => ViewHarness.With(host, ViewKeys.OverlayNotifications, Panel(Feed, expanded: "a1"), harness =>
        {
            harness.RunFrames(60);
            Assert.True(harness.Contains("ntf:actwrap:a1"));
            Assert.True(harness.Contains("ntf:act:a1"));
            var card = harness.AbsoluteBounds(harness.Node("ntf:act:a1"));
            var wrap = harness.AbsoluteBounds(harness.Node("ntf:actwrap:a1"));
            Assert.True(wrap.H > card.H, "the detail (target line and body) sits under the card inside the wrapper");
        }, UnderClassic());

    [Fact]
    public void An_empty_feed_draws_no_rows_and_stays_within_the_panels_width()
        => ViewHarness.With(host, ViewKeys.OverlayNotifications, Panel([]), harness =>
        {
            Assert.Empty(harness.FindPrefix("ntf:"));
            var pill = harness.AbsoluteBounds(harness.FindPrefix("notify-filter:")[0].Node);
            Assert.True(pill.X >= 0f && pill.X + pill.W <= 380f + 0.5f, "the filter rail sits inside the 380 DIP panel");
        }, UnderClassic());

    // ── the banner, the toast card, the offline strip ────────────────────────────────────────────────────────────────────────

    static RuntimeBannerProps Banner(bool show) => new() { Show = show, Message = "Playback needs a local runtime.", Dismiss = static () => { }, SetUp = static () => { } };

    [Fact]
    public void The_banner_draws_its_close_and_set_up_buttons_while_shown_and_nothing_while_gated_off()
    {
        int shown = 0, hidden = 0, hiddenNodes = 0, shownNodes = 0;
        ViewHarness.With(host, ViewKeys.OverlayRuntimeBanner, Banner(show: true), h => { shown = Buttons(h); shownNodes = Count(h); }, UnderClassic());
        ViewHarness.With(host, ViewKeys.OverlayRuntimeBanner, Banner(show: false), h => { hidden = Buttons(h); hiddenNodes = Count(h); }, UnderClassic());
        Assert.Equal(2, shown);
        Assert.Equal(0, hidden);
        Assert.True(shownNodes > hiddenNodes, "gated off it is a bare box: nothing reflows");
    }

    [Fact]
    public void The_update_toast_card_binds_its_bar_to_the_progress_signal_and_the_title()
    {
        var progress = new FloatSignal(0.25f);
        ViewHarness.With(host, ViewKeys.OverlayToast, new ToastCardProps { Title = "Downloading Wavee", Progress = progress }, harness =>
        {
            int before = Count(harness);
            progress.Value = 0.75f;
            harness.RunFrames(3);
            Assert.Equal(before, Count(harness));   // a progress tick is a float write: no reconcile, no new nodes
            Assert.True(before > 2, "the title and the bar are drawn");
        }, UnderClassic());
    }

    [Fact]
    public void The_offline_strip_offers_retry_only_when_the_host_can_retry()
    {
        int with = -1, without = -1;
        ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps { OnRetry = static () => { } }, h => with = Buttons(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps(), h => without = Buttons(h), UnderClassic());
        Assert.Equal(1, with);
        Assert.Equal(0, without);
    }

    [Fact]
    public void The_offline_strip_is_the_boards_36_tall_bar()
        => ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps { OnRetry = static () => { } },
            h => Assert.Equal(ClassicOverlayRules.OfflineStripH, h.AbsoluteBounds(h.Node("offline-strip")).H, 0.5f), UnderClassic());

    [Fact]
    public void The_offline_vacancy_is_a_centred_column_with_retry_and_the_library_link()
        => ViewHarness.With(host, ViewKeys.OverlayOfflineVacancy,
            new OfflineVacancyProps { Page = "Browse", OnRetry = static () => { }, GoToLibrary = static () => { } },
            h =>
            {
                var scene = h.Shell.Host.Scene;
                var root = h.AbsoluteBounds(h.Node("offline-vacancy"));
                var actions = h.Node("offline-vacancy:actions");
                int count = 0;
                for (var c = scene.FirstChild(actions); !c.IsNull; c = scene.NextSibling(c)) count++;
                Assert.Equal(2, count);   // Retry and "Go to Your Library"
                var row = h.AbsoluteBounds(actions);
                Assert.Equal(root.X + root.W / 2f, row.X + row.W / 2f, 1f);                       // centred across the card
                Assert.True(row.Y + row.H < root.Y + root.H / 2f + ClassicOverlayRules.OfflineVacancyLift,
                    "the column sits lifted above the card's centre");
            }, UnderClassic());

    [Fact]
    public void The_offline_vacancy_draws_no_actions_when_the_host_offers_none()
        => ViewHarness.With(host, ViewKeys.OverlayOfflineVacancy, new OfflineVacancyProps(),
            h => Assert.False(h.Contains("offline-vacancy:actions")), UnderClassic());

    // ── the cards ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_logout_card_has_cancel_and_log_out()
        => ViewHarness.With(host, ViewKeys.DialogLogoutConfirm,
            new LogoutConfirmProps { Cancel = static () => { }, Confirm = static () => { } },
            harness => Assert.Equal(2, Buttons(harness)), UnderClassic());

    static PlayLinkProps Link(bool browser, bool canPlay) => new()
    {
        Placeholder = "Paste a link", Text = static () => new Signal<string>(""), Status = "", CanPlay = canPlay, OffersBrowser = browser,
        OpenInBrowser = static () => { }, Cancel = static () => { }, Submit = static () => { },
    };

    [Fact]
    public void The_play_link_card_offers_the_browser_only_after_a_web_url_failed()
    {
        int plain = 0, escape = 0;
        ViewHarness.With(host, ViewKeys.DialogPlayLink, Link(browser: false, canPlay: false), h => plain = Buttons(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.DialogPlayLink, Link(browser: true, canPlay: true), h => escape = Buttons(h), UnderClassic());
        Assert.Equal(2, plain);    // Cancel and Start
        Assert.Equal(3, escape);   // + Open in browser
    }

    // ── the picker ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    static DestinationPickerProps Picker(params Actions.PickerItem[] rows) => new()
    {
        Placeholder = "Search", EmptyText = "No matches", PanelPadding = 0f, Query = static () => new Signal<string>(""),
        Rows = rows, Commit = static _ => { },
    };

    [Fact]
    public void The_picker_draws_a_keyed_row_per_filtered_item()
        => ViewHarness.With(host, ViewKeys.DialogFolderPicker,
            Picker(new Actions.PickerItem("pk:a", "Alpha"), new Actions.PickerItem("pk:b", "Beta", Subtitle: "12 songs", Depth: 1)),
            harness =>
            {
                Assert.True(harness.Contains("pk:a"));
                Assert.True(harness.Contains("pk:b"));
                var a = harness.AbsoluteBounds(harness.Node("pk:a"));
                var b = harness.AbsoluteBounds(harness.Node("pk:b"));
                Assert.True(b.H > 0f && a.H > 0f && b.Y > a.Y, "the rows stack in order");
            }, UnderClassic());

    [Fact]
    public void A_filter_that_matches_nothing_draws_the_empty_line_and_no_rows()
        => ViewHarness.With(host, ViewKeys.DialogFolderPicker, Picker(),
            harness => Assert.Empty(harness.FindPrefix("pk:")), UnderClassic());

    // ── the sheets ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    static CreditsSheetProps Credits(Track.DrawerRules.CreditsView state, params CreditsEntry[] entries)
        => new() { State = state, Entries = entries, OpenArtist = static _ => { } };

    [Fact]
    public void The_credits_sheet_draws_bars_while_unanswered_the_rows_when_answered_and_the_empty_line_otherwise()
    {
        int loading = 0, list = 0, empty = 0;
        ViewHarness.With(host, ViewKeys.DialogSheet, Credits(Track.DrawerRules.CreditsView.Loading), h => loading = Count(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.DialogSheet,
            Credits(Track.DrawerRules.CreditsView.List,
                new CreditsEntry("Performers", "Ada", "Vocals", 0), new CreditsEntry(null, "Grace", "Guitar", 7), new CreditsEntry("Writers", "Alan", "Composer", 0)),
            h => list = Count(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.DialogSheet, Credits(Track.DrawerRules.CreditsView.Empty), h => empty = Count(h), UnderClassic());
        Assert.True(loading > empty, $"four bars stand where the empty line would: {empty} -> {loading}");
        Assert.True(list > empty, $"headings and contributors: {empty} -> {list}");
    }

    [Fact]
    public void The_signature_sheet_draws_a_row_per_fact()
    {
        int few = 0, many = 0;
        static SignatureSheetProps Sig(int rows) => new()
        {
            Summary = "Signed by Wavee", Trusted = true,
            Rows = Enumerable.Range(0, rows).Select(static i => new SignatureSheetRow("Label " + i, i % 2 == 0 ? "value" : null)).ToArray(),
        };
        ViewHarness.With(host, ViewKeys.DialogSignature, Sig(2), h => few = Count(h), UnderClassic());
        ViewHarness.With(host, ViewKeys.DialogSignature, Sig(9), h => many = Count(h), UnderClassic());
        Assert.True(many > few, $"each fact is a label over a value: {few} -> {many}");
    }
}
