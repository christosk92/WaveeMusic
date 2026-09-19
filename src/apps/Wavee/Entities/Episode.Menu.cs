// ── Entities/Episode.Menu.cs ───────────────────────────────────────────────────────────────────────────────────────
// the episode context menu (a reader row's right-click and ⋯) and the three EPISODE verbs it registers into AppActions:
// MarkPlayed · MarkUnplayed · GoToShow — plus the Share ▸ submenu a show or an episode shares
//
// Role: UI
// Owner: E
// Wave: P3 (the podcast rework)
// Budget: 200 lines
// Spec: podcast-show-rework-implementation.md §4 (the menu: play next · add to queue · mark played/unplayed · go to show
//       · share; "save to Your Episodes" is wave P8's), §5.7, §5.11 (ActionId.MarkPlayed/MarkUnplayed/GoToShow and
//       ActionTarget.ForEpisode, wave P2), §12 D-5 · as-built-20260919.md P2-S ("NO AppActions.Register yet")
//
// ── THE GRAMMAR ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   header      38 art · title · the show's name
//   transport   Play next · Play after            (the queue owner's seam, a playable goes in at once)
//   state       Mark as played | Mark as unplayed (an ABSOLUTE-STATE pair: the row that applies, never a toggle)
//   navigation  Go to show                        (absent on the show's own page, and without a show ref)
//   Share ▸     Copy link · Copy Spotify URI · Open in Spotify Web
//
// Built at OPEN time (inside a `ContextMenu.Attach` factory), so it allocates freely and reads every seam once. A verb
// whose seam is absent renders DISABLED — never a live row that does nothing.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

