using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The what's-new timeline ──────────────────────────────────────────────────────────────────────────────────────────
// New releases from followed artists AND the Spotify category's concert/show announcements, grouped by day. A FIXED row
// on Home for the same reason as the artist podium: the data is the notification feed, not the home feed.
//
// It costs no new request and no new subscription. NotificationCenterBridge is activated once at the app root
// (WaveeApp.cs) and already owns the what's-new fetch, the gander fetch, the unread diff against the persisted
// last-seen keys, and a signal that republishes on the UI thread — so reading Items here is a subscribe, not a second
// feed, and the concert rows arrive through the SAME one. Deliberately NOT calling EnsureFresh: go-live already primed
// it, and re-priming per Home mount would refetch on every navigation back.
//
// WHAT JOINS AND WHAT DOES NOT is HomeTimelineMerge's decision, gated on a concrete kind (SpotifyUpdates.IsConcert) and
// never on the center's display category — that category also holds followers and generic announcements, which are not
// timeline material. With no concert items in the feed the module renders exactly what it rendered before.
sealed class HomeTimeline : Component
{
    // `.tlday { grid-template-columns: 96px minmax(0,1fr) }` — the day column is fixed so every rule lines up down the
    // module, which is what makes the pips read as one chronology.
    const float DayColumn = 96f;

    public override Element Render()
    {
        var nc = UseContext(NotificationCenterBridge.Slot);
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        if (nc is null || svc is null) return new BoxEl();

        // ONE subscription: a landed feed (either feed) and a mark-read both republish Items, so the header's counter
        // now comes out of the merge instead of a second read of the bell's badge — which counted every unread
        // notification in the app, including app updates and follower rows this module never shows.
        var feed = HomeTimelineMerge.Build(nc.Items.Value);
        if (feed.IsEmpty) return new BoxEl();
        int unread = feed.Unread, total = feed.Total;

        var groups = new List<Element>(feed.Groups.Length);
        foreach (var group in feed.Groups)
        {
            var rows = new List<Element>(group.Rows.Length);
            foreach (var entry in group.Rows) rows.Add(RowFor(entry, nc, go));
            groups.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.M, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    // `.when` — right-aligned, with the relative form beneath it in a lighter tier.
                    new BoxEl
                    {
                        Width = DayColumn, Shrink = 0f, Direction = 1, Gap = 0f,
                        AlignItems = FlexAlign.End, Padding = new Edges4(0f, Spacing.M, 0f, 0f),
                        Children = [.. DayLabel(group.DayTicks)],
                    },
                    // `.tlrows` carries the rule the pips straddle.
                    new BoxEl
                    {
                        Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, MinWidth = 0f,
                                Children =
                                [
                                    new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                                    new BoxEl { Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [.. rows] },
                                ],
                            },
                        ],
                    },
                ],
            });
        }

        Element module = new BoxEl
        {
            Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
            Children =
            [
                Surfaces.SectionHeader(Loc.Get(Strings.Home.NewReleases),
                    unread > 0 ? Strings.Home.UnheardOf(unread, total) : null,
                    unread > 0 ? InfoBadge.Count(unread) : null),
                new BoxEl { Direction = 1, Gap = 0f, MinWidth = 0f, Children = [.. groups] },
            ],
        };
        return Responsive.Of(width => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, HomeModuleLayout.Gap(width)),
            Children = [module],
        }, fallback: HomeModuleLayout.FallbackWidth);
    }

    // ── the two row legs ─────────────────────────────────────────────────────────────────────────────────────────────
    // ONE anatomy (HomeCards.TimelineRow), two sources. Everything that differs — the badge word, the source line, the
    // art shape and the click — is resolved here and handed over as plain values.
    static Element RowFor(HomeTimelineRow entry, NotificationCenterBridge nc, Action<string, string?>? go)
    {
        Element row = entry.Kind == HomeTimelineKind.Concert && entry.Update is { } s
            ? HomeCards.TimelineRow(
                s.Id, s.ImageUrl, SpotifyUpdates.CleanTitle(s.Title),
                Loc.Get(Strings.Concerts.Detail.Concert),
                SpotifyUpdates.ActName(s) ?? Loc.Get(Strings.Concerts.LiveMusic),
                s.IsUnread,
                () =>
                {
                    // Read state is the notification center's, both ways: this WRITES through the one store (the panel
                    // and the bell badge see it on the next rebuild), and the row's own unread pip READ it from there.
                    nc.MarkRead(s.Id);
                    // ...and the click itself is the center's own decision verbatim, not a second copy of it.
                    NotificationPanel.ClickSocial(s, go, close: null);
                },
                artRadius: WaveeSize.Thumb40 / 2f)   // an act is a person: the center draws these round too
            : entry.Release is { } r
                ? HomeCards.TimelineRow(r.Id, r.ImageUrl, r.Name, KindLabel(r), MetaLine(r), r.IsUnread, () => Navigate(r, go))
                : new BoxEl();
        return row is BoxEl b ? b with { Key = "home-timeline:" + entry.Id } : row;
    }

    // "Release" / "Episode" — the server's coarse kind, not a guess from the title.
    static string KindLabel(NewReleaseNotification n)
        => Loc.Get(n.Kind == NewReleaseKind.Episode ? Strings.Podcast.Show : Strings.Detail.FactReleases);

    // The creator, plus a duration when the feed carried one (episodes do; albums do not).
    static string MetaLine(NewReleaseNotification n) => n.CreatorName;

    static void Navigate(NewReleaseNotification r, Action<string, string?>? go)
    {
        if (go is null) return;
        // Albums route to the album page, through the same uri→route resolver the rest of the app uses.
        if (r.Kind != NewReleaseKind.Episode)
        {
            if (RichText.RouteForUri(r.Uri) is { } route) go(route, r.Name);
            return;
        }
        // An EPISODE has no route of its own, and the what's-new feed carries only the episode's own
        // `spotify:episode:` uri — NewReleaseNotification has no parent-show field and the payload
        // SpotifyWhatsNewService.ParseEpisode reads takes only the show's NAME out of `podcastV2`. So "show:" + that uri
        // was a route the store cannot satisfy (it fetches `spotify:show:` only) and the destination rendered
        // DetailModel.Empty — a click that opened a blank page. Until an episode route exists, fall back to the web
        // player, which is exactly what NotificationPanel.ClickRelease does for this very notification.
        if (SpotifyLink.WebUrl(r.Uri) is { } web) LoginView.OpenUrl(web);
    }

    // Today / Yesterday get the word alone; anything older gets the weekday+date with the relative form under it, which
    // is what lets the reader scan the column without doing arithmetic.
    static IEnumerable<Element> DayLabel(long dayTicks)
    {
        var day = new DateTime(dayTicks, DateTimeKind.Local);
        var today = DateTime.Now.Date;
        int delta = (int)(today - day).TotalDays;
        var c = System.Globalization.CultureInfo.CurrentCulture;

        string head = delta <= 0 ? Loc.Get(Strings.Detail.Today)
                    : delta == 1 ? Loc.Get(Strings.Detail.Yesterday)
                    : day.ToString("ddd d MMM", c);
        yield return Caption(head) with
        {
            Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        if (delta > 1)
            yield return Caption(Strings.Detail.DaysAgo(delta)) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
    }
}
