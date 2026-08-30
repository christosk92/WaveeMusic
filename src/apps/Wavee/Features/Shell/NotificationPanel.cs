using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The notification center's panel ───────────────────────────────────────────────────────────────────────────────────
// There is no standalone bell any more. The unread badge rides the PROFILE CHIP's avatar and a "Notifications" row in
// the profile flyout opens this panel anchored to that chip (ProfileMenu.OpenNotifications, which marks unread-seen via
// NotificationCenterBridge.OnPanelOpened exactly as the bell did). This component is the body it shows.
sealed class NotificationPanel : Component
{
    const float Width = 380f;
    const int TickMs = 30_000;

    readonly Action? _close;   // dismiss the flyout when a click navigates away (a floating panel over a new page reads as stuck)

    public NotificationPanel(Action? close = null) => _close = close;

    public override Element Render()
    {
        var nc = UseContext(NotificationCenterBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var svc = UseContext(Services.Slot);
        var expanded = UseSignal<long>(-1);

        // 30-second refresh so relative times advance while the panel is open: a frame-clock interval that AUTO-PAUSES
        // while parked/minimized (idle quiesce) — replaces the old System.Threading.Timer + post marshal.
        UseInterval(() => { if (nc is not null) nc.NowTick.Value = nc.NowTick.Peek() + 1; }, TickMs, enabled: nc is not null);

        if (nc is null) return new BoxEl { Width = Width };

        var all = nc.Items.Value;               // subscribe → re-render on any push/rebuild
        var filter = nc.Filter.Value;           // subscribe → re-render on filter change
        var socialState = nc.SocialState.Value; // subscribe → empty view distinguishes loading/error/offline
        var whatsNewState = nc.WhatsNewState.Value;
        _ = nc.NowTick.Value;                   // subscribe → relative times advance
        long expandedId = expanded.Value;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var rows = new List<Element>(all.Count);
        foreach (var n in all)
        {
            if (filter is { } f && n.Category != f) continue;
            rows.Add(RowFor(n, now, nc, go, svc, expanded, expandedId, _close));
        }

        Element body = rows.Count > 0
            ? new ScrollEl
            {
                MaxHeight = 460f, ContentSized = true, AutoEdgeFade = true, ScrollKey = "notifications",
                Content = new BoxEl { Direction = 1, Width = Width, Padding = new Edges4(6f, 4f, 6f, 8f), Gap = 2f, Children = rows.ToArray() },
            }
            : EmptyState(filter, socialState, whatsNewState);

        return new BoxEl
        {
            Direction = 1, Width = Width, MinWidth = Width, MaxHeight = 520f,
            Children = [ Header(nc, filter), FilterPills(nc, filter), PendingSyncLine(), body ],
        };
    }

    /// <summary>One line: how many playlist edits this session has made that the server has not acked yet.
    /// <para>It lives HERE because this is where the user comes to ask "did that actually happen?" — the per-page
    /// chip answers it for one playlist, and nothing answered it for the app. A Component so it reads
    /// <c>LibraryBridge.PendingEditsTotal</c> directly and appears/disappears without re-rendering the feed.
    /// Renders nothing at 0, which is nearly always.</para></summary>
    static Element PendingSyncLine() => Embed.Comp(() => new PendingSyncRow()) with { Key = "nc-pending-sync" };

    sealed class PendingSyncRow : Component
    {
        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            if (lib is null) return new BoxEl { Height = 0f, HitTestVisible = false };
            int pending = lib.PendingEditsTotal.Value;   // subscribe → the line tracks the outbox draining
            if (pending <= 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                Padding = new Edges4(14f, 4f, 14f, 8f),
                Children =
                [
                    Icon(Icons.Refresh, 12f, Tok.TextTertiary),
                    new TextEl(Strings.Notifications.PendingSync(pending)) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f },
                ],
            };
        }
    }

    // ── header ───────────────────────────────────────────────────────────────────────────────────────────────────────
    static Element Header(NotificationCenterBridge nc, NotificationCategory? filter)
    {
        var actions = new List<Element>(2)
        {
            LinkButton(Loc.Get(Strings.Notifications.MarkAllRead), nc.MarkAllRead),
        };
        if (filter == NotificationCategory.Activity)
            actions.Add(LinkButton(Loc.Get(Strings.Notifications.Clear), nc.ClearActivity));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Padding = new Edges4(14f, 12f, 8f, 8f),
            Children =
            [
                new TextEl(Loc.Get(Strings.Notifications.Title)) { Size = 15f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f },
                ..actions,
            ],
        };
    }

    static Element FilterPills(NotificationCenterBridge nc, NotificationCategory? filter)
    {
        Element Pill(string label, NotificationCategory? cat) => new BoxEl
        {
            Shrink = 0f, MinHeight = 26f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(11f, 3f, 11f, 3f), Corners = CornerRadius4.All(13f),
            Fill = filter == cat ? Tok.AccentDefault : Tok.FillSubtleSecondary,
            HoverFill = filter == cat ? Tok.AccentSecondary : WaveeColors.RowHover,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => nc.SetFilter(cat),
            Children = [ new TextEl(label) { Size = 12f, Weight = 600, Color = filter == cat ? Tok.TextOnAccentPrimary : Tok.TextSecondary } ],
        };

        return new BoxEl
        {
            Direction = 0, Gap = 6f, Padding = new Edges4(12f, 2f, 12f, 8f),
            Children =
            [
                Pill(Loc.Get(Strings.Notifications.Filter.All), null),
                Pill(Loc.Get(Strings.Notifications.Filter.Updates), NotificationCategory.AppUpdate),
                Pill(Loc.Get(Strings.Notifications.Filter.Spotify), NotificationCategory.Social),
                Pill(Loc.Get(Strings.Notifications.Filter.New), NotificationCategory.NewRelease),
                Pill(Loc.Get(Strings.Notifications.Filter.Activity), NotificationCategory.Activity),
            ],
        };
    }

    // ── row dispatch ─────────────────────────────────────────────────────────────────────────────────────────────────
    static Element RowFor(WaveeNotification n, long now, NotificationCenterBridge nc, Action<string, string?>? go,
        Services? svc, Signal<long> expanded, long expandedId, Action? close) => n switch
    {
        AppUpdateNotification u => Card("ntf:" + u.Id, UpdateRow(u, svc, go, close), n.IsUnread, null),
        SocialNotification s    => Card("ntf:" + s.Id, SocialRow(s, now, go), n.IsUnread, () => ClickSocial(s, go, close)),
        NewReleaseNotification r => Card("ntf:" + r.Id, NewReleaseRow(r), n.IsUnread, () => ClickRelease(r, go, close)),
        ActivityNotification a  => ActivityCard(a, now, nc, expanded, expandedId, go, close),
        _ => new BoxEl(),
    };

    // A generic clickable row frame with an optional unread dot.
    static Element Card(string key, Element content, bool unread, Action? onClick) => new BoxEl
    {
        Key = key,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, MinHeight = 56f,
        Padding = new Edges4(10f, 8f, 10f, 8f), Corners = CornerRadius4.All(8f),
        HoverFill = onClick is not null ? WaveeColors.RowHover : ColorF.Transparent,
        PressedFill = onClick is not null ? WaveeColors.RowPressed : ColorF.Transparent,
        Role = onClick is not null ? AutomationRole.Button : AutomationRole.None,
        Cursor = onClick is not null ? CursorId.Hand : CursorId.Arrow,
        Focusable = onClick is not null,
        OnClick = onClick,
        Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
        Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
        Layout = LayoutTransition.Slide,
        Children = [ content, UnreadDot(unread) ],
    };

    static Element UnreadDot(bool unread) => unread
        ? new BoxEl { Width = 8f, Height = 8f, Shrink = 0f, Corners = CornerRadius4.All(4f), Fill = Tok.AccentDefault }
        : new BoxEl { Width = 8f, Shrink = 0f };

    // ── app update ───────────────────────────────────────────────────────────────────────────────────────────────────
    // ONE row per state, and the state is the WHOLE snapshot the service published (never a torn read of four
    // properties). The sentence under the title is the same one Settings › About prints — a second phrasing of
    // "Wavee 0.3 Crest is available" is a second thing to keep in step for no gain.
    static Element UpdateRow(AppUpdateNotification u, Services? svc, Action<string, string?>? go, Action? close)
    {
        var s = u.Snapshot;
        var (glyph, tint, title) = s.State switch
        {
            AppUpdateState.Available or AppUpdateState.Snoozed =>
                (Icons.Download, Tok.AccentDefault, Loc.Get(Strings.Notifications.Update.AvailableTitle)),
            AppUpdateState.Downloading or AppUpdateState.Installing =>
                (Icons.Refresh, Tok.AccentDefault, Loc.Get(Strings.Update.Os.Downloading)),
            AppUpdateState.Completed =>
                (Icons.StatusSuccess, Tok.SystemFillSuccess, Loc.Get(Strings.Notifications.Update.CompletedTitle)),
            AppUpdateState.Failed =>
                (Icons.StatusError, Tok.SystemFillCritical, Loc.Get(Strings.Notifications.Update.FailedTitle)),
            _ => (Icons.StatusInfo, Tok.TextSecondary, ""),
        };

        // The SIMULATOR when a developer-mode walk is running, else the live updater. This row is the one a
        // simulation injects, so binding its buttons to the real (Idle) service left "Update now" / "Later" / "Retry"
        // doing nothing at all during exactly the scenario they exist to exercise.
        var up = AppUpdateSurface.Resolve(svc);
        // The release the actions talk about: the TARGET while one is pending, the running build once it landed.
        string notesVersion = s.State == AppUpdateState.Completed
            ? AppVersion.Info.Core
            : s.TargetSemVer is { Length: > 0 } sem ? sem : AppUpdateVersion.ReleaseTagVersion(s.TargetQuad);

        void OpenNotes()
        {
            go?.Invoke("whatsnew", notesVersion.Length == 0 ? null : notesVersion);
            close?.Invoke();
        }

        var actions = new List<Element>(3);
        switch (s.State)
        {
            case AppUpdateState.Available:
            case AppUpdateState.Snoozed:
                actions.Add(PillButton(Loc.Get(Strings.Update.Action.UpdateNow),
                    () => { if (up is not null) _ = up.ApplyAsync(CancellationToken.None); }, accent: true));
                actions.Add(PillButton(Loc.Get(Strings.Update.Action.WhatsNew), OpenNotes, accent: false));
                if (s.State == AppUpdateState.Available)
                    actions.Add(PillButton(Loc.Get(Strings.Update.Action.Later), () => up?.Snooze(), accent: false));
                break;
            case AppUpdateState.Completed:
                actions.Add(PillButton(Loc.Get(Strings.Update.Action.WhatsNew), () => { OpenNotes(); up?.Acknowledge(); }, accent: true));
                actions.Add(PillButton(Loc.Get(Strings.Update.Action.Dismiss), () => up?.Acknowledge(), accent: false));
                break;
            case AppUpdateState.Failed:
                actions.Add(PillButton(Loc.Get(Strings.Update.Action.Retry),
                    () => { if (up is not null) _ = up.ApplyAsync(CancellationToken.None); }, accent: true));
                actions.Add(PillButton(Loc.Get(Strings.Update.Action.Dismiss), () => up?.Acknowledge(), accent: false));
                break;
        }

        // While a download runs the row shows PROGRESS instead of buttons: there is exactly one thing happening and
        // nothing useful to press (cancelling mid-stage is the deployment API's business, not a notification's).
        Element trailing = s.State is AppUpdateState.Downloading or AppUpdateState.Installing
            ? new BoxEl
            {
                Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, 6f, 0f, 0f),
                Children =
                [
                    ProgressBar.Determinate(s.ProgressPercent / 100f, 200f),
                    new TextEl(s.ProgressPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%")
                        { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
                ],
            }
            : actions.Count > 0
                ? new BoxEl { Direction = 0, Gap = 6f, Wrap = true, Margin = new Edges4(0f, 4f, 0f, 0f), Children = actions.ToArray() }
                : new BoxEl();

        string body = AboutUpdatePanel.StateSentence(s);

        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Start,
            Children =
            [
                GlyphChip(glyph, tint),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(title) { Size = 13.5f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        body.Length > 0 ? new TextEl(body) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3 } : new BoxEl(),
                        trailing,
                    ],
                },
            ],
        };
    }

    // ── social ───────────────────────────────────────────────────────────────────────────────────────────────────────
    static Element SocialRow(SocialNotification s, long now, Action<string, string?>? go)
    {
        var textKids = new List<Element>(2)
        {
            new TextEl(s.Title) { Size = 13f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2 },
        };
        if (s.Timestamp > 0)
            textKids.Add(new TextEl(RelTime(now - s.Timestamp)) { Size = 11f, Weight = 600, Color = Tok.TextTertiary });

        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), ClipToBounds = true,
                    Children = [ Surfaces.Artwork(ImageOf(s.ImageUrl), (s.Id.GetHashCode() & 0x7fffffff), 40f, 40f, 20f) ],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f, Children = textKids.ToArray() },
            ],
        };
    }

    /// <summary>The Spotify-category row's navigation. INTERNAL because Home's timeline renders a subset of these same
    /// rows (concert announcements) and must click through to exactly the same place — a second copy of this decision
    /// is a second answer the moment either side learns a new uri form.</summary>
    internal static void ClickSocial(SocialNotification s, Action<string, string?>? go, Action? close)
    {
        if (s.ActionType == SocialActionType.Navigate && s.ActionUri is { } uri && RichText.RouteForUri(uri) is { } route)
        {
            go?.Invoke(route, null);
            close?.Invoke();
            return;
        }
        // NAVIGATE_WEBVIEW or an unroutable uri (user / concert) → open the web page.
        string? web = WebUrlFor(s.ActionUri);
        if (web is not null) { LoginView.OpenUrl(web); close?.Invoke(); }
    }

    // ── new release ──────────────────────────────────────────────────────────────────────────────────────────────────
    static Element NewReleaseRow(NewReleaseNotification r)
    {
        bool circular = false;
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(circular ? 22f : 5f), ClipToBounds = true,
                    Children = [ Surfaces.Artwork(ImageOf(r.ImageUrl), (r.Uri.GetHashCode() & 0x7fffffff), 44f, 44f, circular ? 22f : 5f) ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                    Children =
                    [
                        new TextEl(r.Name) { Size = 13.5f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(r.CreatorName) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                TypePill(ReleaseLabel(r)),
            ],
        };
    }

    static void ClickRelease(NewReleaseNotification r, Action<string, string?>? go, Action? close)
    {
        if (r.Kind == NewReleaseKind.Album && RichText.RouteForUri(r.Uri) is { } route) { go?.Invoke(route, r.Name); close?.Invoke(); return; }
        // Episodes have no dedicated detail route yet (open decision) → open the web player as a safe fallback.
        string? web = WebUrlFor(r.Uri);
        if (web is not null) { LoginView.OpenUrl(web); close?.Invoke(); }
    }

    static string ReleaseLabel(NewReleaseNotification r)
    {
        if (r.Kind == NewReleaseKind.Episode) return Loc.Get(Strings.Notifications.Release.Episode);
        return (r.AlbumType ?? "").ToUpperInvariant() switch
        {
            "SINGLE" => Loc.Get(Strings.Notifications.Release.Single),
            "EP" => Loc.Get(Strings.Notifications.Release.Ep),
            "COMPILATION" => Loc.Get(Strings.Notifications.Release.Compilation),
            _ => Loc.Get(Strings.Notifications.Release.Album),
        };
    }

    // ── activity ─────────────────────────────────────────────────────────────────────────────────────────────────────
    static Element ActivityCard(ActivityNotification a, long now, NotificationCenterBridge nc, Signal<long> expanded, long expandedId,
        Action<string, string?>? go, Action? close)
    {
        var e = a.Entry;
        bool isExpanded = expandedId == e.Id;
        bool undone = e.Status == ActivityStatus.Undone;

        var titleKids = new List<Element>(3)
        {
            new TextEl(ActivitySummary(e))
            {
                Size = 13f, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap,
                Color = undone ? Tok.TextTertiary : Tok.TextPrimary,
                Strikethrough = undone,
            },
        };
        if (e.Status == ActivityStatus.Failed) titleKids.Add(StatusChip(Loc.Get(Strings.Notifications.Status.Failed), Tok.SystemFillCritical));
        else if (undone) titleKids.Add(StatusChip(Loc.Get(Strings.Notifications.Status.Undone), Tok.TextTertiary));

        var rightKids = new List<Element>(2)
        {
            new TextEl(RelTime(now - e.TimestampMs)) { Size = 11f, Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
        };
        if (e.IsUndoable)
            rightKids.Add(PillButton(Loc.Get(Strings.Notifications.Undo), () => _ = nc.UndoAsync(e), accent: false));

        var main = new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Center,
            Children =
            [
                GlyphChip(ActivityGlyph(e.Kind), undone ? Tok.TextTertiary : Tok.TextSecondary),
                new BoxEl { Direction = 0, Grow = 1f, Basis = 0f, Gap = 6f, AlignItems = FlexAlign.Center, Children = titleKids.ToArray() },
                new BoxEl { Direction = 1, Shrink = 0f, AlignItems = FlexAlign.End, Gap = 4f, Children = rightKids.ToArray() },
            ],
        };

        var kids = new List<Element>(3) { main, UnreadDot(!e.Read) };
        var card = new BoxEl
        {
            Key = "ntf:act:" + e.Id,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, MinHeight = 56f,
            Padding = new Edges4(10f, 8f, 10f, 8f), Corners = CornerRadius4.All(8f),
            HoverFill = WaveeColors.RowHover, PressedFill = WaveeColors.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => expanded.Value = isExpanded ? -1 : e.Id,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children = kids.ToArray(),
        };

        if (!isExpanded) return card;
        return new BoxEl
        {
            Key = "ntf:actwrap:" + e.Id, Direction = 1, Gap = 2f,
            Children = [ card, ActivityDetail(e, go, close) ],
        };
    }

    static Element ActivityDetail(ActivityEntry e, Action<string, string?>? go, Action? close)
    {
        var kids = new List<Element>(5);

        // Target line — what the action applied to (full name, or the raw uri for pre-name entries), with an Open jump
        // when the uri routes to a page. Always present so expanding is never a visual no-op.
        string target = e.TargetName is { Length: > 0 } tn ? tn : e.TargetUri;
        string? route = RichText.RouteForUri(e.TargetUri);
        kids.Add(new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
            Children =
            [
                new TextEl(target) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap },
                route is not null && go is not null
                    ? PillButton(Loc.Get(Strings.Notifications.Activity.Detail.Open), () => { go(route, e.TargetName); close?.Invoke(); }, accent: false)
                    : new BoxEl(),
            ],
        });

        var p = e.Payload;
        if (p?.OldName is { } oldName && p.NewName is { } newName)
            kids.Add(new TextEl(Strings.Notifications.Activity.Detail.RenamedFrom(oldName, newName)) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        if (p?.Tracks is { Count: > 0 } tracks)
        {
            kids.Add(WaveeType.Eyebrow(Loc.Get(Strings.Notifications.Activity.Detail.Tracks)) with { Color = Tok.TextTertiary });
            int shown = 0;
            foreach (var t in tracks)
            {
                kids.Add(new TextEl("•  " + (t.Name is { Length: > 0 } nm ? nm : t.Uri)) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
                if (++shown >= 8) break;
            }
        }
        return new BoxEl
        {
            Direction = 1, Gap = 3f, Margin = new Edges4(46f, 0f, 12f, 6f),
            Children = kids.ToArray(),
        };
    }

    static string ActivitySummary(ActivityEntry e)
    {
        string playlist = e.TargetName is { Length: > 0 } n ? n : Loc.Get(Strings.Notifications.Activity.GenericTarget);
        string count = Strings.Detail.SongCount(e.Payload?.Tracks?.Count ?? 0);
        return e.Kind switch
        {
            ActivityKind.Save => SavedSummary(e, saved: true),
            ActivityKind.Unsave => SavedSummary(e, saved: false),
            ActivityKind.PlaylistAddTracks => Strings.Notifications.Activity.AddTracks(count, playlist),
            ActivityKind.PlaylistRemoveTracks => Strings.Notifications.Activity.RemoveTracks(count, playlist),
            ActivityKind.PlaylistMoveTracks => Strings.Notifications.Activity.MoveTracks(playlist),
            ActivityKind.PlaylistRename => Strings.Notifications.Activity.Rename(e.Payload?.NewName ?? playlist),
            ActivityKind.PlaylistVisibility => (e.Payload?.NewIsPublic ?? false)
                ? Strings.Notifications.Activity.MadePublic(playlist)
                : Strings.Notifications.Activity.MadePrivate(playlist),
            ActivityKind.PlaylistCreate => Strings.Notifications.Activity.Create(playlist),
            ActivityKind.PlaylistDelete => Strings.Notifications.Activity.Delete(playlist),
            ActivityKind.PlaylistCoverSet => Strings.Notifications.Activity.CoverSet(playlist),
            ActivityKind.PlaylistCoverClear => Strings.Notifications.Activity.CoverClear(playlist),
            ActivityKind.ContributorInvite => Strings.Notifications.Activity.Invite(playlist),
            _ => "",
        };
    }

    // Save/Unsave covers every library entity; the phrasing follows the uri kind (like a song / save an album / follow a
    // profile), naming the item when the call site captured it. Nameless (older) entries fall back to the generic line.
    static string SavedSummary(ActivityEntry e, bool saved)
    {
        if (e.TargetName is not { Length: > 0 } name)
            return Loc.Get(saved ? Strings.Notifications.Activity.Save : Strings.Notifications.Activity.Unsave);
        string uri = e.TargetUri;
        // Kind via the ONE parser (hydration-facade-design.md §1.1). `spotify:local:*` — Spotify's own local-file uri —
        // has no EntityKind (nothing hydrates it), so that one scheme stays an explicit test rather than a made-up kind.
        var kind = EntityUri.KindOf(uri);
        if (kind == EntityKind.Track || uri.StartsWith("spotify:local:", StringComparison.Ordinal))
            return saved ? Strings.Notifications.Activity.SaveTrack(name) : Strings.Notifications.Activity.UnsaveTrack(name);
        if (kind == EntityKind.Album)
            return saved ? Strings.Notifications.Activity.SaveAlbum(name) : Strings.Notifications.Activity.UnsaveAlbum(name);
        // artists, playlists, shows — the save verb is follow
        return saved ? Strings.Notifications.Activity.Follow(name) : Strings.Notifications.Activity.Unfollow(name);
    }

    static string ActivityGlyph(ActivityKind kind) => kind switch
    {
        ActivityKind.Save or ActivityKind.Unsave => Icons.Heart,
        ActivityKind.PlaylistAddTracks => Icons.Add,
        ActivityKind.PlaylistRemoveTracks => Icons.Remove,
        ActivityKind.PlaylistMoveTracks => Icons.Sort,
        ActivityKind.PlaylistRename => Icons.Edit,
        ActivityKind.PlaylistVisibility => Icons.Globe,
        ActivityKind.PlaylistCreate => Icons.Add,
        ActivityKind.PlaylistDelete => Icons.Remove,
        ActivityKind.PlaylistCoverSet or ActivityKind.PlaylistCoverClear => Icons.Picture,
        ActivityKind.ContributorInvite => Icons.Link,
        _ => Icons.StatusInfo,
    };

    // ── shared bits ──────────────────────────────────────────────────────────────────────────────────────────────────
    static Element GlyphChip(string glyph, ColorF tint) => new BoxEl
    {
        Width = 36f, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(18f), Fill = Tok.FillSubtleSecondary,
        Children = [ new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = tint } ],
    };

    static Element StatusChip(string label, ColorF tint) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(7f, 1f, 7f, 1f), Corners = CornerRadius4.All(8f), Fill = Tok.FillSubtleSecondary,
        Children = [ WaveeType.Eyebrow(label) with { Color = tint } ],
    };

    static Element TypePill(string type) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(9f, 2f, 9f, 2f), Corners = CornerRadius4.All(10f), Fill = Tok.FillSubtleSecondary,
        Children = [ WaveeType.Eyebrow(type) with { Color = Tok.TextTertiary } ],
    };

    static Element PillButton(string label, Action onClick, bool accent) => new BoxEl
    {
        Shrink = 0f, MinHeight = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(12f, 4f, 12f, 4f), Corners = CornerRadius4.All(14f),
        Fill = accent ? Tok.AccentDefault : Tok.FillControlDefault,
        HoverFill = accent ? Tok.AccentSecondary : Tok.FillControlSecondary,
        PressedFill = accent ? Tok.AccentTertiary : Tok.FillControlTertiary,
        BorderWidth = accent ? 0f : 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
        OnClick = onClick,
        Children = [ new TextEl(label) { Size = 12f, Weight = 600, Color = accent ? Tok.TextOnAccentPrimary : Tok.TextPrimary } ],
    };

    static Element LinkButton(string label, Action onClick) => new BoxEl
    {
        Shrink = 0f, MinHeight = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(8f, 3f, 8f, 3f), Corners = CornerRadius4.All(6f),
        HoverFill = WaveeColors.RowHover, PressedFill = WaveeColors.RowPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = onClick,
        Children = [ new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary } ],
    };

    static Element EmptyState(NotificationCategory? filter, NotificationFeedState social, NotificationFeedState whatsNew)
    {
        // A remote category with zero rows is only "empty" when the feed actually loaded — a failed or offline fetch
        // must say so instead of masquerading as "no notifications".
        static string? RemoteMsg(NotificationFeedState s) => s switch
        {
            NotificationFeedState.Idle or NotificationFeedState.Loading => Loc.Get(Strings.Notifications.Loading),
            NotificationFeedState.Error => Loc.Get(Strings.Notifications.Feed.Error),
            NotificationFeedState.Offline => Loc.Get(Strings.Notifications.Feed.Offline),
            _ => null,
        };
        string msg = filter switch
        {
            NotificationCategory.AppUpdate => Loc.Get(Strings.Notifications.Empty.Updates),
            NotificationCategory.Social => RemoteMsg(social) ?? Loc.Get(Strings.Notifications.Empty.Spotify),
            NotificationCategory.NewRelease => RemoteMsg(whatsNew) ?? Loc.Get(Strings.Notifications.Empty.New),
            NotificationCategory.Activity => Loc.Get(Strings.Notifications.Empty.Activity),
            _ => social is NotificationFeedState.Idle or NotificationFeedState.Loading || whatsNew is NotificationFeedState.Idle or NotificationFeedState.Loading
                ? Loc.Get(Strings.Notifications.Loading)
                : Loc.Get(Strings.Notifications.Empty.All),
        };
        return new BoxEl
        {
            Direction = 1, Width = Width, MinHeight = 120f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = Edges4.All(24f),
            Children = [ new TextEl(msg) { Size = 13f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 300f } ],
        };
    }

    static Image? ImageOf(string? url) => url is { Length: > 0 } u ? new Image(u) : null;

    // Already-web urls pass through; spotify uris resolve through the ONE consolidated converter (Actions/SpotifyLink.cs).
    static string? WebUrlFor(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return uri;
        return SpotifyLink.WebUrl(uri);
    }

    static string RelTime(long ageMs)
    {
        if (ageMs < 0) ageMs = 0;
        long min = ageMs / 60_000;
        if (min < 1) return Loc.Get(Strings.Friends.Now);
        if (min < 60) return Strings.Friends.MinAgo(min);
        long hr = min / 60;
        if (hr < 24) return Strings.Friends.HrAgo(hr);
        return Strings.Friends.DAgo(hr / 24);
    }
}
