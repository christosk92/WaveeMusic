using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

// Per-entity menu composition — plain code over the AppAction singletons, run lazily inside the ContextMenu.Attach
// open-thunk (allocation at human rate, zero per-frame). Non-empty Primary → the Win11 Explorer command-bar body;
// rows-only → a plain vertical menu. ONE builder per entity; selection semantics live in TrackContextMenu.
//
// ── THE MENU GRAMMAR (D48) ──────────────────────────────────────────────────────────────────────────────────────────
// A right-click on the same KIND of thing offers the same verbs on every surface. Two menu builders for one kind that
// each grew their own subset is the defect this grammar exists to prevent (a search song offered no Go-to-album; a home
// mix card offered no Play-next/Add-to-playlist). The CORE set per kind is:
//
//   TRACK      Play · Play next · Play after · Save · Add to playlist ▸ · Go to album · Go to artist(s) · Share ▸
//   CONTAINER  Play · Play next · Play after · Save · Add to playlist ▸ · Pin · Open · Go to artist · Share ▸
//              (playlist / album / mix / generated; "Add to playlist" deposits the container's TRACKS)
//   SHOW       Play · Open · Pin · Share ▸       (a show has no Track set — see ContainerTracks)
//
// and the ORDER is fixed, whether the verbs render as a command strip or as plain rows:
//
//   transport (Play · Play next · Play after) → state (Save) → collection (Add to playlist · Move · Pin)
//   → navigation (Open · Go to album · Go to artist) → Share → surface extras → destructive LAST (behind a separator).
//
// Surface EXTRAS are additive and documented per builder (queue rows keep Move up/down + Remove from queue, sidebar
// rows keep Move up/down + Remove for navbar customization, an editable playlist keeps its owner block, a track keeps
// credits/song-radio/Video ▸). A core verb may be OMITTED only where the seam genuinely does not exist for that kind —
// and then with a comment naming the reason, never silently.
//
// ── GROUPING (the second axis) ───────────────────────────────────────────────────────────────────────────────────────
// Order alone stops being readable past about ten rows, and the sidebar PLAYLIST menu had grown to fourteen-plus. So a
// long menu also GROUPS, in the ONE shape this app uses (Wavee's own track menu, and Win11 Explorer's):
//
//   header tile → an ICON/COMMAND STRIP of the transport verbs → short separator-delimited groups of rows, where a
//   group that is itself a list of related low-frequency verbs collapses into ONE named submenu → destructive last.
//
// The submenu names are verbs-about-the-thing, not categories: `Organize ▸` (where does this row live: the moves, the
// lift out of a folder, the pin) and `Access ▸` (who may see and who may edit: visibility, collaborative, invite).
// Grouping NEVER removes a verb or moves one between builders — the sidebar playlist/folder menus and the detail page's
// owner menu keep every command they had, and Rename stays a top-level row on all three so the three read alike.
public static class Menus
{
    const int MaxInlinePlaylists = 10;   // Add-to-playlist submenu cap; the rest via "More playlists…" → the picker

    // ── Track set (detail rows, batch bar, eager lists, queue, now-playing) ─────────────────────────────────────────
    /// <summary>The track(s) menu. Primary strip [Play · Play next · Play after · Save]; rows per the grammar above.
    /// <paramref name="showGoToAlbum"/> is false on album detail pages (you are already there); <paramref name="extras"/>
    /// are the calling surface's own additions (the queue panel's Move up/down), inserted after Share and before the
    /// destructive block.</summary>
    public static ContextMenuModel Tracks(in ActionContext ctx, bool showGoToAlbum = true,
                                          IReadOnlyList<MenuFlyoutItem>? extras = null)
        => new(TrackTransportStrip(in ctx), TrackRows(in ctx, showGoToAlbum, extras), TrackHeader(ctx.Target.Tracks));

    /// <summary>The four transport/state verbs as a labeled command strip (the Explorer command-bar body).</summary>
    static AppBarCommand[] TrackTransportStrip(in ActionContext ctx) =>
    [
        TrackActions.Play.ToBarCommand(ctx),
        TrackActions.PlayNext.ToBarCommand(ctx),
        TrackActions.AddToQueue.ToBarCommand(ctx),
        TrackActions.ToggleLike.ToBarCommand(ctx),
    ];

    /// <summary>The same four verbs as plain ROWS — for the rows-only track menus (a 240-DIP sidebar pane is not where
    /// an Explorer-style labeled strip belongs, but the core verbs must still be there).</summary>
    static void AddTrackTransportRows(List<MenuFlyoutItem> rows, in ActionContext ctx)
    {
        rows.Add(TrackActions.Play.ToMenuItem(ctx));
        rows.Add(TrackActions.PlayNext.ToMenuItem(ctx));
        rows.Add(TrackActions.AddToQueue.ToMenuItem(ctx));
        rows.Add(TrackActions.ToggleLike.ToMenuItem(ctx));
    }

    /// <summary>The track menu's vertical rows only (also the batch bar's overflow source). Grammar order: collection →
    /// navigation → Share → extras → destructive.</summary>
    public static IReadOnlyList<MenuFlyoutItem> TrackRows(in ActionContext ctx, bool showGoToAlbum = true,
                                                         IReadOnlyList<MenuFlyoutItem>? extras = null)
    {
        var rows = new List<MenuFlyoutItem>(12) { AddToPlaylistItem(in ctx) };
        // MOVE is offered only where a source to move OUT of exists (an editable playlist context) — everywhere else
        // "move" and "add" would be the same verb twice.
        if (MoveToPlaylistItem(in ctx) is { } move) rows.Add(move);

        // Navigation. A multi-selection has no single album/artist to go to, so these are single-target rows.
        if (ctx.Target.Single is { } single)
        {
            // ONE navigation row for the container this playable belongs to, routed by what that container IS: a song
            // goes to its album, an EPISODE goes to its podcast (an episode carries its SHOW in the album slot —
            // EpisodeAsTrack, design §1.5 — so "Go to album" would have opened the album page of a show).
            if (showGoToAlbum && ActionRules.CanGoToAlbum(in ctx.Target))
                rows.Add(TrackActions.GoToAlbum.ToMenuItem(ctx));
            else if (showGoToAlbum && GoToPodcastItem(ctx.S, in ctx.Target) is { } podcast)
                rows.Add(podcast);
            var nav = NavigableArtists(single.Artists);
            if (nav is { Count: 1 } one)
                // The action navigates to the PRIMARY artist; when the only navigable one is a secondary (the primary
                // came through name-only), the row has to carry that artist itself.
                rows.Add(ActionRules.CanGoToArtist(in ctx.Target)
                    ? TrackActions.GoToArtist.ToMenuItem(ctx)
                    : GoToArtistItem(ctx.S, one[0]));
            else if (nav is { Count: > 1 })
                rows.Add(GoToArtistsItem(ctx.S, nav));
        }

        rows.Add(ShareItem(in ctx));

        // Surface extras: the track kind's own (credits · song radio · Video ▸) then the caller's.
        if (ctx.Target.Single is not null)
        {
            if (ActionRules.CanViewCredits(in ctx.Target))
                rows.Add(TrackActions.ViewCredits.ToMenuItem(ctx));
            if (ActionRules.CanStartTrackRadio(in ctx.Target))
                rows.Add(TrackActions.GoToSongRadio.ToMenuItem(ctx));
            if (VideoItem(in ctx) is { } video)
                rows.Add(video);
        }
        if (extras is { Count: > 0 })
        {
            rows.Add(MenuFlyoutItem.Separator);
            for (int i = 0; i < extras.Count; i++) rows.Add(extras[i]);
        }

        bool removeRow = TrackActions.RemoveFromThisPlaylist.EnabledFor(ctx);
        bool removeQueue = ctx.Target.Kind == TargetKind.QueueEntry && ctx.Target.RemoveFromDisplay is not null;
        if (removeRow || removeQueue) rows.Add(MenuFlyoutItem.Separator);
        if (removeRow) rows.Add(TrackActions.RemoveFromThisPlaylist.ToMenuItem(ctx));
        if (removeQueue) rows.Add(TrackActions.RemoveFromQueue.ToMenuItem(ctx));
        return rows;
    }

    // ── A MODULE playable (a YouTube video, a Twitch channel, a radio station) ──────────────────────────────────────
    /// <summary>The track menu for a playable a MODULE owns. Same grammar as <see cref="Tracks"/> — a module playable
    /// IS a track, and giving it a parallel menu is exactly the "same kind, two subsets" defect D48 exists to stop —
    /// with two deliberate differences:
    ///
    /// <list type="bullet">
    ///   <item><b>No "Go to album".</b> A module playable has no album (<c>LocalPlayables.ForModule</c> builds an empty
    ///   <c>AlbumRef</c>), so the row would open the route <c>album:</c> — a dead page. Passed as
    ///   <c>showGoToAlbum: false</c> rather than left to fall through, so the reason is stated where a reader looks.</item>
    ///   <item><b>An "Open on &lt;Module&gt;" extra</b>, when the module's page document for this playable carries an
    ///   <c>openUrl</c> action whose url survives the <c>ShellOpen</c> http(s) guard. Absent otherwise — never a row
    ///   that opens nothing.</item>
    /// </list></summary>
    /// <param name="ctx">The action context (target + services).</param>
    public static ContextMenuModel ModuleTrack(in ActionContext ctx)
        => Tracks(in ctx, showGoToAlbum: false, OpenOnModuleRows(in ctx));