public readonly partial struct Episode
{
    /// <summary>What a surface varies about the episode menu. <see cref="ShowGoToShow"/> is false on the show page
    /// itself, where "Go to show" would navigate to the page the row is on.
    /// <para><b>Construct with a named argument</b> — <c>default</c> reads <see cref="ShowGoToShow"/> false.</para></summary>
    public readonly record struct MenuOptions(bool ShowGoToShow = true);

    /// <summary>The episode menu, or null when there is nothing to act on.</summary>
    public static ContextMenuModel? Menu(Episode e, in MenuOptions o)
    {
        EnsureActions();
        if (!e.IsValid || !e.Uri.IsValid) return null;
        string title = TitleOf(e);
        var show = e.Show;
        bool showKnown = show.IsValid && show.Uri.IsValid;
        var ctx = new ActionContext(ActionTarget.ForEpisode(e.Uri, title, showKnown ? show.Uri : default), Actions.Services);

        var rows = new List<MenuFlyoutItem>(8) { TransportRow(e, next: true), TransportRow(e, next: false) };

        bool saved = Spotify.Podcasts.IsSaved(e);
        rows.Add(new MenuFlyoutItem(Loc.Get(saved ? Strings.Podcast.Reader.RemoveSaved : Strings.Podcast.Reader.Save),
            Icons.Heart, !Spotify.Podcasts.SavedBusy, () => Spotify.Podcasts.ToggleSaved(e)));

        // D-5: the row that applies is decided by THE completion rule, the same pct the reader row paints.
        bool played = Rules.Played(ReaderPctOf(e));
        if (Actions.Menu.Row(played ? ActionId.MarkUnplayed : ActionId.MarkPlayed, in ctx) is { } mark)
        {
            Actions.Menu.OpenGroup(rows);
            // The action table has no check key yet (Actions.UI.cs's ActionIcons); the row carries its glyph itself.
            rows.Add(mark with { Icon = played ? Icons.Undo : Icons.Check });
        }

        if (o.ShowGoToShow && showKnown && Actions.Menu.Row(ActionId.GoToShow, in ctx) is { } go)
        {
            Actions.Menu.OpenGroup(rows);
            rows.Add(go);
        }

        if (ShareMenu(e.Uri, in ctx) is { } share)
        {
            Actions.Menu.OpenGroup(rows);
            rows.Add(share);
        }

        string? subtitle = show.IsValid && show.Knows(ShowFields.Title) ? show.Title : null;
        return new ContextMenuModel(rows, Actions.Menu.Header(ArtOf(e), title, subtitle));
    }

    /// <summary>Play next / Play after for ONE episode through the queue owner's seam (<see cref="Enqueue"/>). The
    /// registered PlayNext/AddToQueue verbs read a TRACK set and the container verbs a membership — an episode is
    /// neither, so these two rows are this menu's own.</summary>
    static MenuFlyoutItem TransportRow(Episode e, bool next)
    {
        var s = Actions.Services;
        bool enabled = (next ? s.PlayNext : s.AddToQueue) is not null;
        return new MenuFlyoutItem(Loc.Get(next ? Strings.Detail.PlayNext : Strings.Detail.PlayAfter),
            ActionIcons.Resolve(next ? ActionIcons.PlayNext : ActionIcons.Queue), enabled, () => Enqueue(e, next));
    }

    /// <summary>The Share ▸ submenu for ONE shareable uri (an episode here, the show's ⋯ on the show page): Copy link —
    /// this file's own row, since the registered CopyLink reads a track set — then the registered Copy Spotify URI and
    /// Open in Spotify Web, which read the target's own uri. Null for a uri that is not Spotify's.</summary>
    internal static MenuFlyoutItem? ShareMenu(EntityUri uri, in ActionContext ctx)
    {
        if (!Actions.MenuRules.IsShareable(uri)) return null;
        Track.EnsureActions();
        string link = Actions.WebLinkOf(uri);
        var items = new List<MenuFlyoutItem>(3)
        {
            new(Loc.Get(Strings.Menu.CopyLink), ActionIcons.Resolve(ActionIcons.Link),
                Actions.Services.Clipboard is not null && link.Length > 0, () => CopyLink(link)),
        };
        if (Actions.Menu.Row(ActionId.CopySpotifyUri, in ctx) is { } copyUri) items.Add(copyUri);
        if (Actions.Menu.Row(ActionId.OpenInSpotifyWeb, in ctx) is { } web) items.Add(web);
        return Actions.Menu.SubMenu(Loc.Get(Strings.Menu.Share), items, ActionIcons.Share);
    }

    /// <summary>Put a web link on the clipboard and say so; a clipboard failure toasts rather than doing nothing.</summary>
    internal static void CopyLink(string link)
    {
        if (Actions.Services.Clipboard is not { } clip || link.Length == 0) return;
        try { clip(link); }
        catch
        {
            Notify.Say(Loc.Get(Strings.Common.ErrorTitle), InfoBarSeverity.Error);
            return;
        }
        Notify.Say(Loc.Get(Strings.Menu.LinkCopied), InfoBarSeverity.Success);
    }

    // ══ THE THREE EPISODE VERBS ══════════════════════════════════════════════════════════════════════════════════════

    static bool s_actionsInstalled;

    /// <summary>Make sure the episode verbs are registered before a menu reads them.</summary>
    public static void EnsureActions()
    {
        if (!s_actionsInstalled) InstallActions();
    }

    /// <summary>Register MarkPlayed · MarkUnplayed · GoToShow into <see cref="AppActions"/> (first-wins per id, so this
    /// is idempotent). A mark runs exactly <see cref="Entities.MarkEpisode"/> — the local stage at Local plus the
    /// herodotus revision (wave P2) — and is disabled for a target that does not resolve to a resident episode;
    /// GoToShow navigates <see cref="ActionTarget.ShowUri"/> through <see cref="ActionRules.ShowRouteFor"/> and is
    /// disabled without a show ref or a navigator.</summary>
    public static void InstallActions()
    {
        if (s_actionsInstalled) return;
        s_actionsInstalled = true;

        AppActions.Register(new AppAction
        {
            Id = ActionId.MarkPlayed, IconKey = ActionIcons.Save,
            Label = static _ => Loc.Get(Strings.Podcast.Menu.MarkPlayed),
            IsEnabled = static c => EpisodeOf(c.Target).IsValid,
            Execute = static c => Mark(c.Target, played: true),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.MarkUnplayed, IconKey = ActionIcons.Remove,
            Label = static _ => Loc.Get(Strings.Podcast.Menu.MarkUnplayed),
            IsEnabled = static c => EpisodeOf(c.Target).IsValid,
            Execute = static c => Mark(c.Target, played: false),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.GoToShow, IconKey = ActionIcons.Radio,
            Label = static _ => Loc.Get(Strings.Podcast.Menu.GoToShow),
            IsEnabled = static c => c.S.Go is not null && ActionRules.ShowRouteFor(c.Target).Kind != Shell.RouteKind.NotFound,
            Execute = static c =>
            {
                var route = ActionRules.ShowRouteFor(c.Target);
                if (route.Kind != Shell.RouteKind.NotFound) c.S.Go?.Invoke(route);
            },
        });
    }

    /// <summary>The resident episode an EPISODE target names, or default.</summary>
    static Episode EpisodeOf(ActionTarget target)
    {
        if (target.Kind != TargetKind.Episode || !target.Uri.IsValid || Entities.Current is not { } scope) return default;
        return scope.Episodes.TryGetSlot(target.Uri.Id, out int slot) ? new Episode(slot) : default;
    }

    static void Mark(ActionTarget target, bool played)
    {
        var e = EpisodeOf(target);
        if (e.IsValid) Entities.MarkEpisode(e, played);
    }
}
