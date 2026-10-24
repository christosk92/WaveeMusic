// ── Themes/Zune/Structure/Views/Overlays/ZuneNotifications.cs — Zune's `Overlay.Notifications` (themes plan 6.3, P5 Z8a) ────────────────────────────
//
// Board Z-Overlays ("notifications · 380 wide · filters are sub-pivot words · undo is a word"): the panel's body is a flat plate with the title in the heading voice and its
// two commands as words ("mark all read", "clear"), the filters as a row of sub-pivot words (the selected one white at 600, the others secondary), then the feed: each row
// a 40 art | words | action columns at least 52 tall, the action a lowercase accent word ("undo", "open", "see what's new", "play").
//
// The core panel host (`Shell.NotificationPanelView`) keeps the feed's signals, the filter, the expanded id, the relative-time tick and the update-progress mirror, and hands
// in what passes the filter; every click is a verb on the host (`INotificationsHost`). The rows keep Classic's choreography (they enter from below, leave upward, slide into a
// gap) and the activity row still expands in place under a keyed remount; the unread dot is a reserved column, so a read mark never moves the words.
//
// THE TIERS (`ZuneOverlayTiers`): the panel is never wider than the window less the tier's margin and never taller than the window less the title row; a short window bounds the
// feed, which scrolls inside the panel. At Compact the rows are 56 and every word is a 40-DIP target.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's notification panel.</summary>
internal static class ZuneNotifications
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayNotifications"/>.</summary>
    public static Element Template(NotificationsProps props, in ViewContext ctx)
    {
        var overlay = ctx.Hooks.UseContext(Overlay.Service);   // the release rows' album menu host
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        Size2 vp = ctx.Hooks.UseContextSignal(Viewport.Size).Value;

        // The panel is never wider than the window less the tier's margin, and a short window bounds the feed under its header and filters: the panel scrolls inside itself.
        float panelW = layout.PanelWidth(ZuneOverlayRules.PanelWidth, vp.Width);
        float panelH = layout.PanelHeight(ZuneOverlayRules.PanelMaxHeight, vp.Height);
        float feedMax = layout.PanelBodyHeight(ZuneOverlayRules.FeedMaxHeight, ZuneOverlayRules.PanelMaxHeight, ZuneOverlayRules.PanelFixedHeight, vp.Height);
        float inner = MathF.Max(1f, panelW - ZuneOverlayRules.PanelPadding.Horizontal);

        var rows = new List<Element>(props.Rows.Count);
        for (int i = 0; i < props.Rows.Count; i++)
            rows.Add(Row(props, props.Rows[i], overlay, layout));

        Element body = rows.Count > 0
            ? new ScrollEl
            {
                MaxHeight = feedMax, ContentSized = true, AutoEdgeFade = true, ScrollKey = "notifications",
                Content = new BoxEl { Direction = 1, Width = inner, Children = rows.ToArray() },
            }
            : PanelEmpty(props.EmptyText, inner);

        var pending = props.PendingEdits;
        return new BoxEl
        {
            Key = ZuneOverlayRules.PanelKey, Direction = 1, Width = panelW, MinWidth = panelW, MaxHeight = panelH,
            Padding = ZuneOverlayRules.PanelPadding, Gap = ZuneOverlayRules.PanelGap,
            Children =
            [
                PanelHeader(props, layout),
                FilterWords(props.Filter, props.Host, layout),
                Embed.Comp(() => new PendingSyncRow(pending)) with { Key = "zn:pending-sync" },
                body,
            ],
        };
    }

    // ── the pending-sync line ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"n playlist changes still syncing": its own component, so the outbox draining never re-renders the feed; nothing at 0, which is nearly always.</summary>
    sealed class PendingSyncRow(IReadSignal<int> edits) : Component
    {
        public override Element Render()
        {
            int pending = edits.Value;
            if (pending <= 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.PendingGap,
                Children =
                [
                    Icon(Icons.Refresh, ZuneOverlayRules.PendingIcon, Tok.TextTertiary),
                    R.Type.MicroMeta(Strings.Notifications.PendingSync(pending)) with { Color = Tok.TextSecondary, Grow = 1f, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ],
            };
        }
    }

    // ── the title row and the filter words ───────────────────────────────────────────────────────────────────────────

    static Element PanelHeader(NotificationsProps props, ZuneOverlayLayout layout)
    {
        var kit = new ZuneKit(ZuneSettingsTiers.For(layout.Tier));
        var commands = new List<Element>(2)
        {
            // Mark all read must also clear the OS banners it acknowledges (the verb does): the panel only asks.
            kit.Cmd(Loc.Get(Strings.Notifications.MarkAllRead), props.Host.Verbs.MarkAllRead),
        };
        if (props.Clear is { } clear) commands.Add(kit.Cmd(Loc.Get(Strings.Notifications.Clear), clear));
        return new BoxEl
        {
            Key = "zn:header", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.HeaderWordsGap, MinWidth = 0f,
            Children =
            [
                ZuneText.Heading(Loc.Get(Strings.Notifications.Title)) with { Shrink = 0f, Wrap = TextWrap.NoWrap, Grow = 1f },
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Wrap = true, Gap = ZuneOverlayRules.HeaderWordsGap, MinWidth = 0f, Children = commands.ToArray() },
            ],
        };
    }

    /// <summary>The filters as sub-pivot words: the selected one white at the selected weight, the others secondary (the board's "all · updates · spotify · new · activity").</summary>
    static Element FilterWords(NotifyFilter current, INotificationsHost host, ZuneOverlayLayout layout)
    {
        (string Key, NotifyFilter Filter)[] filters =
        [
            (Strings.Notifications.Filter.All, NotifyFilter.All),
            (Strings.Notifications.Filter.Updates, NotifyFilter.Updates),
            (Strings.Notifications.Filter.Spotify, NotifyFilter.Spotify),
            (Strings.Notifications.Filter.New, NotifyFilter.New),
            (Strings.Notifications.Filter.Activity, NotifyFilter.Activity),
        ];
        ushort selected = (ushort)R.Type.FacetCompactStyle.SelectedWeight;
        var words = new Element[filters.Length];
        for (int i = 0; i < filters.Length; i++)
        {
            var (key, filter) = filters[i];
            bool on = filter == current;
            var text = ZuneText.Cased(R.Type.SubPivot(Loc.Get(key)), LabelKind.Navigation) with
            {
                Color = on ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, MinWidth = 0f,
            };
            if (on) text = text with { Weight = selected };
            words[i] = new BoxEl
            {
                Key = ZuneOverlayRules.FilterKeyPrefix + (int)filter, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = layout.WordHeight,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => host.SetFilter(filter),
                Children = [text],
            }.Interactive(Interaction.Subtle);
        }
        return new BoxEl
        {
            Key = "zn:filters", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.FilterGap, MinWidth = 0f, Children = words,
        };
    }

    // ── the rows ────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Row(NotificationsProps props, in Notification n, IOverlayService? overlay, ZuneOverlayLayout layout)
    {
        var host = props.Host;
        var verbs = host.Verbs;
        var item = n;   // the closures below take the row by value, not the `in` parameter
        return n.Category switch
        {
            NotifyCategory.AppUpdate => Card("ntf:" + n.Id, UpdateRow(props, n.Update ?? AppUpdateSnapshot.Idle, layout), n.IsUnread, null, layout),
            NotifyCategory.Social => Card("ntf:" + n.Id, SocialRow(n, props.Now), n.IsUnread, () => verbs.OpenSocial(item), layout),
            NotifyCategory.NewRelease => ReleaseCard(host, n, overlay, layout),
            _ => ActivityCard(props, n, layout),
        };
    }

    /// <summary>A new-release row: click opens it; an ALBUM release also answers a right-click with the one album menu (the cards' own). An episode release has no container menu.</summary>
    static Element ReleaseCard(INotificationsHost host, in Notification n, IOverlayService? overlay, ZuneOverlayLayout layout)
    {
        var item = n;
        var card = Card("ntf:" + n.Id, ReleaseRow(n), n.IsUnread, () => host.Verbs.OpenRelease(item), layout);
        if (overlay is null || Controls.IsNullOverlay(overlay)) return card;
        return host.ReleaseMenu(n) is { } menu ? card.WithContextMenu(overlay, menu) : card;
    }

    /// <summary>The generic row frame: the tier's least height, the hover wash, the unread column, and the enter / exit / slide choreography.</summary>
    static BoxEl Card(string key, Element content, bool unread, Action? onClick, ZuneOverlayLayout layout) => new BoxEl
    {
        Key = key,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.RowGap, MinHeight = layout.RowMinHeight,
        Padding = new Edges4(0f, ZuneOverlayRules.RowPadY, 0f, ZuneOverlayRules.RowPadY),
        HoverFill = onClick is not null ? R.Layer.RowHover : ColorF.Transparent,
        PressedFill = onClick is not null ? R.Layer.RowPressed : ColorF.Transparent,
        Role = onClick is not null ? AutomationRole.Button : AutomationRole.None,
        Cursor = onClick is not null ? CursorId.Hand : CursorId.Arrow,
        Focusable = onClick is not null,
        OnClick = onClick,
        Enter = new EnterExit(Dy: ZuneOverlayRules.RowEnterDy, Opacity: 0f, Active: true),
        Exit = new EnterExit(Dy: ZuneOverlayRules.RowExitDy, Opacity: 0f, Active: true),
        Layout = LayoutTransition.Slide,
        Children = [content, UnreadDot(unread)],
    };

    static Element UnreadDot(bool unread) => unread
        ? new BoxEl
        {
            Width = ZuneOverlayRules.UnreadDot, Height = ZuneOverlayRules.UnreadDot, Shrink = 0f,
            Corners = CornerRadius4.All(ZuneOverlayRules.UnreadDotRadius), Fill = Tok.AccentDefault,
        }
        : new BoxEl { Width = ZuneOverlayRules.UnreadDot, Shrink = 0f };

    /// <summary>ONE row per update state, from the WHOLE published snapshot; the sentence under the title is the one Settings > About prints. While a download runs the row shows
    /// progress instead of words.</summary>
    static Element UpdateRow(NotificationsProps props, AppUpdateSnapshot s, ZuneOverlayLayout layout)
    {
        var arm = Notify.UpdateRow(s.State);
        var (glyph, tint) = arm.Glyph switch
        {
            UpdateRowGlyph.Download => (Icons.Download, R.Ink.AccentDecor),
            UpdateRowGlyph.Refresh => (Icons.Refresh, R.Ink.AccentDecor),
            UpdateRowGlyph.Success => (Icons.StatusSuccess, Tok.SystemFillSuccess),
            UpdateRowGlyph.Error => (Icons.StatusError, Tok.SystemFillCritical),
            _ => (Icons.StatusInfo, Tok.TextSecondary),
        };
        var kit = new ZuneKit(ZuneSettingsTiers.For(layout.Tier));
        Element trailing;
        if (arm.ShowsProgress)
        {
            trailing = new BoxEl
            {
                Direction = 0, Gap = ZuneOverlayRules.UpdateStripGap, AlignItems = FlexAlign.Center,
                Children =
                [
                    ProgressBar.Create(props.UpdateProgress, ZuneOverlayRules.UpdateBarWidth),
                    R.Type.MicroMeta(s.ProgressPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%") with
                        { Color = Tok.TextTertiary, FontFamily = ZuneOverlayRules.PercentFace },
                ],
            };
        }
        else if (arm.Actions.Length > 0)
        {
            var verbs = props.Host.Verbs;
            var words = new Element[arm.Actions.Length];
            for (int i = 0; i < words.Length; i++)
            {
                var verb = arm.Actions[i];
                words[i] = kit.Cmd(UpdateVerbLabel(verb), () => verbs.RunUpdateRow(verb, s), accent: i == 0);
            }
            trailing = new BoxEl { Direction = 0, Gap = ZuneOverlayRules.UpdateWordsGap, Wrap = true, AlignItems = FlexAlign.Center, Children = words };
        }
        else trailing = new BoxEl();

        string title = arm.TitleLocKey.Length > 0 ? Loc.Get(arm.TitleLocKey) : "";
        string body = Notify.AppUpdateToasts.StateSentence(s);
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = ZuneOverlayRules.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Children =
            [
                GlyphTile(glyph, tint),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = ZuneOverlayRules.UpdateLineGap, MinWidth = 0f,
                    Children =
                    [
                        ZuneText.Cased(R.Type.DenseTitle(title), LabelKind.Label) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        body.Length > 0
                            ? R.Type.MicroMeta(body) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3, MinWidth = 0f }
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
            R.Type.DenseMeta(SpotifyUpdates.CleanTitle(n.Title)) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, MinWidth = 0f },
        };
        if (n.TimestampMs > 0 && n.TimestampMs != NotifyRows.UpdatePin)
            text.Add(R.Type.MicroMeta(RelativeTime(now - n.TimestampMs)) with { Color = Tok.TextTertiary });
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = ZuneOverlayRules.RowGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                // A person: a circle (the one round thing in a feed).
                new BoxEl
                {
                    Width = ZuneOverlayRules.RowArt, Height = ZuneOverlayRules.RowArt, Shrink = 0f,
                    Corners = R.Shape.All(ShapeRole.Avatar), ClipToBounds = true,
                    Children = [Controls.Artwork(n.ImageUrl, ZuneOverlayRules.RowArt, ZuneOverlayRules.RowArt, ZuneOverlayRules.RowArt / 2f, decodePx: ZuneOverlayRules.RowArtDecodePx)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = ZuneOverlayRules.LineGap, MinWidth = 0f, Children = text.ToArray() },
            ],
        };
    }

    static Element ReleaseRow(in Notification n) => new BoxEl
    {
        Direction = 0, Grow = 1f, Basis = 0f, Gap = ZuneOverlayRules.RowGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Width = ZuneOverlayRules.RowArt, Height = ZuneOverlayRules.RowArt, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
                Children = [Controls.Artwork(n.ImageUrl, ZuneOverlayRules.RowArt, ZuneOverlayRules.RowArt, R.Shape.Cover, decodePx: ZuneOverlayRules.RowArtDecodePx)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = ZuneOverlayRules.LineGap, MinWidth = 0f,
                Children =
                [
                    R.Type.TrackTitle(n.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    R.Type.MicroMeta(n.Creator ?? "") with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            },
            // The release's kind ("album", "single", "episode") is a quiet word at the row's end.
            ZuneText.Cased(R.Type.MicroMeta(Loc.Get(Notify.ReleaseTypeKey(n.ReleaseKind, n.AlbumType))), LabelKind.Label)
                with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 },
        ],
    };

    /// <summary>A local library mutation: summary + age + undo (when the journal offers it); a click expands the detail (swapping the card's KEY, so the expansion is a keyed
    /// remount the slide choreography carries).</summary>
    static Element ActivityCard(NotificationsProps props, in Notification n, ZuneOverlayLayout layout)
    {
        var host = props.Host;
        var item = n;
        var kit = new ZuneKit(ZuneSettingsTiers.For(layout.Tier));
        bool isExpanded = props.ExpandedId == n.Id;
        var right = new List<Element>(2)
        {
            R.Type.MicroMeta(RelativeTime(props.Now - n.TimestampMs)) with { Color = Tok.TextTertiary, Shrink = 0f },
        };
        if (props.CanUndo)
            right.Add(kit.Cmd(Loc.Get(Strings.Notifications.Undo), () => host.Undo(item), accent: true));

        var card = new BoxEl
        {
            Key = "ntf:act:" + n.Id,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.RowGap, MinHeight = layout.RowMinHeight,
            Padding = new Edges4(0f, ZuneOverlayRules.RowPadY, 0f, ZuneOverlayRules.RowPadY),
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => host.ToggleActivity(item),
            Enter = new EnterExit(Dy: ZuneOverlayRules.RowEnterDy, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: ZuneOverlayRules.RowExitDy, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Basis = 0f, Gap = ZuneOverlayRules.RowGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        GlyphTile(Icons.StatusInfo, Tok.TextSecondary),
                        R.Type.DenseMeta(n.Title) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary },
                        new BoxEl { Direction = 1, Shrink = 0f, AlignItems = FlexAlign.End, Gap = ZuneOverlayRules.LineGap, Children = right.ToArray() },
                    ],
                },
                UnreadDot(n.IsUnread),
            ],
        };
        if (!isExpanded) return card;
        return new BoxEl
        {
            Key = "ntf:actwrap:" + n.Id, Direction = 1,
            Children = [card, ActivityDetail(host, n, kit)],
        };
    }

    /// <summary>The target line (always present, so expanding is never a visual no-op) with an "open" jump when the subject routes, then the journal's detail sentence.</summary>
    static Element ActivityDetail(INotificationsHost host, in Notification n, ZuneKit kit)
    {
        var kids = new List<Element>(2);
        if (host.ActivityTarget(n) is { } target)
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.DetailTargetGap,
                Children =
                [
                    R.Type.DenseMeta(target.Text) with { Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 2, Wrap = TextWrap.Wrap },
                    target.Open is { } open ? kit.Cmd(Loc.Get(Strings.Notifications.Activity.Detail.Open), open, accent: true) : new BoxEl(),
                ],
            });
        if (n.Body.Length > 0)
            kids.Add(R.Type.MicroMeta(n.Body) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f });
        return new BoxEl { Direction = 1, Gap = ZuneOverlayRules.DetailGap, Margin = ZuneOverlayRules.DetailMargin, Children = kids.ToArray() };
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
        Key = "zn:empty", Direction = 1, Width = width, MinHeight = ZuneOverlayRules.EmptyMinHeight,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = Edges4.All(ZuneOverlayRules.EmptyPadding),
        Children = [R.Type.DenseTitle(message) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = ZuneOverlayRules.EmptyMessageMaxWidth }],
    };

    /// <summary>A flat tile for a row with no picture of its own: the art's size, the placeholder ground, one glyph.</summary>
    static Element GlyphTile(string glyph, ColorF tint) => new BoxEl
    {
        Width = ZuneOverlayRules.RowArt, Height = ZuneOverlayRules.RowArt, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = R.Shape.All(ShapeRole.Cover), Fill = R.Layer.ArtPlaceholder,
        Children = [Icon(glyph, ZuneOverlayRules.RowGlyph, tint)],
    };
}