    static IReadOnlyList<MenuFlyoutItem>? OpenOnModuleRows(in ActionContext ctx)
    {
        if (ctx.Target.Single is not { } track) return null;
        if (!Wavee.Sdk.ModuleUri.TryDecode(track.Uri, out string moduleId, out _)) return null;
        if (ModulePage.OpenUrlOf(ModuleOpenPage(track.Uri)) is not { Length: > 0 } url) return null;

        string name = ModuleDisplayName(moduleId);
        return [new MenuFlyoutItem(Strings.ModulePage.OpenOn(name), Icons.OpenInNewWindow,
            Invoke: () => ShellOpen.OpenUrl(url))];
    }

    /// <summary>The page document this playable's own page would render, from the sync cache — never a fetch: a menu is
    /// built inside the open thunk, on the UI thread, and must not await a child process.</summary>
    static Wavee.Sdk.ModulePageDoc? ModuleOpenPage(string playableUri)
    {
        if (Wavee.Backend.Modules.ModulePages.RouteFor(playableUri, LinkSlot.Title) is not { } route) return null;
        return Wavee.Backend.Modules.ModulePages.Get(Wavee.Backend.Modules.ModulePages.UriOf(route));
    }

    static string ModuleDisplayName(string moduleId)
    {
        var installed = Wavee.Backend.Modules.ModuleHost.Current?.Installed;
        if (installed is null) return moduleId;
        for (int i = 0; i < installed.Count; i++)
            if (string.Equals(installed[i].Id, moduleId, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(installed[i].Manifest.DisplayName)
                    ? moduleId : installed[i].Manifest.DisplayName;
        return moduleId;
    }

    /// <summary>The artists a menu can actually navigate to: those carrying a uri. Several producers hand back a display
    /// NAME with no uri (a projected sidebar row, a search row without an artist link) — navigating those would land on
    /// an empty <c>artist:</c> route. Returns null when none qualify, so the row is absent rather than dead.</summary>
    static IReadOnlyList<ArtistRef>? NavigableArtists(IReadOnlyList<ArtistRef> artists)
    {
        int n = 0;
        for (int i = 0; i < artists.Count; i++) if (artists[i].Uri.Length > 0) n++;
        if (n == 0) return null;
        if (n == artists.Count) return artists;
        var kept = new List<ArtistRef>(n);
        for (int i = 0; i < artists.Count; i++) if (artists[i].Uri.Length > 0) kept.Add(artists[i]);
        return kept;
    }

    // ── Share ▸ (Copy link(s) + single-target Copy Spotify URI / Open in Spotify Web) ────────────────────────────────
    /// <summary>The Share submenu that replaces the bare Copy-link row app-wide (tracks, cards, sidebar playlist):
    /// Copy link(s) always; the raw-URI and web-player variants only when the target is a single shareable spotify
    /// entity (multi-select collapses to just "Copy links (N)" — the URI/web variants are single-target).</summary>
    static MenuFlyoutItem ShareItem(in ActionContext ctx)
    {
        var items = new List<MenuFlyoutItem>(3) { TrackActions.CopyLink.ToMenuItem(ctx) };
        if (SpotifyLink.SingleUri(in ctx.Target) is not null)
        {
            items.Add(TrackActions.CopySpotifyUri.ToMenuItem(ctx));
            items.Add(TrackActions.OpenInSpotifyWeb.ToMenuItem(ctx));
        }
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.Share), items, ActionIcons.Resolve(ActionIcons.Share));
    }

