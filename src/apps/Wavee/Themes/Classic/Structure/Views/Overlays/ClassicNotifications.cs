// ── Themes/Classic/Structure/Views/Overlays/ClassicNotifications.cs — Classic's `Overlay.Notifications` (themes plan 6.1, T11a) ─────
//
// The notification panel's body, moved whole from `Shell.UI.cs` (ch 19 W14-W17, A9): the header (title, "Mark all read", "Clear" on the
// activity filter), the filter pills on the shared chip rail, the pending-sync line, then the feed's rows over a bounded, edge-faded scroll:
// the update row (one per update state, with a progress strip or its pills), a social row, a new-release row (an album answers a
// right-click with the container menu), and an activity row that expands in place (a keyed remount the slide choreography carries). Each row
// is a 56-DIP card with the hover rung, the unread dot and the enter, exit and slide choreography.
//
// The core panel host (`Shell.NotificationPanelView`) keeps the feed's signals, the filter, the expanded id, the relative-time tick and the
// update-progress mirror, and hands in what passes the filter; every click is a verb on the host (`INotificationsHost`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's notification panel.</summary>
internal static class ClassicNotifications
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayNotifications"/>.</summary>
    public static Element Template(NotificationsProps props, in ViewContext ctx)
    {
        var overlay = ctx.Hooks.UseContext(Overlay.Service);   // the release rows' album menu host
        var host = props.Host;

        // The panel is never wider than the window less the band's margin, and a short window bounds the feed under its header and chip rail: the panel
        // scrolls inside itself (TC9). The nominal sizes are the tuned panel's, so a roomy window draws exactly what it always did.
        var tier = ClassicOverlayTiers.For(ctx.Tier().Window);
        Size2 vp = ctx.Hooks.UseContextSignal(Viewport.Size).Value;
        float panelW = tier.PanelWidth(ClassicNotificationRules.PanelWidth, vp.Width);
        float panelH = tier.PanelHeight(ClassicNotificationRules.PanelMaxHeight, vp.Height);
        float feedMax = tier.PanelBodyHeight(ClassicNotificationRules.FeedMaxHeight, ClassicNotificationRules.PanelMaxHeight, ClassicNotificationRules.FixedHeight, vp.Height);

        var rows = new List<Element>(props.Rows.Count);
        for (int i = 0; i < props.Rows.Count; i++)
            rows.Add(Row(props, props.Rows[i], overlay));

        Element body = rows.Count > 0
            ? new ScrollEl
            {
                MaxHeight = feedMax, ContentSized = true, AutoEdgeFade = true, ScrollKey = "notifications",
                Content = new BoxEl
                {
                    Direction = 1, Width = panelW, Padding = ClassicNotificationRules.FeedPadding,
                    Gap = ClassicNotificationRules.FeedGap,
                    Children = rows.ToArray(),
                },
            }
            : PanelEmpty(props.EmptyText, panelW);

        var pending = props.PendingEdits;
        return new BoxEl
        {
            Direction = 1, Width = panelW, MinWidth = panelW,
            MaxHeight = panelH,
            Children =
            [
                PanelHeader(props),
                FilterPills(props.Filter, host),
                Embed.Comp(() => new PendingSyncRow(pending)) with { Key = "nc-pending-sync" },
                body,
            ],
        };
    }

    // ── the pending-sync line ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"n playlist changes still syncing" — the app-wide answer to "did that actually happen?". Its own component
    /// so the outbox draining never re-renders the feed; nothing at 0, which is nearly always.</summary>
    sealed class PendingSyncRow(IReadSignal<int> edits) : Component
    {
        public override Element Render()
        {
            int pending = edits.Value;
            if (pending <= 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicNotificationRules.PendingGap, Padding = ClassicNotificationRules.PendingPadding,
                Children =
                [
                    Icon(Icons.Refresh, ClassicNotificationRules.PendingIcon, Tok.TextTertiary),
                    new TextEl(Strings.Notifications.PendingSync(pending)) { Size = ClassicNotificationRules.PendingTextSize, Color = Tok.TextSecondary, Grow = 1f },
                ],
            };
        }
    }

    // ── header and pills ────────────────────────────────────────────────────────────────────────────────────────────

    static Element PanelHeader(NotificationsProps props)
    {
        var kids = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Notifications.Title)) { Size = ClassicNotificationRules.HeaderTitleSize, Weight = 700, Color = Tok.TextPrimary, Grow = 1f },
            // G-090: Mark all read must also clear the OS banners it acknowledges — otherwise every live toast this
            // session raised keeps sitting in the Action Center after the in-app centre says everything is read.
            PanelLink(Loc.Get(Strings.Notifications.MarkAllRead), props.Host.Verbs.MarkAllRead),
        };
        if (props.Clear is { } clear)
            kids.Add(PanelLink(Loc.Get(Strings.Notifications.Clear), clear));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicNotificationRules.HeaderGap, Padding = ClassicNotificationRules.HeaderPadding,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The filter chips on the shared one-line rail (it scrolls with an edge fade if the flyout is too narrow).</summary>
    static Element FilterPills(NotifyFilter current, INotificationsHost host) => new BoxEl
    {
        Padding = ClassicNotificationRules.FilterPadding,
        Children =
        [
            Controls.ChipRail(
            [
                FilterPill(Strings.Notifications.Filter.All, NotifyFilter.All, current, host),
                FilterPill(Strings.Notifications.Filter.Updates, NotifyFilter.Updates, current, host),
                FilterPill(Strings.Notifications.Filter.Spotify, NotifyFilter.Spotify, current, host),
                FilterPill(Strings.Notifications.Filter.New, NotifyFilter.New, current, host),
                FilterPill(Strings.Notifications.Filter.Activity, NotifyFilter.Activity, current, host),
            ], "notify.filters"),
        ],
    };

    /// <summary>The app's one filter chip (<see cref="Controls.Chip"/>); exclusive, so a click on the active one is a no-op.</summary>
    static Element FilterPill(string labelKey, NotifyFilter pill, NotifyFilter current, INotificationsHost host)
        => Controls.Chip(Loc.Get(labelKey), pill == current, true, () => host.SetFilter(pill)) with { Key = "notify-filter:" + (int)pill };

    // ── the rows ────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Row(NotificationsProps props, in Notification n, IOverlayService? overlay)
    {
        var host = props.Host;
        var verbs = host.Verbs;
        var item = n;   // the closures below take the row by value, not the `in` parameter
        return n.Category switch
        {
            NotifyCategory.AppUpdate => NotificationCard("ntf:" + n.Id, UpdateRow(props, n.Update ?? AppUpdateSnapshot.Idle), n.IsUnread, null),
            NotifyCategory.Social => NotificationCard("ntf:" + n.Id, SocialRow(n, props.Now), n.IsUnread, () => verbs.OpenSocial(item)),
            NotifyCategory.NewRelease => ReleaseCard(host, n, overlay),
            _ => ActivityCard(props, n),
        };
    }

    /// <summary>A new-release row: click opens it; an ALBUM release also answers a right-click with the one album menu
    /// (the cards' own — Play next, Add to queue, Save, Go to artist…). An episode release has no container menu.</summary>
    static Element ReleaseCard(INotificationsHost host, in Notification n, IOverlayService? overlay)
    {
        var item = n;
        var card = NotificationCard("ntf:" + n.Id, ReleaseRow(n), n.IsUnread, () => host.Verbs.OpenRelease(item));
        if (overlay is null || Controls.IsNullOverlay(overlay)) return card;
        return host.ReleaseMenu(n) is { } menu ? card.WithContextMenu(overlay, menu) : card;
    }

    /// <summary>The generic row frame: 56 min, r8, the hover rung, the unread dot, and the enter/exit/slide choreography.</summary>
    static BoxEl NotificationCard(string key, Element content, bool unread, Action? onClick) => new BoxEl
    {
        Key = key,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicNotificationRules.CardGap, MinHeight = ClassicNotificationRules.CardMinHeight,
        Padding = ClassicNotificationRules.CardPadding, Corners = CornerRadius4.All(ClassicNotificationRules.CardRadius),
        HoverFill = onClick is not null ? R.Layer.RowHover : ColorF.Transparent,
        PressedFill = onClick is not null ? R.Layer.RowPressed : ColorF.Transparent,
        Role = onClick is not null ? AutomationRole.Button : AutomationRole.None,
        Cursor = onClick is not null ? CursorId.Hand : CursorId.Arrow,
        Focusable = onClick is not null,
        OnClick = onClick,
        Enter = new EnterExit(Dy: ClassicNotificationRules.CardEnterDy, Opacity: 0f, Active: true),
        Exit = new EnterExit(Dy: ClassicNotificationRules.CardExitDy, Opacity: 0f, Active: true),
        Layout = LayoutTransition.Slide,
        Children = [content, UnreadDot(unread)],
    };

    static Element UnreadDot(bool unread) => unread
        ? new BoxEl
        {
            Width = ClassicNotificationRules.UnreadDot, Height = ClassicNotificationRules.UnreadDot, Shrink = 0f,
            Corners = CornerRadius4.All(ClassicNotificationRules.UnreadDotRadius), Fill = Tok.AccentDefault,
        }
        : new BoxEl { Width = ClassicNotificationRules.UnreadDot, Shrink = 0f };

    /// <summary>ONE row per update state, from the WHOLE published snapshot; the sentence under the title is the one
    /// Settings ▸ About prints. While a download runs the row shows progress instead of buttons.</summary>
    static Element UpdateRow(NotificationsProps props, AppUpdateSnapshot s)
    {
        var arm = Notify.UpdateRow(s.State);
        var (glyph, tint) = arm.Glyph switch
        {
            UpdateRowGlyph.Download => (Icons.Download, Tok.AccentDefault),
            UpdateRowGlyph.Refresh => (Icons.Refresh, Tok.AccentDefault),
            UpdateRowGlyph.Success => (Icons.StatusSuccess, Tok.SystemFillSuccess),
            UpdateRowGlyph.Error => (Icons.StatusError, Tok.SystemFillCritical),
            _ => (Icons.StatusInfo, Tok.TextSecondary),
        };
        Element trailing;
        if (arm.ShowsProgress)
        {
            trailing = new BoxEl
            {
                Direction = 0, Gap = ClassicNotificationRules.UpdateStripGap, AlignItems = FlexAlign.Center, Margin = ClassicNotificationRules.UpdateStripMargin,
                Children =
                [
                    ProgressBar.Create(props.UpdateProgress, ClassicNotificationRules.UpdateBarWidth),
                    R.Type.MicroMeta(s.ProgressPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%") with
                        { Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
                ],
            };
        }
        else if (arm.Actions.Length > 0)
        {
            var verbs = props.Host.Verbs;
            var buttons = new Element[arm.Actions.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                var verb = arm.Actions[i];
                buttons[i] = PanelPill(UpdateVerbLabel(verb), () => verbs.RunUpdateRow(verb, s), accent: i == 0);
            }
            trailing = new BoxEl
            {
                Direction = 0, Gap = ClassicNotificationRules.UpdateButtonsGap, Wrap = true, Margin = ClassicNotificationRules.UpdateButtonsMargin,
                Children = buttons,
            };
        }
        else trailing = new BoxEl();

        string title = arm.TitleLocKey.Length > 0 ? Loc.Get(arm.TitleLocKey) : "";
        string body = Notify.AppUpdateToasts.StateSentence(s);
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = ClassicNotificationRules.CardGap, AlignItems = FlexAlign.Start,
            Children =
            [
                GlyphChip(glyph, tint),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = ClassicNotificationRules.UpdateColumnGap, MinWidth = 0f,
                    Children =
                    [
                        R.Type.DenseTitle(title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        body.Length > 0
                            ? new TextEl(body) { Size = ClassicNotificationRules.UpdateBodySize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3 }
                            : new BoxEl(),
                        trailing,
                    ],
                },
            ],
        };
    }

    static string UpdateVerbLabel(UpdateRowAction verb) => Loc.Get(verb switch
    {
        UpdateRowAction.UpdateNow => Strings.Update.Action.UpdateNow,
        UpdateRowAction.WhatsNew => Strings.Update.Action.WhatsNew,
        UpdateRowAction.Later => Strings.Update.Action.Later,
        UpdateRowAction.Retry => Strings.Update.Action.Retry,
        UpdateRowAction.OpenReleasePage => Strings.Update.Action.OpenReleasePage,
        _ => Strings.Update.Action.Dismiss,
    });

    static Element SocialRow(in Notification n, long now)
    {
        var text = new List<Element>(2)
        {
            R.Type.DenseMeta(SpotifyUpdates.CleanTitle(n.Title)) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2 },
        };
        if (n.TimestampMs > 0 && n.TimestampMs != NotifyRows.UpdatePin)
            text.Add(R.Type.MicroMeta(RelativeTime(now - n.TimestampMs)) with { Weight = 600, Color = Tok.TextTertiary });
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = ClassicNotificationRules.CardGap, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = ClassicNotificationRules.SocialAvatar, Height = ClassicNotificationRules.SocialAvatar, Shrink = 0f,
                    Corners = CornerRadius4.All(ClassicNotificationRules.SocialAvatarRadius), ClipToBounds = true,
                    Children = [Controls.Artwork(n.ImageUrl, ClassicNotificationRules.SocialAvatar, ClassicNotificationRules.SocialAvatar, ClassicNotificationRules.SocialAvatarRadius)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = ClassicNotificationRules.ColumnGap, Children = text.ToArray() },
            ],
        };
    }

    static Element ReleaseRow(in Notification n) => new BoxEl
    {
        Direction = 0, Grow = 1f, Basis = 0f, Gap = ClassicNotificationRules.CardGap, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = ClassicNotificationRules.ReleaseCover, Height = ClassicNotificationRules.ReleaseCover, Shrink = 0f,
                Corners = CornerRadius4.All(ClassicNotificationRules.ReleaseCoverRadius), ClipToBounds = true,
                Children = [Controls.Artwork(n.ImageUrl, ClassicNotificationRules.ReleaseCover, ClassicNotificationRules.ReleaseCover, ClassicNotificationRules.ReleaseCoverRadius)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = ClassicNotificationRules.ColumnGap, MinWidth = 0f,
                Children =
                [
                    R.Type.DenseTitle(n.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(n.Creator ?? "") { Size = ClassicNotificationRules.ReleaseCreatorSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
            PanelTypePill(Loc.Get(Notify.ReleaseTypeKey(n.ReleaseKind, n.AlbumType))),
        ],
    };

    /// <summary>A local library mutation: summary + age + Undo (when the journal offers it); a click expands the detail
    /// (swapping the card's KEY, so the expansion is a keyed remount the slide choreography carries).</summary>
    static Element ActivityCard(NotificationsProps props, in Notification n)
    {
        var host = props.Host;
        var item = n;
        bool isExpanded = props.ExpandedId == n.Id;
        var right = new List<Element>(2)
        {
            R.Type.MicroMeta(RelativeTime(props.Now - n.TimestampMs)) with { Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
        };
        if (props.CanUndo)
            right.Add(PanelPill(Loc.Get(Strings.Notifications.Undo), () => host.Undo(item), accent: false));

        var card = new BoxEl
        {
            Key = "ntf:act:" + n.Id,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicNotificationRules.CardGap, MinHeight = ClassicNotificationRules.CardMinHeight,
            Padding = ClassicNotificationRules.CardPadding, Corners = CornerRadius4.All(ClassicNotificationRules.CardRadius),
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => host.ToggleActivity(item),
            Enter = new EnterExit(Dy: ClassicNotificationRules.CardEnterDy, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: ClassicNotificationRules.CardExitDy, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Basis = 0f, Gap = ClassicNotificationRules.CardGap, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        GlyphChip(Icons.StatusInfo, Tok.TextSecondary),
                        R.Type.DenseMeta(n.Title) with { Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary },
                        new BoxEl { Direction = 1, Shrink = 0f, AlignItems = FlexAlign.End, Gap = ClassicNotificationRules.ActivityRightGap, Children = right.ToArray() },
                    ],
                },
                UnreadDot(n.IsUnread),
            ],
        };
        if (!isExpanded) return card;
        return new BoxEl
        {
            Key = "ntf:actwrap:" + n.Id, Direction = 1, Gap = ClassicNotificationRules.FeedGap,
            Children = [card, ActivityDetail(host, n)],
        };
    }

    /// <summary>The target line (always present, so expanding is never a visual no-op) with an Open jump when the
    /// subject routes, then the journal's detail sentence.</summary>
    static Element ActivityDetail(INotificationsHost host, in Notification n)
    {
        var kids = new List<Element>(2);
        if (host.ActivityTarget(n) is { } target)
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicNotificationRules.DetailTargetGap,
                Children =
                [
                    new TextEl(target.Text) { Size = ClassicNotificationRules.DetailTextSize, Weight = 600, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap },
                    target.Open is { } open
                        ? PanelPill(Loc.Get(Strings.Notifications.Activity.Detail.Open), open, accent: false)
                        : new BoxEl(),
                ],
            });
        if (n.Body.Length > 0)
            kids.Add(new TextEl(n.Body) { Size = ClassicNotificationRules.DetailTextSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        return new BoxEl { Direction = 1, Gap = ClassicNotificationRules.DetailGap, Margin = ClassicNotificationRules.DetailMargin, Children = kids.ToArray() };
    }

    static string RelativeTime(long ageMs)
    {
        var (unit, count) = Notify.RelativeAge(ageMs);
        return unit switch
        {
            RelativeUnit.Now => Loc.Get(Strings.Friends.Now),
            RelativeUnit.Minutes => Strings.Friends.MinAgo(count),
            RelativeUnit.Hours => Strings.Friends.HrAgo(count),
            _ => Strings.Friends.DAgo(count),
        };
    }

    // ── the pieces ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element PanelEmpty(string message, float width) => new BoxEl
    {
        Direction = 1, Width = width, MinHeight = ClassicNotificationRules.EmptyMinHeight,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = Edges4.All(ClassicNotificationRules.EmptyPadding),
        Children = [R.Type.DenseTitle(message) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = ClassicNotificationRules.EmptyMessageMaxWidth }],
    };

    static Element GlyphChip(string glyph, ColorF tint) => new BoxEl
    {
        Width = ClassicNotificationRules.GlyphChip, Height = ClassicNotificationRules.GlyphChip, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(ClassicNotificationRules.GlyphChipRadius), Fill = Tok.FillSubtleSecondary,
        Children = [Icon(glyph, ClassicNotificationRules.GlyphIcon, tint)],
    };

    static Element PanelTypePill(string label) => new BoxEl
    {
        Shrink = 0f, Padding = ClassicNotificationRules.TypePillPadding, Corners = CornerRadius4.All(ClassicNotificationRules.TypePillRadius),
        Fill = Tok.FillSubtleSecondary,
        Children = [R.Type.Eyebrow(label) with { Color = Tok.TextTertiary }],
    };

    static Element PanelPill(string label, Action onClick, bool accent) => new BoxEl
    {
        Shrink = 0f, MinHeight = ClassicNotificationRules.PillMinHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = ClassicNotificationRules.PillPadding, Corners = CornerRadius4.All(ClassicNotificationRules.PillRadius),
        Fill = accent ? Tok.AccentDefault : Tok.FillControlDefault,
        HoverFill = accent ? Tok.AccentSecondary : Tok.FillControlSecondary,
        PressedFill = accent ? Tok.AccentTertiary : Tok.FillControlTertiary,
        BorderWidth = accent ? 0f : 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
        OnClick = onClick,
        Children = [new TextEl(label) { Size = ClassicNotificationRules.PillTextSize, Weight = 600, Color = accent ? Tok.TextOnAccentPrimary : Tok.TextPrimary }],
    };

    static Element PanelLink(string label, Action onClick) => new BoxEl
    {
        Shrink = 0f, MinHeight = ClassicNotificationRules.PillMinHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = ClassicNotificationRules.LinkPadding, Corners = CornerRadius4.All(ClassicNotificationRules.LinkRadius),
        HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = onClick,
        Children = [new TextEl(label) { Size = ClassicNotificationRules.LinkTextSize, Weight = 600, Color = Tok.AccentTextPrimary }],
    };
}