    // ── Video ▸ (the user's local video-override curation for ONE playable) ─────────────────────────────────────────
    /// <summary>The Video submenu: "Attach video file…" when nothing is attached, else "Replace video file…" +
    /// "Locate video file…" (broken link only) + "Show in Explorer" (file present) + a separated "Remove video".
    /// Null — the row is absent entirely — for a multi-selection (one file, one playable) or on a backend with no
    /// curation service. Which rows exist is decided by <see cref="VideoOverrideUx.MenuFor"/>, which walks the SAME
    /// tier decision playback takes, so the menu can never disagree with what will actually play.</summary>
    static MenuFlyoutItem? VideoItem(in ActionContext ctx)
    {
        string? uri = ctx.Target.Single is { Uri.Length: > 0 } t ? t.Uri : null;
        var which = VideoOverrideUx.MenuFor(ctx.Target.Single is not null, uri, ctx.S.VideoOverrides);
        if (which == VideoMenuItems.None) return null;

        var items = new List<MenuFlyoutItem>(5);
        if ((which & VideoMenuItems.Attach) != 0) items.Add(VideoActions.AttachVideo.ToMenuItem(ctx));
        if ((which & VideoMenuItems.Replace) != 0) items.Add(VideoActions.ReplaceVideo.ToMenuItem(ctx));
        if ((which & VideoMenuItems.Locate) != 0) items.Add(VideoActions.LocateVideo.ToMenuItem(ctx));
        if ((which & VideoMenuItems.ShowInExplorer) != 0) items.Add(VideoActions.ShowVideoInExplorer.ToMenuItem(ctx));
        // Destructive last, behind a separator (the sidebar playlist Delete convention). No confirm dialog — detaching is
        // metadata-only, never touches the file, and the toast carries Undo.
        if ((which & VideoMenuItems.Remove) != 0)
        {
            items.Add(MenuFlyoutItem.Separator);
            items.Add(VideoActions.RemoveVideo.ToMenuItem(ctx));
        }
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.VideoOverride.MenuTitle), items,
            ActionIcons.Resolve(ActionIcons.Video));
    }

    /// <summary>Multi-artist track → a "Go to artists" cascade, one row per artist. Callers pass an already-filtered
    /// list (<see cref="NavigableArtists"/>): a uri-less artist has nowhere to go and must not get a row.</summary>
    static MenuFlyoutItem GoToArtistsItem(ActionServices s, IReadOnlyList<ArtistRef> artists)
    {
        var items = new MenuFlyoutItem[artists.Count];
        for (int i = 0; i < artists.Count; i++)
        {
            var a = artists[i];   // fresh capture per row — each navigates to its OWN artist
            items[i] = new MenuFlyoutItem(a.Name, null, s.Go is not null, () => s.Go?.Invoke("artist:" + a.Uri, a.Name));
        }
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.GoToArtists), items, ActionIcons.Resolve(ActionIcons.Artist));
    }

    /// <summary>"Go to podcast" — the navigation row an EPISODE row gets where a song row gets "Go to album". It opens
    /// the SHOW page ("show:" — the shared detail surface rendering Episodes), reading the show ref an episode carries
    /// in its album slot. Null when the target is not one of those rows, so the verb never appears on a song.</summary>
    static MenuFlyoutItem? GoToPodcastItem(ActionServices s, in ActionTarget target)
    {
        if (!ActionRules.CanGoToPodcast(in target) || target.Single is not { } t) return null;
        string uri = t.Album.Uri, name = t.Album.Name;   // captured by value — no Track held by the closure
        return new MenuFlyoutItem(Loc.Get(Strings.Menu.GoToPodcast), Icons.RadioTower,
                                  s.Go is not null, () => s.Go?.Invoke("show:" + uri, name));
    }

    /// <summary>"Go to artist" for ONE named artist that is not the target's primary (so the action singleton, which
    /// always navigates to <c>Artists[0]</c>, cannot express it).</summary>
    static MenuFlyoutItem GoToArtistItem(ActionServices s, ArtistRef a)
        => new(Loc.Get(Strings.Detail.GoToArtist), ActionIcons.Resolve(ActionIcons.Artist),
               s.Go is not null, () => s.Go?.Invoke("artist:" + a.Uri, a.Name));

    // ── Add to playlist ▸ (New playlist + up to 10 editable playlists + "More playlists…" → the picker) ─────────────
    static MenuFlyoutItem AddToPlaylistItem(in ActionContext ctx)
    {
        var s = ctx.S;
        var tracks = ctx.Target.Tracks;
        bool canAdd = s.Library is not null && tracks.Count > 0;
        return PlaylistDepositItem(s, Loc.Get(Strings.Detail.AddToPlaylist), Icons.Add, canAdd,
            deposit: (uri, name) => AddTo(s, uri, name, tracks),
            createAndDeposit: () => CreateAndAdd(s, tracks),
            excludeUri: null, pickerTracks: tracks);
    }

    /// <summary>Add to playlist ▸ for a CONTAINER (album / playlist / mix card): the same submenu, depositing the
    /// container's TRACKS — resolved through the shared <see cref="ContainerTracks"/> seam, i.e. exactly what dropping
    /// this card onto that playlist deposits. Null for a kind with no track set (an artist, a show — see
    /// <see cref="ContainerTracks"/>), so the row is ABSENT rather than a disabled promise.</summary>
    static MenuFlyoutItem? ContainerAddToPlaylistItem(in ActionContext ctx)
    {
        var s = ctx.S;
        if (ContainerTracks.ResolverFor(in ctx) is not { } resolve) return null;
        bool canAdd = s.Library is not null;
        // Adding a playlist to ITSELF is the one deposit that is always a no-op duplicate — the same refusal
        // WaveeResourceDrop.DepositTracksAsync makes for the container-on-itself drop.
        string? exclude = ctx.Target.Kind == TargetKind.Playlist ? ctx.Target.Uri : null;
        return PlaylistDepositItem(s, Loc.Get(Strings.Detail.AddToPlaylist), Icons.Add, canAdd,
            deposit: (uri, name) => ContainerTracks.AddTo(s, uri, name, resolve),
            createAndDeposit: () => CreateAndDeposit(s, (uri, name) => ContainerTracks.AddTo(s, uri, name, resolve)),
            excludeUri: exclude, pickerTracks: Array.Empty<Track>());
    }

    /// <summary>The ONE playlist-deposit submenu shape behind Add-to-playlist (tracks), Add-to-playlist (container) and
    /// Move-to-playlist: New playlist + up to <see cref="MaxInlinePlaylists"/> editable playlists + "More playlists…" →
    /// the full picker. Only what a pick DOES differs, so only that is a parameter — the row filter (editable, real
    /// <c>spotify:playlist:*</c>, the same one <c>PlaylistPickerPanel</c> applies) lives here once.</summary>
    static MenuFlyoutItem PlaylistDepositItem(ActionServices s, string title, IconRef icon, bool canAdd,
                                              Action<string, string> deposit, Action createAndDeposit,
                                              string? excludeUri, IReadOnlyList<Track> pickerTracks)
    {
        var items = new List<MenuFlyoutItem>(MaxInlinePlaylists + 3)
        {
            new(Loc.Get(Strings.Detail.NewPlaylist), Icons.Add, canAdd, createAndDeposit),
        };

        s.Store?.EnsurePlaylists();
        // MOST-RECENTLY-FILED FIRST. The inline rows used to be rootlist order truncated to ten, which for anyone with
        // more than ten playlists is the same ten forever — very often not the one they are reaching for, so the common
        // case degraded into "More playlists… → type the name". PlaylistDepositTargets owns the order AND the eligibility
        // filter, shared with the picker and the tab drop rules (they used to be three separate copies).
        var ordered = PlaylistDepositTargets.Order(s.Store?.Playlists.Value.Peek(), RecentDeposits(s), excludeUri);
        int inline = Math.Min(ordered.Count, MaxInlinePlaylists);
        for (int i = 0; i < inline; i++)
        {
            var uri = ordered[i].Uri;
            var name = ordered[i].Name;
            items.Add(new MenuFlyoutItem(name, null, canAdd, () => deposit(uri, name)));
        }

        items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MorePlaylists), null,
            canAdd && s.Overlay is not null, () => OpenPicker(s, pickerTracks, title, deposit, excludeUri)));

        return MenuFlyoutItem.SubMenu(title, items, icon, enabled: canAdd);
    }

    /// <summary>The persisted most-recently-filed-into playlist uris (newest first), or empty when there is no settings
    /// seam — the ordering then falls back to plain rootlist order, which is exactly the previous behaviour.</summary>
    internal static IReadOnlyList<string> RecentDeposits(ActionServices s)
        => s.Svc is { } svc ? PlaylistDepositTargets.Parse(svc.Settings.Get(WaveeSettings.PlaylistDepositRecents))
                            : Array.Empty<string>();

    /// <summary>Record a successful deposit so it leads the list next time. Idempotent-ish: an unchanged MRU is not
    /// written, so re-filing into the playlist already at the front costs no storage write.</summary>
    internal static void RememberDeposit(ActionServices s, string uri)
    {
        if (s.Svc is not { } svc) return;
        string current = svc.Settings.Get(WaveeSettings.PlaylistDepositRecents);
        string next = PlaylistDepositTargets.Serialize(PlaylistDepositTargets.Remember(PlaylistDepositTargets.Parse(current), uri));
        if (!string.Equals(next, current, StringComparison.Ordinal))
            svc.Settings.Set(WaveeSettings.PlaylistDepositRecents, next);
    }

    /// <summary>The confirmation toast for a completed deposit. Its ONE action slot spends itself on <b>Undo</b>, not
    /// "Open": the user is mid-flow on the page they filed from and rarely wants to leave it, whereas the recoverable
    /// mistake — wrong playlist, wrong row, a multi-selection they had forgotten about — is both common and, until now,
    /// only recoverable by going to find the notification panel. A create-then-add toasts <b>Open</b> instead
    /// (see <see cref="CreateAndAdd"/>): you just made a playlist and probably want to name it.</summary>
    internal static void ToastDeposited(ActionServices s, string name, long activityId)
    {
        var nc = s.Svc?.Notifications;
        Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
        {
            Severity = InfoBarSeverity.Success,
            ActionLabel = nc is not null && activityId >= 0 ? Loc.Get(Strings.Notifications.Undo) : null,
            OnAction = nc is not null && activityId >= 0 ? () => _ = nc.UndoByIdAsync(activityId) : null,
        });
    }

    /// <summary>The confirmation for a completed REMOVE. Same reasoning as <see cref="ToastDeposited"/>, and the same
    /// action slot: a remove is the edit most often made by accident, so Undo is the only thing worth offering — and
    /// until now the only way back was the notification panel, which nobody opens in the second they realise.</summary>
    internal static void ToastRemoved(ActionServices s, int count, long activityId)
    {
        var nc = s.Svc?.Notifications;
        Toast.Show(Strings.Detail.Edit.RemovedFromPlaylist(count), new ToastOptions
        {
            Severity = InfoBarSeverity.Success,
            ActionLabel = nc is not null && activityId >= 0 ? Loc.Get(Strings.Notifications.Undo) : null,
            OnAction = nc is not null && activityId >= 0 ? () => _ = nc.UndoByIdAsync(activityId) : null,
        });
    }

    /// <summary>"New playlist" for a deposit whose payload is not in hand (a container): create, then run the same
    /// deposit the named rows run. The track path keeps <see cref="CreateAndAdd"/>, which can add inside one flow.</summary>
    static void CreateAndDeposit(ActionServices s, Action<string, string> deposit)
    {
        // SYNCHRONOUS create (PlaylistCreateFlow): the optimistic row is in the store the instant this returns, so the
        // deposit runs in the same gesture instead of waiting on a round trip that may never come back.
        if (PlaylistCreateFlow.Create(s, default, navigate: false, out string name) is not { } created) return;
        deposit(created.Uri, name);
    }

    // ── Move to playlist ▸ (same picker, MOVE semantics: deposit into the target, then drop the source rows) ────────
    /// <summary>The menu equivalent of dragging these rows into another playlist — the a11y/Pragmatic answer to a drag
    /// (an outcome-equivalent command, never a simulated one). Offered only when the tracks sit in an editable playlist
    /// whose membership rows we can actually remove; otherwise there is nothing to move OUT of and Add already covers it.
    /// <para>It is honestly a COPY-then-REMOVE, not an atomic server move: Spotify exposes no cross-playlist move op.
    /// The order is deliberate — a failed add leaves the source untouched, whereas removing first can lose the rows.</para></summary>
    static MenuFlyoutItem? MoveToPlaylistItem(in ActionContext ctx)
    {
        var s = ctx.S;
        var tracks = ctx.Target.Tracks;
        if (s.Library is null || tracks.Count == 0) return null;
        if (!ActionRules.CanRemoveFromPlaylist(ctx.Target.Host) || ctx.Target.Host is not { } host) return null;

        // moving into the source playlist is a no-op → it is excluded from the list entirely.
        return PlaylistDepositItem(s, Loc.Get(Strings.Menu.MoveToPlaylist), Icons.Forward, canAdd: true,
            deposit: (uri, name) => MoveTo(s, uri, name, tracks, host),
            createAndDeposit: () => CreateAndMove(s, tracks, host),
            excludeUri: host.PlaylistUri, pickerTracks: tracks);
    }

    static void MoveTo(ActionServices s, string uri, string name, IReadOnlyList<Track> tracks, PlaylistHost host)
    {
        if (s.Library is not { } lib || tracks.Count == 0 || host.Rows.Count == 0) return;
        if (string.Equals(uri, host.PlaylistUri, StringComparison.Ordinal)) return;
        // Undo payload for the remove half (uri/title/uid per row), exactly as RemoveFromThisPlaylist records it.
        var refs = new ActivityTrackRef[tracks.Count];
        for (int i = 0; i < refs.Length; i++)
            refs[i] = new ActivityTrackRef(tracks[i].Uri, tracks[i].Title, tracks[i].ContextUid);
        string from = host.PlaylistUri;
        var rows = host.Rows;
        _ = Run();

        async Task Run()
        {
            try
            {
                await lib.AddTracksAsync(uri, tracks).ConfigureAwait(false);
                await lib.RemovePlaylistRowsAsync(from, rows, refs).ConfigureAwait(false);
                RememberDeposit(s, uri);
                Toast.Show(Strings.Menu.MovedToPlaylist(name), new ToastOptions
                {
                    Severity = InfoBarSeverity.Success,
                    ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => s.Go?.Invoke("pl:" + uri, name),
                });
            }
            // Mapped, not raw: PlaylistEditErrors turns "offline", "revision conflict" and the rest into a sentence a
            // listener can act on, where ex.Message is engine prose.
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
        }
    }

    static void CreateAndMove(ActionServices s, IReadOnlyList<Track> tracks, PlaylistHost host)
    {
        if (s.Library is null || tracks.Count == 0) return;
        if (PlaylistCreateFlow.Create(s, default, navigate: false, out string name) is not { } created) return;
        MoveTo(s, created.Uri, name, tracks, host);
    }

    /// <summary>Add to an existing playlist. AWAITED, then confirmed — the old shape fired the write and toasted
    /// "Added to X" unconditionally, so a failed add (offline, revoked permissions, a rejected revision) reported
    /// SUCCESS and the only trace was an entry flipped to Failed in a panel nobody was looking at. That is a trust bug in
    /// the middle of the flow this whole pass is about, so the correct shape (await → map the exception through
    /// PlaylistEditErrors) is now shared by every add path.</summary>
    static void AddTo(ActionServices s, string uri, string name, IReadOnlyList<Track> tracks)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        var post = s.Post;
        _ = Run();
        async Task Run()
        {
            try
            {
                long id = await lib.AddTracksTrackedAsync(uri, tracks).ConfigureAwait(false);
                ContainerActions.Post(post, () =>
                {
                    RememberDeposit(s, uri);
                    ToastDeposited(s, name, id);
                });
            }
            catch (Exception ex) { ContainerActions.Post(post, () => PlaylistEditErrors.Toast(ex)); }
        }
    }

    /// <summary>"New playlist" for tracks in hand: create with the next unused "<c>My Playlist #N</c>" name, add, then
    /// toast with <b>Open</b> — the one deposit where leaving IS what the user wants next (the new playlist needs a name,
    /// and inline rename lives on its page). Every "New playlist" used to create another playlist literally called
    /// "New playlist", so a few of them were indistinguishable in the sidebar.</summary>
    static void CreateAndAdd(ActionServices s, IReadOnlyList<Track> tracks)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        if (PlaylistCreateFlow.Create(s, default, navigate: false, out string name) is not { } created) return;
        string uri = created.Uri;
        var post = s.Post;
        _ = Run();
        async Task Run()
        {
            try
            {
                await lib.AddTracksAsync(uri, tracks).ConfigureAwait(false);
                ContainerActions.Post(post, () =>
                {
                    RememberDeposit(s, uri);
                    Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
                    {
                        Severity = InfoBarSeverity.Success,
                        ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => s.Go?.Invoke("pl:" + uri, name),
                    });
                });
            }
            catch (Exception ex) { ContainerActions.Post(post, () => PlaylistEditErrors.Toast(ex)); }
        }
    }

    /// <summary>The name a one-click "New playlist" gets: the next unused "<c>{localized base} #N</c>". Nothing computed
    /// this before — every new playlist was called "New playlist" verbatim.</summary>
    internal static string NextPlaylistName(ActionServices s)
        => PlaylistDepositTargets.NextDefaultName(s.Store?.Playlists.Value.Peek(), Loc.Get(Strings.Sidebar.NewPlaylist));

    /// <summary>"More playlists…" — the full existing PlaylistPickerPanel, hosted in a centered ContentDialog (the
    /// originating menu is gone by invoke time, so there is no anchor rect to open a flyout at).
    /// <para>A <paramref name="deposit"/> override carries its own payload (a container resolves its tracks at deposit
    /// time), so the empty-track guard applies only to the in-hand path.</para></summary>
    static void OpenPicker(ActionServices s, IReadOnlyList<Track> tracks,
                           string? title = null, Action<string, string>? deposit = null, string? exclude = null)
    {
        if (s.Overlay is not { } overlay || s.Library is null || (deposit is null && tracks.Count == 0)) return;
        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = title ?? Loc.Get(Strings.Detail.AddToPlaylist);
            d.PrimaryText = "";                                   // rows act; the dialog only needs a dismiss
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = Embed.Comp(() => new PlaylistPickerPanel
            {
                GetTracks = () => tracks,
                Close = () => handle?.Close(),
                Deposit = deposit,
                ExcludeUri = exclude,
            });
        });
    }

    // ── Containers ───────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A media-card menu inferred from the card's uri (cards carry only uri + title) — the CONTAINER grammar:
    /// Primary [Play · Play next · Play after · Save/Follow] + rows [Follow (artist), Add to playlist ▸, Open, Pin/Unpin,
    /// Go to artist (album), Share ▸, artist radio (artist)].
    ///
    /// <para>Two kinds omit part of the core, each for a reason recorded at the seam rather than by silence:
    /// an ARTIST has no resolvable track set (locked decision on <see cref="ContainerTracks"/>), so it gets neither the
    /// queue verbs nor Add-to-playlist; a SHOW has no <c>Track</c> at all, so its arm is Play · Open · Pin · Share.
    /// A track uri gets the thin track shape (<see cref="TrackUriCard"/>); an EPISODE uri gets no menu, because there is
    /// no episode detail route to Open and no episode-aware deposit seam. Unknown schemes get no menu.</para></summary>
    public static ContextMenuModel? Card(ActionServices s, string uri, string name,
        Image? image = null, string? subtitle = null, bool circular = false)
    {
        if (uri is not { Length: > 0 }) return null;
        // The card's grammar follows its KIND, read by the ONE parser (hydration-facade-design.md §1.1) rather than by
        // substring probes. Episode and every unrecognised scheme still fall through to "no menu", as documented above.
        var kind = EntityUri.KindOf(uri);
        if (kind == EntityKind.Track) return TrackUriCard(s, uri, name, image, subtitle);
        if (kind == EntityKind.Show) return ShowCard(s, uri, name, image, subtitle);

        // Every liked spelling, not just the canonical one: a card built from a Home/recents section item can carry
        // `spotify:user:<u>:collection`, whose EntityKind is Collection — so the ladder below fell through to
        // TargetKind.None and the card got NO MENU AT ALL, not merely the wrong Save verb.
        bool liked = LikedSongsArtwork.IsLikedUri(uri);
        ActionTarget target =
            kind == EntityKind.Album ? ActionTarget.ForAlbum(uri, name)
            : kind == EntityKind.Artist ? ActionTarget.ForArtist(uri, name)
            : kind == EntityKind.Playlist || liked ? ActionTarget.ForPlaylist(uri, name)
            : default;
        if (target.Kind == TargetKind.None) return null;

        var ctx = new ActionContext(target, s);
        return new ContextMenuModel(ContainerStrip(in ctx, liked), ContainerRows(in ctx),
            Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : KindLabel(target.Kind), circular));
    }

    /// <summary>The container transport strip: Play · Play next · Play after · Save. The queue pair is present exactly
    /// when the kind HAS a track set (album/playlist — see <see cref="ContainerTracks"/>); Liked Songs drops Save
    /// because it cannot be un-saved.</summary>
    static AppBarCommand[] ContainerStrip(in ActionContext ctx, bool liked)
    {
        var strip = new List<AppBarCommand>(4) { ContainerActions.PlayContext.ToBarCommand(ctx) };
        if (ContainerTracks.CanResolve(in ctx))
        {
            strip.Add(ContainerActions.PlayContextNext.ToBarCommand(ctx));
            strip.Add(ContainerActions.AddContextToQueue.ToBarCommand(ctx));
        }
        if (!liked) strip.Add(ContainerActions.SaveContext.ToBarCommand(ctx));
        return strip.ToArray();
    }

    /// <summary>The container rows, in grammar order: state → collection → navigation → Share → surface extras. Shared
    /// by the card menu and the sidebar album/artist arms, so the two can never drift.</summary>
    static List<MenuFlyoutItem> ContainerRows(in ActionContext ctx)
    {
        var rows = new List<MenuFlyoutItem>(7);
        // Follow / Unfollow as a ROW on artist menus (Spotify shows it as a row, not only the strip toggle).
        if (ctx.Target.Kind == TargetKind.Artist) rows.Add(ContainerActions.SaveContext.ToMenuItem(ctx));
        if (ContainerAddToPlaylistItem(in ctx) is { } add) rows.Add(add);
        rows.Add(ContainerActions.OpenItem.ToMenuItem(ctx));
        // Pin/Unpin immediately after Open (F.5.3 / §3.2.11) — the one documented deviation from "collection before
        // navigation": pin answers "where does this live", which reads as part of Open. A non-pinnable uri (a track
        // card) gets no row at all — decided in ONE place by SidebarPinId, never per menu.
        if (PinActions.Row(in ctx) is { } pinRow) rows.Add(pinRow);
        // The album card's Go-to-artist: the artist is resolved at invoke (the card carries no artists) — see the action.
        if (ctx.Target.Kind == TargetKind.Album) rows.Add(ContainerActions.GoToAlbumArtist.ToMenuItem(ctx));
        rows.Add(ShareItem(in ctx));
        if (ctx.Target.Kind == TargetKind.Artist) rows.Add(ContainerActions.GoToArtistRadio.ToMenuItem(ctx));
        return rows;
    }

    static string KindLabel(TargetKind kind) => kind switch
    {
        TargetKind.Album => "Album",
        TargetKind.Artist => "Artist",
        TargetKind.Playlist => "Playlist",
        _ => "",
    };

    /// <summary>A SHOW / podcast card: Play · Open · Pin · Share ▸. Built from explicit rows rather than a new
    /// <c>TargetKind.Show</c> — the same explicit non-goal <see cref="SidebarEntry"/> records; if that kind ever lands,
    /// this arm and the sidebar's migrate onto it together. No track-set verbs: a show's items are episodes, and
    /// <c>Wavee.Core</c> models an episode as its own record, not a <c>Track</c>.</summary>
    static ContextMenuModel ShowCard(ActionServices s, string uri, string name, Image? image, string? subtitle)
        => new(ShowRows(s, uri, name, "show:" + uri,
                        PinActions.RowForId(s, SidebarPinId.FromUri(uri), SidebarEntryKind.Show, uri, name)),
               Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : "Podcast"));

    /// <summary>The show row list, shared by the card arm and the sidebar arm. <paramref name="pinRow"/> is passed in
    /// because the two surfaces know their pin id differently (a card derives it from the uri, a projected row already
    /// IS one) — same rule, same toasts, both through <see cref="PinActions"/>.</summary>
    static List<MenuFlyoutItem> ShowRows(ActionServices s, string uri, string name, string? route, MenuFlyoutItem? pinRow)
    {
        var rows = new List<MenuFlyoutItem>(4)
        {
            new(Loc.Get(Strings.Detail.Play), ActionIcons.Resolve(ActionIcons.Play),
                s.Svc?.Player is not null && uri.Length > 0, () => { _ = s.Svc?.Player.PlayAsync(uri); }),
            new(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open),
                s.Go is not null && route is { Length: > 0 }, () => s.Go?.Invoke(route!, name)),
        };
        if (pinRow is { } pin) rows.Add(pin);
        rows.Add(ShareItem(new ActionContext(LinkTarget(uri, name), s)));
        return rows;
    }

    /// <summary>A SHARE-ONLY target: it carries the uri the Share submenu links and nothing else. <see cref="TargetKind.None"/>
    /// is deliberate — no verb is enabled by it, and the three Share actions read only <c>Uri</c>/<c>Tracks</c>
    /// (<see cref="SpotifyLink"/>). This is how a show gets the app-wide Share submenu without inventing the
    /// <c>TargetKind.Show</c> the sidebar design lists as an explicit non-goal.</summary>
    static ActionTarget LinkTarget(string uri, string name)
        => new() { Kind = TargetKind.None, Tracks = Array.Empty<Track>(), Uri = uri, Name = name };

    /// <summary>A card that is a bare track URI: the full track strip [Play · Play next · Play after · Save] + rows
    /// [Add to playlist ▸, Share ▸, Go to song radio].
    ///
    /// <para><b>Go to album / Go to artist are absent here, and that is a LAST RESORT, not the shape a song menu should
    /// have.</b> They need the track's album/artist URIs and this model carries a uri and a title. A surface that can
    /// resolve the real <see cref="Track"/> must therefore attach <see cref="TrackAttach"/> (the full track menu)
    /// instead of a card menu — which is exactly what the search results page does with the track already sitting in its
    /// own <c>SearchResults.Tracks</c>. Song radio survives because it seeds off the <b>uri alone</b>.</para></summary>
    static ContextMenuModel TrackUriCard(ActionServices s, string uri, string name, Image? image, string? subtitle)
    {
        var target = ActionTarget.ForTracks(new[]
        {
            new Track("", uri, name, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null),
        });
        var ctx = new ActionContext(target, s);
        var rows = new List<MenuFlyoutItem>(3) { AddToPlaylistItem(in ctx), ShareItem(in ctx) };
        if (ActionRules.CanStartTrackRadio(in ctx.Target)) rows.Add(TrackActions.GoToSongRadio.ToMenuItem(ctx));
        return new ContextMenuModel(TrackTransportStrip(in ctx), rows,
            Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : "Song"));
    }

    /// <summary>The card attach helper for shared element factories: null when the action system isn't provided.</summary>
    public static MenuAttach? CardAttach(ActionServices? s, IOverlayService overlay, string uri, string name,
        Image? image = null, string? subtitle = null, bool circular = false)
        => s is null ? null : new MenuAttach(overlay, () => Card(s, uri, name, image, subtitle, circular));

    /// <summary>A single-track attach (eager rows that DO carry the full Track — search "Songs", fallbacks).</summary>
    public static MenuAttach? TrackAttach(ActionServices? s, IOverlayService overlay, Track track)
        => s is null ? null : new MenuAttach(overlay, () => TrackContextMenu.BuildSingle(s, track));

    // ── Sidebar playlist row (transport strip + grouped rows) ────────────────────────────────────────────────────────
    /// <summary>The action context a SIDEBAR playlist row acts through. Sidebar summaries carry only CanEdit/IsOwner, so
    /// this is the one place that maps them onto the capabilities shape the actions gate on — the row menu and the row's
    /// F2 accelerator share it, which is what stops the keyboard verb and the menu verb from disagreeing about who may
    /// rename what.</summary>
    static ActionContext SidebarPlaylistCtx(ActionServices s, string uri, string name, bool isOwner, bool canEdit)
    {
        var caps = new PlaylistCapabilities(
            CanView: true, CanEditItems: canEdit, CanEditMetadata: isOwner,
            IsCollaborative: canEdit && !isOwner, IsOwner: isOwner,
            CanAdministratePermissions: isOwner);
        var host = new PlaylistHost(uri, caps, Array.Empty<PlaylistRowRef>());
        return new ActionContext(ActionTarget.ForPlaylist(uri, name, host), s);
    }

    /// <summary><b>F2</b> on a sidebar row, or null when the row has nothing to rename. The KEYBOARD path to the same
    /// verb the row menu offers — built from the same context and the same commands, never a second implementation.
    /// A folder renames through <see cref="FolderActions"/>; a playlist through <see cref="ContainerActions.RenamePlaylist"/>
    /// (owner-only, which its own enablement decides — not a second copy of the rule here).</summary>
    public static Action? SidebarRenameAction(ActionServices s, in SidebarLibraryEntry e)
    {
        if (s.Library is null || s.Overlay is null) return null;
        if (e.Kind == SidebarEntryKind.Folder)
        {
            if (e.FolderId.Length == 0) return null;
            string folderId = e.FolderId, folderName = e.Name;
            return () => FolderActions.Rename(s, folderId, folderName);
        }
        if (e.Kind != SidebarEntryKind.Playlist || e.Uri.Length == 0) return null;
        var ctx = SidebarPlaylistCtx(s, e.Uri, e.Name, e.IsOwner, e.CanEdit);
        if (!ContainerActions.RenamePlaylist.EnabledFor(in ctx)) return null;
        return () => ContainerActions.RenamePlaylist.Execute(ctx);
    }

    /// <summary>The playlist row menu — the CONTAINER grammar, GROUPED so a fourteen-row flat list reads as four short
    /// ones (the Win11 Explorer shape, and the shape Wavee's own TRACK menu already had):
    /// <code>
    /// [ Play · Play next · Play after · Saved ]  ← the labeled command strip (ContainerStrip, the card menu's)
    ///   Add to playlist ▸ · Open
    ///   ─
    ///   Organize ▸ (Move up · Move down · Move to folder… · Move out of {parent} · ─ · Pin/Unpin) · Rename playlist
    ///   ─
    ///   Access ▸ (Public · Private · ─ · Collaborative · ─ · Invite collaborators) · Share ▸
    ///   ─
    ///   Delete playlist
    /// </code>
    /// Nothing was dropped and no verb changed hands: the four transport verbs moved from rows into the strip, and the
    /// low-frequency positional / permission verbs moved one level down into the two submenus that name them.
    ///
    /// <para><b>Rename stays a top-level ROW</b> here, on the folder menu and on the detail page's owner menu — ONE
    /// grammar for all three. A second (bottom) icon strip would be a shape only this surface has, and the detail
    /// page's owner overflow has no strip at all to put it in.</para>
    ///
    /// <para><paramref name="organize"/> are the pane's positional verbs (Move up · Move down · Move to folder…), which
    /// only the renderer can compute — they LEAD the Organize submenu instead of arriving as trailing extras below the
    /// owner block, which is where "Move up" used to land after "Invite collaborators". See
    /// <see cref="SidebarMenuExtras"/>.</para></summary>
    static List<MenuFlyoutItem> SidebarPlaylistRows(ActionServices s, string uri, string name, bool isOwner, bool canEdit,
                                                   string parentFolderId = "", string parentFolderName = "",
                                                   string entryId = "", IReadOnlyList<MenuFlyoutItem>? organize = null)
    {
        var ctx = SidebarPlaylistCtx(s, uri, name, isOwner, canEdit);
        bool live = PlaylistInlineEdit.SpotifyEditsLive(s.Svc);
        var rows = new List<MenuFlyoutItem>(10);

        // GROUP 1 — primary. The transport four are the strip above these rows (SidebarPlaylistMenu), not rows.
        if (ContainerAddToPlaylistItem(in ctx) is { } add) rows.Add(add);
        rows.Add(ContainerActions.OpenItem.ToMenuItem(ctx));

        // GROUP 2 — organize: everything answering "where does this row LIVE". "Move out of {folder}" is nested-only
        // (absent, never disabled, at top level: there is nothing to move out of). The command addresses the row by its
        // projection ENTRY ID and re-reads the tree at invoke time, so it lands one level up from where the row IS, not
        // from where the menu was built. The folder name is clipped, so a long one cannot widen the whole flyout.
        MenuFlyoutItem? moveOut = parentFolderId.Length > 0
            ? new MenuFlyoutItem(Strings.Menu.MoveOutOf(MenuLabel.Clip(parentFolderName)),
                ActionIcons.Resolve(ActionIcons.Folder), s.Library is not null,
                () => FolderActions.MoveOut(s, entryId.Length > 0 ? entryId : SidebarPinId.Canonical(uri) ?? uri))
            : null;
        Group(rows, OrganizeItem(organize, moveOut,
            PinActions.RowForId(s, SidebarPinId.Canonical(uri), SidebarEntryKind.Playlist, uri, name)));
        if (isOwner) rows.Add(ContainerActions.RenamePlaylist.ToMenuItem(ctx));

        // GROUP 3 — access & sharing. Access ▸ is owner+live only (the permission verbs mean nothing otherwise); Share ▸
        // stays top-level, because sharing a link is neither owner-gated nor rare.
        if (rows.Count > 0 && !rows[^1].IsSeparator) rows.Add(MenuFlyoutItem.Separator);
        if (isOwner && live) rows.Add(AccessItem(in ctx, uri));
        rows.Add(ShareItem(in ctx));

        // GROUP 4 — destructive, LAST, behind its own separator.
        if (isOwner)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(ContainerActions.DeletePlaylist.ToMenuItem(ctx));
        }
        return rows;
    }

    /// <summary>Open a new group: append <paramref name="lead"/> behind a separator, or do nothing at all when the group
    /// is empty on this row — so a menu never renders a stray divider with nothing under it.</summary>
    static void Group(List<MenuFlyoutItem> rows, MenuFlyoutItem? lead)
    {
        if (lead is not { } row) return;
        if (rows.Count > 0 && !rows[^1].IsSeparator) rows.Add(MenuFlyoutItem.Separator);
        rows.Add(row);
    }

    /// <summary><b>Organize ▸</b> — the ONE submenu holding every verb that changes where a sidebar row LIVES, shared by
    /// the playlist arm and the folder arm: the positional moves the pane computes (Move up · Move down · Move to
    /// folder…), then "Move out of {parent}" for a nested row, then — behind a separator, because the pinned list is a
    /// different list — Pin to sidebar / Unpin. Null when none of them apply, so the row is absent rather than an empty
    /// cascade.</summary>
    static MenuFlyoutItem? OrganizeItem(IReadOnlyList<MenuFlyoutItem>? moves, MenuFlyoutItem? moveOut, MenuFlyoutItem? pin)
    {
        var items = new List<MenuFlyoutItem>(6);
        if (moves is { Count: > 0 })
            for (int i = 0; i < moves.Count; i++) items.Add(moves[i]);
        if (moveOut is { } lift) items.Add(lift);
        if (pin is { } pinRow)
        {
            if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
            items.Add(pinRow);
        }
        if (items.Count == 0) return null;
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.Organize), items, ActionIcons.Resolve(ActionIcons.Folder));
    }

    /// <summary>The playlist arm's full model: the container transport STRIP over the grouped rows above. The strip is
    /// <see cref="ContainerStrip"/> — the very same builder the media-card menu and (in its track form) the track menu
    /// use — so "Play · Play next · Play after · Saved" is one component with one enablement rule, not a sidebar copy.
    /// Liked Songs drops Save there exactly as it does on a card.</summary>
    static ContextMenuModel SidebarPlaylistMenu(ActionServices s, in SidebarLibraryEntry e, in SidebarMenuExtras extras)
    {
        var ctx = SidebarPlaylistCtx(s, e.Uri, e.Name, e.IsOwner, e.CanEdit);
        bool liked = string.Equals(e.Uri, SidebarPinId.LikedSongsUri, StringComparison.Ordinal);
        return new ContextMenuModel(
            ContainerStrip(in ctx, liked),
            SidebarPlaylistRows(s, e.Uri, e.Name, e.IsOwner, e.CanEdit, e.ParentFolderId, e.ParentFolderName, e.Id,
                                extras.Organize),
            Header(e.Cover, e.Uri, e.Name,
                e.OwnerName is { Length: > 0 } owner ? owner : Loc.Get(Strings.Sidebar.V3.Kind.Playlist)));
    }

    // ── Sidebar projected row (Library V3 + Curated) ─────────────────────────────────────────────────────────────────
    /// <summary>The menu EVERY V3/Curated row uses — one builder with a per-kind arm (§3.2.11), because a single entry
    /// record already carries the kind and every fact each arm needs. Rows-only (no command strip: a 240-DIP pane is not
    /// where an Explorer-style labeled strip belongs), destructive-last, with the pin row in the same place every arm.
    ///
    /// <list type="bullet">
    /// <item><b>Playlist</b> — <see cref="SidebarPlaylistMenu"/>: the container transport STRIP over the grouped rows
    /// (Add to playlist ▸ · Open · — · Organize ▸ · Rename · — · Access ▸ · Share ▸ · — · Delete).</item>
    /// <item><b>Album / Artist</b> — the container card model's rows: Play · Save/Follow (artist also gets its radio) ·
    /// Open · Pin · Share ▸.</item>
    /// <item><b>Show / podcast</b> — Play · Open · Pin · — · Copy link, built from explicit rows rather than a new
    /// <c>TargetKind.Show</c>. <b>Explicit non-goal</b> (§3.2.11): adding <c>TargetKind.Show</c> /
    /// <c>ActionTarget.ForShow</c>; if that lands later this arm migrates onto it.</item>
    /// <item><b>Folder</b> — the full folder verb set (<see cref="SidebarFolderRows"/>): Expand/Collapse · New playlist
    /// in this folder · New folder inside · — · Organize ▸ (Move up · Move down · Move to folder… · Move out of {parent}
    /// · — · Pin) · Rename folder · — · Delete folder. The old "locked decision 9" (no folder CRUD in the UI) is
    /// <b>LIFTED</b>: the rootlist create/rename/delete wire exists, so the verbs are real commands through
    /// <c>FolderActions</c>. A folder has no uri, so there is nothing to play and nothing to share — hence no strip.</item>
    /// <item><b>App route</b> — Open · Pin.</item>
    /// </list>
    ///
    /// <paramref name="toggleFolder"/> is the surface's own expansion closure (null ⇒ the folder arm omits the
    /// expand/collapse row rather than showing a dead one).
    ///
    /// <para><paramref name="extras"/> are the pane's per-row layout verbs, split by where the grammar puts them (see
    /// <see cref="SidebarMenuExtras"/>). The playlist and folder arms FOLD <c>Organize</c> into their Organize submenu —
    /// they are the only arms that have one — and everything else keeps the flat additive slot the queue row uses,
    /// inserted after Share and before any trailing destructive block, so drag is never the only way to reorder
    /// (P6).</para></summary>
    public static ContextMenuModel? SidebarEntry(ActionServices s, in SidebarLibraryEntry e,
        Action? toggleFolder = null, bool folderExpanded = false, SidebarMenuExtras extras = default)
    {
        // The two arms with an Organize submenu consume `extras.Organize` INSIDE it; only the trailing verbs (Remove)
        // are appended. Every other arm has nowhere to fold them into and takes the whole slot flat, as before.
        switch (e.Kind)
        {
            case SidebarEntryKind.Playlist:
                return WithLayoutExtras(SidebarPlaylistMenu(s, in e, in extras), extras.Trailing);
            case SidebarEntryKind.Folder:
                return WithLayoutExtras(SidebarFolderMenu(s, in e, toggleFolder, folderExpanded, in extras), extras.Trailing);
        }

        ContextMenuModel? menu = e.Kind switch
        {
            // The album/artist arms ARE the card menu — same target kinds, same verbs, same pin placement.
            SidebarEntryKind.Album or SidebarEntryKind.Artist => Card(s, e.Uri, e.Name, e.Cover,
                e.Creator is { Length: > 0 } ? e.Creator : null, e.Circular),

            SidebarEntryKind.Show => SidebarShowMenu(s, in e),
            SidebarEntryKind.AppRoute => SidebarRouteMenu(s, in e),
            SidebarEntryKind.Track => SidebarTrackMenu(s, in e),
            _ => null,
        };
        return WithLayoutExtras(menu, extras.Flat());
    }

    /// <summary>A LAYOUT-ONLY menu (an action shortcut, a hand-placed track): no entity verbs, just the pane's own
    /// extras, flat. Null extras open nothing at all rather than an empty flyout.</summary>
    public static ContextMenuModel? LayoutOnly(in SidebarMenuExtras extras) => WithLayoutExtras(null, extras.Flat());

    /// <summary>Append navbar-customization extras after the entity verbs and before a trailing destructive group
    /// (the playlist owner's Delete). A layout-only menu (an action shortcut, a hand-placed track) is just the extras.
    /// Null extras, or an empty list, leave the menu unchanged — including a null menu, which still opens nothing.</summary>
    public static ContextMenuModel? WithLayoutExtras(ContextMenuModel? menu, IReadOnlyList<MenuFlyoutItem>? extras)
    {
        if (extras is not { Count: > 0 }) return menu;
        if (menu is not { } present) return new ContextMenuModel(CopyExtras(extras));

        var rows = new List<MenuFlyoutItem>(present.Rows.Count + extras.Count + 1);
        for (int i = 0; i < present.Rows.Count; i++) rows.Add(present.Rows[i]);

        int at = rows.Count;
        // A trailing separator + one command is the destructive-last block (Delete playlist). Insert extras in front
        // of that separator so Move up/down never land after a delete, and Remove stays with the extras rather than
        // swapping places with an owner verb.
        if (rows.Count >= 2 && rows[^2].IsSeparator) at = rows.Count - 2;

        if (at > 0 && !rows[at - 1].IsSeparator) rows.Insert(at++, MenuFlyoutItem.Separator);
        for (int i = 0; i < extras.Count; i++) rows.Insert(at++, extras[i]);
        return present with { Rows = rows };
    }

    static List<MenuFlyoutItem> CopyExtras(IReadOnlyList<MenuFlyoutItem> extras)
    {
        var rows = new List<MenuFlyoutItem>(extras.Count);
        for (int i = 0; i < extras.Count; i++) rows.Add(extras[i]);
        return rows;
    }

    /// <summary>Feed TRACK rows — the only producers are the track-yielding data sources (<c>wavee.queue</c>,
    /// <c>wavee.nowPlaying</c>, <c>wavee.artist.topTracks</c>); <c>SidebarProjection</c> never emits one. The TRACK
    /// grammar as plain rows (a 240-DIP pane is not where an Explorer-style labeled strip belongs): Play · Play next ·
    /// Play after · Save · then <see cref="TrackRows"/>.
    ///
    /// <para>Two deliberate ABSENCES survive the convergence. No pin row: locked decision 4 keeps tracks unpinnable and
    /// <c>SidebarPinId.FromEntry</c> refuses them, so a row here would be a promise the store rejects. No Open row: a
    /// track has no detail route (<c>RouteKey</c> is null by construction). The old "no Play row either — activating the
    /// row plays it" argument did NOT survive: it left this the one track menu in the app with a different core set, and
    /// the click affordance is not a reason to withhold the verb (every other track surface is clickable too).</para>
    ///
    /// <para>Go-to-album/artist self-select: a projected row carries an artist NAME with no uri (see
    /// <see cref="TrackFromEntry"/>), and <see cref="ActionRules.CanGoToArtist"/> / the album-uri check drop those rows
    /// rather than navigating to an empty route.</para></summary>
    static ContextMenuModel SidebarTrackMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        var ctx = new ActionContext(ActionTarget.ForTracks([TrackFromEntry(in e)]), s);
        var rows = new List<MenuFlyoutItem>(12);
        AddTrackTransportRows(rows, in ctx);
        rows.AddRange(TrackRows(in ctx));
        return new ContextMenuModel(rows, Header(e.Cover, e.Uri, e.Name,
            e.FirstArtistName is { Length: > 0 } artist ? artist : e.Creator is { Length: > 0 } ? e.Creator : null));
    }

    /// <summary>The minimal <see cref="Track"/> the queue verbs need from a projected row. <c>SidebarSourceMap.FromTrack</c>
    /// keeps only what a ROW draws, so the original Track is gone by the time a menu opens — this rebuilds exactly the
    /// fields <c>DetailQueueActions.BuildMetadata</c> puts on the wire (title · artist · image) and leaves the rest at
    /// their honest zero: duration 0 and a blank album ref, because a projected row genuinely does not know them, and
    /// <c>Availability</c> unknown, because only <c>getAlbum</c>/<c>getTrack</c> carry a server verdict and inventing one
    /// here would make "nobody told us" indistinguishable from "confirmed playable".</summary>
    static Track TrackFromEntry(in SidebarLibraryEntry e) => new(
        Id: e.Id, Uri: e.Uri, Title: e.Name,
        Artists: e.FirstArtistName is { Length: > 0 } artist
            ? [new ArtistRef("", "", artist)]
            : Array.Empty<ArtistRef>(),
        Album: new AlbumRef("", "", ""),
        DurationMs: 0, IsExplicit: false, Image: e.Cover);

    /// <summary>Show / podcast rows — the SAME list a podcast CARD gets (<see cref="ShowRows"/>), so the sidebar arm and
    /// the card arm cannot drift. Built explicitly (no <c>TargetKind.Show</c> — see <see cref="SidebarEntry"/>). The
    /// pin row comes from the entry (its <c>Id</c> already is the pin id) rather than from the uri.</summary>
    static ContextMenuModel SidebarShowMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        var rows = ShowRows(s, e.Uri, e.Name, e.RouteKey, PinActions.RowForEntry(s, in e));
        return new ContextMenuModel(rows, Header(e.Cover, e.Uri, e.Name,
            e.Publisher is { Length: > 0 } publisher ? publisher : Loc.Get(Strings.Sidebar.V3.Kind.Show)));
    }

    /// <summary>Playlist-folder rows, shared by every design through <see cref="SidebarEntry"/> so Classic, Library V3
    /// and Wavee Curated get the same folder verbs from one builder.
    ///
    /// <para>ORDER (the container grammar, applied to a thing that holds things): the disclosure and creation verbs the
    /// row is mostly used for lead — Expand/Collapse · New playlist in this folder · New folder inside — then a
    /// separator, then the management block (<b>Organize ▸</b> · Rename folder), then a separator and
    /// <b>Delete folder</b>, destructive-last exactly as a playlist's Delete is. A folder has no transport strip: there
    /// is nothing to play.</para>
    ///
    /// <para>Organize ▸ is the SAME submenu the playlist arm gets (<see cref="OrganizeItem"/>) — Move up · Move down ·
    /// Move to folder… · Move out of {parent} · — · Pin — so the two row kinds cannot grow different answers to "where
    /// does this live". Pin and Move out of used to sit flat in the management block, and the positional verbs arrived
    /// separately as trailing extras.</para>
    ///
    /// <para>The old "locked decision 9" — folder create/rename/delete deferred, and not to appear even disabled — is
    /// <b>LIFTED</b>. The rootlist wire for all three landed with P3, so these are real commands
    /// (<see cref="FolderActions"/>), not promises. What has NOT changed is where the writes go: the rootlist is written
    /// through the resource-drop seam and <c>FolderActions</c>, and through nothing else.</para>
    ///
    /// <para>"Move out of {parent}" is present only on a NESTED folder (<c>ParentFolderId</c> non-empty) — a top-level
    /// folder has nothing to move out of, so the row is absent rather than disabled.</para></summary>
    static List<MenuFlyoutItem> SidebarFolderRows(ActionServices s, in SidebarLibraryEntry e, Action? toggle, bool expanded,
                                                  IReadOnlyList<MenuFlyoutItem>? organize = null)
    {
        // A folder row's FolderId IS its own group id (the projection's contract) — never strip the pin prefix off Id.
        string folderId = e.FolderId;
        string entryId = e.Id;
        string name = e.Name;
        int childCount = e.ChildCount;
        string parentId = e.ParentFolderId;
        string parentName = e.ParentFolderName;
        bool live = s.Library is not null;
        var rows = new List<MenuFlyoutItem>(9);
        if (toggle is not null)
            rows.Add(new MenuFlyoutItem(
                Loc.Get(expanded ? Strings.Sidebar.Item.CollapseFolder : Strings.Sidebar.Item.ExpandFolder),
                new IconRef { Glyph = expanded ? Icons.ChevronUp : Icons.ChevronDown, Font = Theme.IconFont },
                true, toggle));
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.NewPlaylistHere), ActionIcons.Resolve(ActionIcons.Add),
            live && folderId.Length > 0, () => FolderActions.NewPlaylistIn(s, folderId)));
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.NewFolderInside), ActionIcons.Resolve(ActionIcons.Folder),
            live && s.Overlay is not null && folderId.Length > 0, () => FolderActions.NewFolder(s, folderId)));
        rows.Add(MenuFlyoutItem.Separator);
        // Organize ▸ — the same submenu the playlist arm gets, over this folder's own moves and pin.
        MenuFlyoutItem? moveOut = parentId.Length > 0
            ? new MenuFlyoutItem(Strings.Menu.MoveOutOf(MenuLabel.Clip(parentName)),
                ActionIcons.Resolve(ActionIcons.Folder), live,
                () => FolderActions.MoveOut(s, entryId))
            : null;
        Group(rows, OrganizeItem(organize, moveOut, PinActions.RowForEntry(s, in e)));
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.RenameFolder), ActionIcons.Resolve(ActionIcons.Rename),
            live && s.Overlay is not null && folderId.Length > 0, () => FolderActions.Rename(s, folderId, name)));
        rows.Add(MenuFlyoutItem.Separator);
        // Overlay required: FolderActions.Delete confirms first, and a null overlay would delete without asking.
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.DeleteFolder), ActionIcons.Resolve(ActionIcons.Delete),
            live && s.Overlay is not null && folderId.Length > 0,
            () => FolderActions.Delete(s, folderId, name, childCount)));
        return rows;
    }

    static ContextMenuModel SidebarFolderMenu(ActionServices s, in SidebarLibraryEntry e, Action? toggle, bool expanded,
                                              in SidebarMenuExtras extras)
        => new(SidebarFolderRows(s, in e, toggle, expanded, extras.Organize),
            header: Header(null, e.Id, e.Name, Strings.Sidebar.V3.ItemCount(e.ChildCount)));

    /// <summary>Pinned/static app-route rows: Open · Pin.</summary>
    static ContextMenuModel SidebarRouteMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        string? route = e.RouteKey;
        string name = e.Name;
        var rows = new List<MenuFlyoutItem>(2)
        {
            new(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open),
                s.Go is not null && route is { Length: > 0 }, () => s.Go?.Invoke(route!, name)),
        };
        if (PinActions.RowForEntry(s, in e) is { } pinRow) rows.Add(pinRow);
        return new ContextMenuModel(rows, Header(null, e.Id, name, null));
    }

    // (The local clipboard helper the show / sidebar-track arms used for their bare "Copy link" row is gone: both arms
    // now carry the app-wide Share ▸ submenu, which runs the TrackActions.CopyLink path — one clipboard behaviour.)

    /// <summary><b>Access ▸</b> — the ONE submenu for who may see and who may edit this playlist: Public · Private (an
    /// absolute radio pair) · — · Collaborative (a toggle) · — · Invite collaborators. It absorbs the old top-level
    /// <c>Visibility ▸</c> submenu AND the Invite row that used to sit loose beside it: three owner-only permission
    /// verbs under one name is one row where there were two, and the invite link is what a reader is looking for the
    /// moment they set Collaborative.
    /// <para>The state rows are still ABSOLUTE (each one SETS what it names), CHECKED against the live store header read
    /// at menu-open. That header is the canonical permission state — seeded when the playlist opens and flipped in place
    /// by a dealer permission push — so the check mark agrees with the detail page's access flyout without either
    /// surface issuing a request. A backend with no real store gets unchecked absolute rows, because inventing a checked
    /// state would invert the user's intent.</para></summary>
    static MenuFlyoutItem AccessItem(in ActionContext ctx, string uri)
    {
        var s = ctx.S;
        var header = s.Svc?.RealStore?.GetPlaylist(uri);
        bool known = header is not null;
        bool isPublic = header?.IsPublic ?? false;
        bool collaborative = header?.Capabilities.IsCollaborative ?? false;
        var items = new List<MenuFlyoutItem>(6)
        {
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Menu.MakePublic), known && isPublic, () => ContainerActions.SetVisibility(s, uri, true)),
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Menu.MakePrivate), known && !isPublic, () => ContainerActions.SetVisibility(s, uri, false)),
            MenuFlyoutItem.Separator,
            MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.Edit.Collaborative), collaborative,
                () => ContainerActions.SetCollaborative(s, uri, !collaborative)),
            MenuFlyoutItem.Separator,
            ContainerActions.InviteCollaborators.ToMenuItem(ctx),
        };
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.Access), items, ActionIcons.Resolve(ActionIcons.Globe));
    }

    // ── Queue entry ─────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The TRACK grammar with the queue's own extras. Primary [Play now · Play next · Play after · Save];
    /// rows = <see cref="TrackRows"/> (Add to playlist ▸ · Go to album · Go to artist(s) · Share ▸ · credits · song
    /// radio) + the queue extras [Move up, Move down] + the destructive [Remove from queue].
    ///
    /// <para>Before the convergence this menu offered Go-to-album/artist and a bare Copy link and nothing else — no
    /// Add-to-playlist, no Share submenu, no credits. Only <b>Play now</b> is queue-specific (a skip-in-place, which is
    /// not what <c>Play</c> means anywhere else), so it replaces Play in the strip and the panel's closure drives it.</para>
    /// <paramref name="playNow"/> is the panel's skip-in-place; <paramref name="removeFromDisplay"/> the panel's
    /// remove closure (null when a remote viewer — the row is then absent).</summary>
    public static ContextMenuModel QueueEntry(ActionServices s, QueueEntry entry, Action? removeFromDisplay, Action playNow,
        Action? moveUp = null, Action? moveDown = null)
    {
        var ctx = new ActionContext(ActionTarget.ForQueueEntry(entry, removeFromDisplay), s);
        var primary = new[]
        {
            new AppBarCommand(Icons.Play, Loc.Get(Strings.Menu.PlayNow), playNow),
            TrackActions.PlayNext.ToBarCommand(ctx),
            TrackActions.AddToQueue.ToBarCommand(ctx),
            TrackActions.ToggleLike.ToBarCommand(ctx),
        };
        List<MenuFlyoutItem>? extras = null;
        if (moveUp is not null)
            (extras ??= new List<MenuFlyoutItem>(2)).Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveUp),
                new IconRef { Glyph = Icons.ChevronUp, Font = Theme.IconFont }, true, moveUp));
        if (moveDown is not null)
            (extras ??= new List<MenuFlyoutItem>(2)).Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveDown),
                new IconRef { Glyph = Icons.ChevronDown, Font = Theme.IconFont }, true, moveDown));
        // TrackRows adds the Remove-from-queue row itself for a QueueEntry target with a remove closure.
        var rows = TrackRows(in ctx, showGoToAlbum: true, extras);
        var t = entry.Track;
        string section = entry.Provider switch
        {
            QueueProvider.Queue => "Next in queue",
            QueueProvider.Autoplay => "Autoplay",
            _ => "Next up",
        };
        string artists = DetailFormat.ArtistNames(t.Artists);
        return new ContextMenuModel(primary, rows,
            Header(t.Image, t.Uri, t.Title,
                artists.Length > 0 ? $"{artists} · {section}" : section));
    }

    // ── Player-bar now playing ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The track menu for the now-playing cluster: Host = null → no Remove rows.</summary>
    public static ContextMenuModel NowPlaying(ActionServices s, Track track)
        => Tracks(new ActionContext(ActionTarget.ForNowPlaying(track), s));

    static ContextMenuHeader TrackHeader(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 1)
        {
            var track = tracks[0];
            string artists = DetailFormat.ArtistNames(track.Artists);
            string subtitle = track.Album is { Name.Length: > 0 } album && artists.Length > 0
                ? $"{artists} · {album.Name}"
                : artists;
            return Header(track.Image, track.Uri, track.Title, subtitle);
        }

        var first = tracks.Count > 0 ? tracks[0] : null;
        string summary = first is null ? "" : first.Title;
        if (tracks.Count > 1) summary += $"  +{tracks.Count - 1} more";
        return Header(first?.Image, first?.Uri ?? "", $"{tracks.Count} songs selected", summary);
    }

    /// <summary>The ONE menu-header constructor — and therefore the ONE place a header string is made safe.
    ///
    /// <para><b>Every entity subtitle in this app can be an HTML FRAGMENT.</b> The Spotify mappers build card/row
    /// subtitles as markup ("Song • &lt;a href=&quot;spotify:artist:…&quot;&gt;Name&lt;/a&gt;" —
    /// <c>SpotifyExportMapper.ArtistLinks</c>) because the ROW renderers parse them with <c>RichText</c> into
    /// individually clickable artist links. A menu header is a plain <c>TextEl</c>, so a producer that forwards the same
    /// string verbatim renders the raw tag at the user (the reported defect: a search song's menu header read
    /// <c>Song • &lt;a href="spotify:artist:2Q3eZMfD…</c>).</para>
    ///
    /// <para>Fixing it at each producer is a rule every future producer has to remember, so it is fixed HERE instead:
    /// the header strips tags to text with the shared <c>SpotifyExportMapper.ToPlainText</c> walk (the same one the home
    /// card path uses), which early-outs on the common no-markup string without allocating. A header needs NAMES, not
    /// links.</para></summary>
    static ContextMenuHeader Header(Image? image, string key, string title, string? subtitle, bool circular = false)
    {
        // The liked collection's header art comes from the user's chosen treatment, not from the producer: every
        // producer passes `image: null` for it (there is no store cover), which is why the one container in the app
        // whose menu is reachable from four surfaces used to open with NO header art at all. 38 DIP is below the
        // treatment floor, so what actually paints is the flat 2x2 mosaic of the newest likes (or the stock PNG).
        Element? leading = LikedSongsArtwork.For(key, 38f, 38f, circular ? 19f : 6f)
            ?? (image is null ? null : Surfaces.Artwork(
                image, key.GetHashCode() & 0x7fffffff, 38f, 38f, circular ? 19f : 6f, decodePx: 76));
        return new ContextMenuHeader(leading, PlainHeaderText(title) ?? title, PlainHeaderText(subtitle));
    }

    /// <summary>Flatten a possibly-HTML header string: strip tags, THEN decode entities — the order
    /// <c>RichText</c> itself uses, and the order the flattener's own docs demand (decoding first would turn an escaped
    /// <c>&amp;lt;a&amp;gt;</c> into a live tag the stripper then eats). Decoding matters because the mappers
    /// <c>Esc()</c> the names they embed, so an un-decoded header would read "AC&amp;amp;DC".</summary>
    static string? PlainHeaderText(string? text)
        => SpotifyExportMapper.HtmlText(SpotifyExportMapper.ToPlainText(text));
}

/// <summary>
/// The sidebar pane's per-row LAYOUT verbs, split by where the menu grammar puts them. Only the renderer can compute
/// them (they depend on the row's position in a reorder band, in the pin list, or in the rootlist sibling run), so they
/// travel into <see cref="Menus.SidebarEntry"/> from <c>SidebarPaneSlot.NavExtras</c>.
///
/// <para>They used to be ONE flat list appended below every entity verb, which is how "Move up" came to render after
/// "Invite collaborators" on a playlist row. The split is the whole fix: <see cref="Organize"/> are positional verbs and
/// belong INSIDE the arm's Organize ▸ submenu next to Pin and "Move out of {parent}"; <see cref="Trailing"/> is the
/// document-level Remove, which is destructive-adjacent and stays at the bottom where it was. An arm with no Organize
/// submenu (album / artist / show / app route / feed track) flattens both, exactly as before.</para>
/// </summary>
public readonly record struct SidebarMenuExtras(
    IReadOnlyList<MenuFlyoutItem>? Organize = null,
    IReadOnlyList<MenuFlyoutItem>? Trailing = null)
{
    /// <summary>Nothing to contribute — a right-click on this row opens the entity menu alone (or nothing at all, when
    /// the row has no entity menu either).</summary>
    public bool IsEmpty => (Organize is null || Organize.Count == 0) && (Trailing is null || Trailing.Count == 0);

    /// <summary>Both groups as one flat list, in grammar order (positional verbs, then Remove) — what an arm without an
    /// Organize submenu appends, and what a layout-only menu IS. Returns the one non-empty group unwrapped, so the
    /// common single-group case costs no copy.</summary>
    public IReadOnlyList<MenuFlyoutItem>? Flat()
    {
        if (Trailing is not { Count: > 0 }) return Organize;
        if (Organize is not { Count: > 0 }) return Trailing;
        var all = new List<MenuFlyoutItem>(Organize.Count + Trailing.Count);
        for (int i = 0; i < Organize.Count; i++) all.Add(Organize[i]);
        for (int i = 0; i < Trailing.Count; i++) all.Add(Trailing[i]);
        return all;
    }
}
