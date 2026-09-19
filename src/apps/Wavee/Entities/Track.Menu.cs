// ── Entities/Track.Menu.cs ─────────────────────────────────────────────────────────────────────────────────────────
// The named partial of Track.UI.cs: the track context-menu COMPOSITION (ch 01 §6.4) and the eighteen track VERBS
// registered into AppActions (the five Video ▸ ones in the named partial Track.Menu.Video.cs).
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: 2600 lines shared with Track.UI.cs (this partial is named on day one by the WP-4.5 contract §1)
// Spec: ch 01 §6.4, §9 ("Menu grammar order"); ch 29 §9.10 (the verbs live in the entity files)
//
// ── THE GRAMMAR, RESTATED BECAUSE THIS FILE IS WHERE IT IS SPELLED FOR TRACKS ────────────────────────────────────────
//
//   header (38 art · title · "artists · album", or "{n} songs selected" / "{first}  +{n-1} more")
//   transport strip  Play · Play next · Play after · Save            (a 240-DIP pane: the same four as plain rows)
//   collection       Add to playlist ▸ · Move to playlist ▸ (an editable host only)
//   navigation       Go to album (Go to podcast for an episode) · Go to artist / Go to artists ▸   — single target
//   Share ▸          Copy link(s) · [Copy Spotify URI · Open in Spotify Web]                      — the extras single
//   track extras     View credits · Go to song radio · Video ▸                                    — single target
//   surface extras   Move up · Move down · Track details
//   destructive      Remove from this playlist · Remove from queue                               — behind a separator
//
// Built at OPEN time (inside a `ContextMenu.Attach` factory), so it allocates freely and reads every seam once. A verb
// whose seam is not installed renders DISABLED (a registered row) or is ABSENT (a submenu with nothing behind it) —
// never a live row that does nothing.

using System.Globalization;
using System.Text;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Input;
using FluentGpu.Localization;

namespace Wavee;

public readonly partial struct Track
{
    // ══ 1. OPTIONS AND SEAMS ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a surface varies about the track menu.
    /// <para><b>Construct with a named argument</b> — <c>default</c> / <c>new()</c> zero-initializes a record struct and
    /// reads <see cref="ShowGoToAlbum"/> false.</para>
    /// <para><see cref="Host"/> is the playlist the rows sit in (default elsewhere); <see cref="TrackDetails"/> is the
    /// ultra-compact tier's drawer fallback — pass it only when the chevron lane is OFF
    /// (<c>TableRules.ShowVersionsMenuItem</c>), one action must not have two visible controls; <see cref="QueueItemId"/>
    /// non-zero makes a single target a queue entry; <see cref="CompactRows"/> is the 240-DIP pane's rows-only shape;
    /// <see cref="PickerOverlay"/> is where "More playlists…" opens (absent ⇒ that row is disabled).</para></summary>
    public readonly record struct MenuOptions(PlaylistHost Host = default, bool ShowGoToAlbum = true,
        Action? TrackDetails = null, Action? MoveUp = null, Action? MoveDown = null, long QueueItemId = 0,
        bool CompactRows = false /* a 240-DIP pane: the transport verbs as rows instead of the strip */,
        IOverlayService? PickerOverlay = null);

    static readonly MenuOptions s_defaultMenuOptions = new(ShowGoToAlbum: true);

    /// <summary>The writes the track verbs need that <see cref="ActionServices"/> does not carry. Each is ONE mutation
    /// that awaits the server, toasts (with Undo where the grammar offers it) and maps its own failure — the menu only
    /// calls it. Installed by the owning page/shell file (playlist writes: owner O; the queue: owner Q; the credits
    /// modal: <c>Track.Drawer.cs</c>). Null ⇒ the verb renders disabled or its row is absent.</summary>
    public static class MenuSeams
    {
        /// <summary>Remove these tracks' membership rows (<c>host.Rows</c>, original indices in display order).</summary>
        public static Action<PlaylistHost, IReadOnlyList<Track>>? RemoveRows { get; set; }

        /// <summary>Move = add to <c>target</c>, THEN remove from <c>host</c> (Spotify has no cross-playlist move; a failed
        /// add must leave the source untouched). A target whose <c>Uri</c> is default means "a new playlist".</summary>
        public static Action<PlaylistHost, Actions.Menu.DepositTarget, IReadOnlyList<Track>>? MoveRows { get; set; }

        /// <summary>Remove one queue row by its server item id.</summary>
        public static Action<long>? RemoveFromQueue { get; set; }

        /// <summary>Open the "View credits" modal for one track.</summary>
        public static Action<Track>? ViewCredits { get; set; }
    }

    // ══ 2. THE MENUS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The track menu for an explicit target set — the eager rows, the rails, the now-playing cluster. Null when
    /// there is nothing to act on.</summary>
    public static ContextMenuModel? Menu(IReadOnlyList<Track> targets, in MenuOptions o)
    {
        EnsureActions();
        if (targets is not { Count: > 0 } || targets[0].Slot <= 0) return null;
        var host = o.Host.Rows is null ? PlaylistHost.None : o.Host;
        var target = o.QueueItemId != 0 && targets.Count == 1
            ? ActionTarget.ForQueueEntry(targets[0], o.QueueItemId)
            : ActionTarget.ForTracks(targets, host);
        return Compose(new ActionContext(target, Actions.Services), in o);
    }

    /// <summary>The selection-aware menu for a row of a selection-backed list (ch 01 W20): right-click INSIDE a ≥2
    /// selection acts on all of it, outside it collapses the selection to the clicked row first — <c>TargetResolver</c>,
    /// run AT OPEN inside the factory.
    /// <para><paramref name="trackAt"/> maps an ITEM index to its track (<c>default</c> for a non-track row).
    /// <paramref name="trackStart"/> maps an item index to its ORIGINAL membership index (&lt; 0 = not a membership row):
    /// with a playlist <see cref="MenuOptions.Host"/> the host's rows are rebuilt from the SETTLED selection, so a remove
    /// or a move names exactly the target set (the 0.2.9 "host thunk runs after the selection settles" rule).</para></summary>
    public static ContextMenuModel? RowMenu(SelectionModel selection, int clickedIndex, Func<int, Track> trackAt,
                                            Func<int, int> trackStart, in MenuOptions o)
    {
        EnsureActions();
        var baseHost = o.Host;
        var sel = selection;
        var at = trackAt;
        var membershipOf = trackStart;
        Func<PlaylistHost>? host = baseHost.IsSome && baseHost.Rows is not null
            ? () => SettledHost(sel, at, membershipOf, baseHost)
            : null;
        if (TargetResolver.Resolve(selection, trackAt, clickedIndex, host) is not { } target) return null;
        return Compose(new ActionContext(target, Actions.Services), in o);
    }

    /// <summary>The host's rows for the selection as it stands AFTER the resolver settled it: every selected track row,
    /// through the caller's membership map.</summary>
    static PlaylistHost SettledHost(SelectionModel selection, Func<int, Track> trackAt, Func<int, int> membershipOf,
                                    PlaylistHost host)
    {
        var rows = new List<int>(Math.Max(1, selection.SelectedCount));
        for (int r = 0; r < selection.RangeCount; r++)
        {
            var (start, end) = selection.GetRange(r);
            for (int i = start; i <= end; i++)
            {
                if (trackAt(i).Slot <= 0) continue;
                int membership = membershipOf(i);
                if (membership >= 0) rows.Add(membership);
            }
        }
        return host with { Rows = rows };
    }

    static readonly ActionId[] s_transportVerbs = [ActionId.Play, ActionId.PlayNext, ActionId.AddToQueue, ActionId.ToggleLike];

    /// <summary>The grammar, in order (file header).</summary>
    static ContextMenuModel Compose(ActionContext ctx, in MenuOptions o)
    {
        var rows = new List<MenuFlyoutItem>(16);
        AppBarCommand[] strip;
        if (o.CompactRows)
        {
            // An Explorer-style labelled strip does not fit a 240-DIP pane, but the core verbs must still be there.
            strip = [];
            Actions.Menu.AddRows(rows, in ctx, s_transportVerbs);
            Actions.Menu.OpenGroup(rows);
        }
        else strip = Actions.Menu.Strip(in ctx, s_transportVerbs);

        // collection
        rows.Add(AddToPlaylistItem(in ctx, o.PickerOverlay));
        if (MoveToPlaylistItem(in ctx, o.PickerOverlay) is { } move) rows.Add(move);

        // navigation — a multi-selection has no single album or artist to go to
        if (ctx.Target.Single is { } single)
        {
            // The album page passes ShowGoToAlbum false, which suppresses BOTH container rows (ch 01 §6.4 row 3b).
            if (o.ShowGoToAlbum)
            {
                if (single.IsPodcast)
                {
                    if (GoToPodcastItem(single, ctx.S) is { } podcast) rows.Add(podcast);
                }
                else if (ActionRules.CanGoToAlbum(ctx.Target) && Actions.Menu.Row(ActionId.GoToAlbum, in ctx) is { } album)
                    rows.Add(album);
            }
            if (ArtistNavItem(single, in ctx) is { } artists) rows.Add(artists);
        }

        if (Actions.Menu.Share(in ctx) is { } share) rows.Add(share);

        // the track kind's own extras
        if (ctx.Target.Single is { } one)
        {
            if (ActionRules.CanViewCredits(ctx.Target) && Actions.Menu.Row(ActionId.ViewCredits, in ctx) is { } credits)
                rows.Add(credits);
            if (ActionRules.CanStartTrackRadio(ctx.Target) && Actions.Menu.Row(ActionId.GoToSongRadio, in ctx) is { } radio)
                rows.Add(radio);
            if (VideoItem(one, in ctx) is { } video) rows.Add(video);
        }

        // the surface's extras
        bool details = o.TrackDetails is not null && ctx.Target.Count == 1;
        if (o.MoveUp is not null || o.MoveDown is not null || details)
        {
            Actions.Menu.OpenGroup(rows);
            Actions.Menu.AddMoveRows(rows, o.MoveUp, o.MoveDown);
            if (details)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.TrackFacts.ShowDetails), Icons.List, true, o.TrackDetails));
        }

        // destructive, last, behind its own separator — present exactly when the REGISTERED verb says it can run (the
        // queue owner registers its own RemoveFromQueue first; whichever verb holds the id decides)
        bool removeRow = AppActions.Find(ActionId.RemoveFromThisPlaylist) is { } removeVerb && removeVerb.EnabledFor(in ctx);
        bool removeQueue = ctx.Target.Kind == TargetKind.QueueEntry
                           && AppActions.Find(ActionId.RemoveFromQueue) is { } dequeueVerb && dequeueVerb.EnabledFor(in ctx);
        if (removeRow || removeQueue)
        {
            Actions.Menu.OpenGroup(rows);
            if (removeRow && Actions.Menu.Row(ActionId.RemoveFromThisPlaylist, in ctx) is { } remove) rows.Add(remove);
            if (removeQueue && Actions.Menu.Row(ActionId.RemoveFromQueue, in ctx) is { } dequeue) rows.Add(dequeue);
        }

        return new ContextMenuModel(strip, rows, HeaderFor(ctx.Target.Tracks));
    }

    // ── the rows ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Add to playlist ▸ — the ONE deposit submenu shape (<c>Actions.Menu.Deposit</c>): New playlist · the
    /// editable playlists · More playlists…. The writes are the library seam's (<c>Sidebar.LibraryWrites</c>: deposit,
    /// and create-with); the submenu is DISABLED, not absent, while either is missing (ch 01 §6.4).</summary>
    static MenuFlyoutItem AddToPlaylistItem(in ActionContext ctx, IOverlayService? overlay)
    {
        var writes = Sidebar.LibraryWrites;
        var tracks = Snapshot(ctx.Target.Tracks);
        bool canAdd = tracks.Length > 0 && writes is not null && writes.DepositTracks is not null
                      && writes.CreatePlaylistWith is not null;
        var arts = new List<string?>();
        var ordered = DepositTargets(default, arts);
        var payload = PayloadOf(tracks);
        string title = Loc.Get(Strings.Detail.AddToPlaylist);
        Action<Actions.Menu.DepositTarget> deposit = target =>
        {
            if (Sidebar.LibraryWrites?.DepositTracks is { } write) write(target.Uri.Text, target.Name, payload);
        };
        Action create = () =>
        {
            if (Sidebar.LibraryWrites?.CreatePlaylistWith is { } make) make(payload);
        };
        return Actions.Menu.Deposit(title, ActionIcons.Resolve(ActionIcons.Add), canAdd, ordered, deposit, create,
                                    MorePlaylists(overlay, title, ordered, arts, deposit, create, canAdd));
    }

    /// <summary>Move to playlist ▸ — offered only where there is a source to move OUT of (an editable host with resolved
    /// rows) and a move seam; elsewhere "move" and "add" would be the same verb twice. The source is excluded.</summary>
    static MenuFlyoutItem? MoveToPlaylistItem(in ActionContext ctx, IOverlayService? overlay)
    {
        var host = ctx.Target.Host;
        if (MenuSeams.MoveRows is null || ctx.Target.Count == 0 || !ActionRules.CanRemoveFromPlaylist(host)) return null;
        var tracks = Snapshot(ctx.Target.Tracks);
        var arts = new List<string?>();
        var ordered = DepositTargets(host.Playlist, arts);
        string title = Loc.Get(Strings.Menu.MoveToPlaylist);
        Action<Actions.Menu.DepositTarget> deposit = target => MenuSeams.MoveRows?.Invoke(host, target, tracks);
        Action create = () => MenuSeams.MoveRows?.Invoke(host, default, tracks);
        return Actions.Menu.Deposit(title, Icons.Forward, canAdd: true, ordered, deposit, create,
                                    MorePlaylists(overlay, title, ordered, arts, deposit, create, canAdd: true));
    }

    const string PickerNewKey = "new";

    /// <summary>"More playlists…" — the full searchable destination picker over the SAME ordering, in a centred dialog
    /// (the menu is gone by invoke time, so there is no anchor for a flyout). Null — the row disabled — without an overlay
    /// host or while nothing can be added.</summary>
    static Action? MorePlaylists(IOverlayService? overlay, string title, List<Actions.Menu.DepositTarget> ordered,
                                 List<string?> arts, Action<Actions.Menu.DepositTarget> deposit, Action create, bool canAdd)
    {
        if (!canAdd || Controls.IsNullOverlay(overlay)) return null;
        IOverlayService svc = overlay;
        return () =>
        {
            var items = new Actions.PickerItem[ordered.Count + 1];
            items[0] = new Actions.PickerItem(PickerNewKey, Loc.Get(Strings.Detail.NewPlaylist), Glyph: Icons.Add,
                                              Pinned: true, Plated: true);
            for (int i = 0; i < ordered.Count; i++)
                items[i + 1] = new Actions.PickerItem(i.ToString(CultureInfo.InvariantCulture), ordered[i].Name,
                                                      ArtUrl: i < arts.Count ? arts[i] : null);
            Actions.OpenPicker(svc, new Actions.PickerSpec(title, items, pick =>
            {
                if (pick.Key == PickerNewKey) { create(); return; }
                if (int.TryParse(pick.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && (uint)index < (uint)ordered.Count)
                    deposit(ordered[index]);
            })
            {
                Placeholder = Loc.Get(Strings.Detail.FindPlaylist),
                EmptyText = Loc.Get(Strings.Detail.NoPlaylists),
                PanelPadding = 8f,
            });
        };
    }

    /// <summary>The playlists a deposit can land in, in <c>PlaylistDepositTargets.Order</c> (MRU first, then rootlist
    /// order — ch 01 item 42), the one ordering the picker shares. <paramref name="arts"/> receives each cover url, in step.</summary>
    static List<Actions.Menu.DepositTarget> DepositTargets(EntityUri exclude, List<string?> arts)
        => Playlist.DepositTargetsFor(exclude, arts);

    /// <summary>"Go to podcast" — the container row an EPISODE row gets instead of "Go to album": it reads the show out
    /// of the album slot through the SHOW table (the album table's cell at that slot is some other row).</summary>
    static MenuFlyoutItem? GoToPodcastItem(Track t, ActionServices s)
    {
        int slot = t.AlbumSlot;
        if (slot <= 0) return null;
        var show = new Show(slot);
        if (!show.IsValid || !show.Uri.IsValid) return null;
        var route = Shell.For(show.Uri, show.Title);
        var go = s.Go;
        return new MenuFlyoutItem(Loc.Get(Strings.Menu.GoToPodcast), Icons.RadioTower, go is not null, () => go?.Invoke(route));
    }

    /// <summary>Go to artist / Go to artists ▸ over the NAVIGABLE artists (those with a uri — a name-only artist has
    /// nowhere to go). Exactly one: the registered verb when it is the primary, else a row carrying that artist (the verb
    /// always navigates to the primary). Two or more: one plain row per artist, in billing order.</summary>
    static MenuFlyoutItem? ArtistNavItem(Track t, in ActionContext ctx)
    {
        var slots = t.ArtistSlots;
        int navigable = 0;
        for (int i = 0; i < slots.Length; i++)
            if (new Artist(slots[i]).Uri.IsValid) navigable++;
        if (navigable == 0) return null;
        var go = ctx.S.Go;
        if (navigable == 1)
        {
            if (ActionRules.CanGoToArtist(ctx.Target) && Actions.Menu.Row(ActionId.GoToArtist, in ctx) is { } row) return row;
            for (int i = 0; i < slots.Length; i++)
            {
                var a = new Artist(slots[i]);
                if (a.Uri.IsValid) return Actions.Menu.GoToArtist(new Actions.Menu.ArtistLink(a.Name, Shell.For(a.Uri, a.Name)), go);
            }
        }
        var links = new Actions.Menu.ArtistLink[navigable];
        int n = 0;
        for (int i = 0; i < slots.Length && n < links.Length; i++)
        {
            var a = new Artist(slots[i]);
            if (a.Uri.IsValid) links[n++] = new Actions.Menu.ArtistLink(a.Name, Shell.For(a.Uri, a.Name));
        }
        return Actions.Menu.GoToArtists(links, go);
    }

    /// <summary>Video ▸ — the user's local video override for ONE playable. Which rows exist is
    /// <c>Video.OverrideUx.MenuFor</c>, which walks the SAME tier decision playback takes; each row is the registered
    /// <c>ActionId.*Video</c> verb (absent until its owner registers it), Remove behind a separator. Null — the row
    /// absent — with no curation roster or no row to show.</summary>
    static MenuFlyoutItem? VideoItem(Track t, in ActionContext ctx)
    {
        if (!Video.Overrides.Present || !t.Uri.IsValid) return null;
        string uri = t.Uri.Text;
        var which = Video.OverrideUx.MenuFor(true, uri, Video.Overrides.Present, Video.Overrides.Has(uri),
                                              Video.Overrides.Decide(uri).Tier);
        if (which == Video.MenuItems.None) return null;
        var items = new List<MenuFlyoutItem>(6);
        if ((which & Video.MenuItems.Attach) != 0 && Actions.Menu.Row(ActionId.AttachVideo, in ctx) is { } attach) items.Add(attach);
        if ((which & Video.MenuItems.Replace) != 0 && Actions.Menu.Row(ActionId.ReplaceVideo, in ctx) is { } replace) items.Add(replace);
        if ((which & Video.MenuItems.Locate) != 0 && Actions.Menu.Row(ActionId.LocateVideo, in ctx) is { } locate) items.Add(locate);
        if ((which & Video.MenuItems.ShowInExplorer) != 0 && Actions.Menu.Row(ActionId.ShowVideoInExplorer, in ctx) is { } reveal)
            items.Add(reveal);
        if ((which & Video.MenuItems.Remove) != 0 && Actions.Menu.Row(ActionId.RemoveVideo, in ctx) is { } remove)
        {
            if (items.Count > 0) items.Add(Actions.Menu.Separator);
            items.Add(remove);
        }
        return items.Count == 0 ? null : Actions.Menu.SubMenu(Loc.Get(Strings.VideoOverride.MenuTitle), items, ActionIcons.Video);
    }

    /// <summary>The identity strip: 38 art · title · "artists · album" for one track; "{n} songs selected" over
    /// "{first}  +{n-1} more" for a selection. Plain text by construction — every name here is a column, never markup.</summary>
    static ContextMenuHeader HeaderFor(IReadOnlyList<Track> tracks)
    {
        var first = tracks[0];
        string? art = Controls.ArtUrl(first.ImageId);
        if (tracks.Count > 1) return Actions.Menu.SelectionHeader(art, tracks.Count, first.Title);
        return Actions.Menu.Header(art, first.Title, SubtitleOf(first));
    }

    static string? SubtitleOf(Track t)
    {
        var sb = new StringBuilder(64);
        var slots = t.ArtistSlots;
        for (int i = 0; i < slots.Length; i++)
        {
            string name = new Artist(slots[i]).Name;
            if (name.Length == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name);
        }
        var (_, container) = ContainerOf(t);
        if (container.Length > 0 && sb.Length > 0) sb.Append(" · ").Append(container);
        return sb.Length == 0 ? null : sb.ToString();
    }

    static Track[] Snapshot(IReadOnlyList<Track> tracks)
    {
        int n = 0;
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Slot > 0) n++;
        var arr = new Track[n];
        int k = 0;
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Slot > 0) arr[k++] = tracks[i];
        return arr;
    }

    /// <summary>A deposit payload for a track set in hand — the same envelope a row drag carries, so a menu deposit and
    /// a drop reach the library seam through ONE shape.</summary>
    static DragPayload PayloadOf(Track[] tracks)
    {
        if (tracks.Length == 0) return new DragPayload(DragKind.Track, "", "", "", Tracks: tracks);
        var first = tracks[0];
        string uri = first.Uri.Text;
        return new DragPayload(DragKind.Track, uri, uri, first.Title, new EntityRef(EntityKind.Track, first.Slot), Tracks: tracks);
    }

    // ══ 3. PLAY ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Play a track set from a menu (0.2.9 <c>TrackActions.Play</c>): the first row through the playback host's
    /// ONE play path (<c>Playback.PlayContext</c> — a playable plays as a one-row queue at once), the rest queued behind
    /// it through the queue owner's <c>ActionServices.AddToQueue</c> seam. Never a second context load. The table's own
    /// "play from here" is its profile's <c>PlayFrom</c>; this is the menu's and the art card's.</summary>
    static void PlayTracks(IReadOnlyList<Track> tracks)
    {
        int first = -1;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Slot > 0) { first = i; break; }
        if (first < 0) return;
        Playback.PlayContext(tracks[first].Id);
        if (Actions.Services.AddToQueue is not { } enqueue) return;
        for (int i = first + 1; i < tracks.Count; i++)
            if (tracks[i].Slot > 0) enqueue(tracks[i].Uri);
    }

    // ══ 4. THE EIGHTEEN VERBS ════════════════════════════════════════════════════════════════════════════════════════

    static bool s_actionsInstalled;

    /// <summary>Make sure the verbs are registered before a menu reads them — the first menu registers them if the
    /// composition root has not.</summary>
    public static void EnsureActions()
    {
        if (!s_actionsInstalled) InstallActions();
    }

    /// <summary>Register the eighteen track verbs into <see cref="AppActions"/> — Play · PlayNext · AddToQueue ·
    /// ToggleLike · CopyLink · GoToAlbum · GoToArtist · GoToSongRadio · ViewCredits · CopySpotifyUri · OpenInSpotifyWeb ·
    /// RemoveFromThisPlaylist · RemoveFromQueue, plus the five Video ▸ verbs of <c>Track.Menu.Video.cs</c> (AttachVideo ·
    /// ReplaceVideo · LocateVideo · ShowVideoInExplorer · RemoveVideo). Idempotent (and <c>AppActions.Register</c> is
    /// first-wins per id). Each
    /// verb runs through a seam — <c>Actions.Services</c>, <c>Playback</c>/<c>Queue</c>, <c>User.Me</c>,
    /// <see cref="MenuSeams"/> — and is DISABLED while that seam is absent, never a silent no-op. Every IsEnabled/Label is
    /// a one-shot snapshot at menu open (a menu closes on invoke).</summary>
    public static void InstallActions()
    {
        if (s_actionsInstalled) return;
        s_actionsInstalled = true;

        // ── transport ────────────────────────────────────────────────────────────────────────────────────────────────
        AppActions.Register(new AppAction
        {
            Id = ActionId.Play, IconKey = ActionIcons.Play,
            Label = static _ => Loc.Get(Strings.Detail.Play),
            IsEnabled = static c => c.Target.Count > 0 && Entities.Current is not null,
            Execute = static c => PlayTracks(c.Target.Tracks),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.PlayNext, IconKey = ActionIcons.PlayNext,
            Label = static c => c.Target.Count > 1 ? Strings.Menu.PlayNextN(c.Target.Count) : Loc.Get(Strings.Detail.PlayNext),
            IsEnabled = static c => c.S.PlayNext is not null && c.Target.Count > 0,
            Execute = static c =>
            {
                if (c.S.PlayNext is not { } playNext) return;
                var tracks = c.Target.Tracks;
                int n = 0;
                // Each call inserts directly after the playing row, so walking the set BACKWARDS keeps its order.
                for (int i = tracks.Count - 1; i >= 0; i--)
                {
                    var uri = tracks[i].Uri;
                    if (!uri.IsValid) continue;
                    playNext(uri);
                    n++;
                }
                if (n > 0) Notify.Say(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), InfoBarSeverity.Success);
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.AddToQueue, IconKey = ActionIcons.Queue,
            // Language parity with the detail rail's "Play after" (same verb, same custom icon, app-wide).
            Label = static c => c.Target.Count > 1 ? Strings.Menu.AddToQueueN(c.Target.Count) : Loc.Get(Strings.Detail.PlayAfter),
            IsEnabled = static c => c.S.AddToQueue is not null && c.Target.Count > 0,
            Execute = static c =>
            {
                if (c.S.AddToQueue is not { } enqueue) return;
                var tracks = c.Target.Tracks;
                int n = 0;
                for (int i = 0; i < tracks.Count; i++)
                {
                    var uri = tracks[i].Uri;
                    if (!uri.IsValid) continue;
                    enqueue(uri);
                    n++;
                }
                if (n > 0) Notify.Say(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), InfoBarSeverity.Success);
            },
        });

        // ── state ────────────────────────────────────────────────────────────────────────────────────────────────────
        AppActions.Register(new AppAction
        {
            Id = ActionId.ToggleLike, IconKey = ActionIcons.Like,
            // The strip's one-word form ("Save"/"Saved"): equal-width strip columns would ellipsize "Save to Liked Songs".
            Label = static c => AllLiked(c.Target.Tracks) ? Loc.Get(Strings.Menu.Saved) : Loc.Get(Strings.Menu.Save),
            // Multi: checked iff ALL are saved; running it then saves the rest, or unsaves all when all were saved.
            IsChecked = static c => AllLiked(c.Target.Tracks),
            IsEnabled = static c => c.Target.Count > 0 && User.Me.Slot > 0,
            Execute = static c =>
            {
                var me = User.Me;
                if (me.Slot <= 0) return;
                var tracks = c.Target.Tracks;
                bool save = !AllLiked(tracks);
                for (int i = 0; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    if (t.Slot <= 0) continue;
                    bool liked = me.Likes(t);
                    if (save && !liked) me.Like(t);
                    else if (!save && liked) me.Unlike(t);
                }
            },
        });

        // ── share ────────────────────────────────────────────────────────────────────────────────────────────────────
        AppActions.Register(new AppAction
        {
            Id = ActionId.CopyLink, IconKey = ActionIcons.Link,
            Label = static c => c.Target.Count > 1 ? Strings.Menu.CopyLinks(c.Target.Count) : Loc.Get(Strings.Menu.CopyLink),
            IsEnabled = static c => c.S.Clipboard is not null && LinkTextOf(c.Target).Length > 0,
            Execute = static c => Copy(c.S, LinkTextOf(c.Target), Strings.Menu.LinkCopied),
        });

        // ── navigation ───────────────────────────────────────────────────────────────────────────────────────────────
        AppActions.Register(new AppAction
        {
            Id = ActionId.GoToAlbum, IconKey = ActionIcons.Album,
            Label = static _ => Loc.Get(Strings.Menu.GoToAlbum),
            IsEnabled = static c => c.S.Go is not null && ActionRules.CanGoToAlbum(c.Target),
            Execute = static c =>
            {
                if (c.Target.Single is not { } t || c.S.Go is not { } go) return;
                var album = t.Album;
                if (album.IsValid && album.Uri.IsValid) go(Shell.For(album.Uri, album.Title));
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.GoToArtist, IconKey = ActionIcons.Artist,
            Label = static _ => Loc.Get(Strings.Detail.GoToArtist),
            // A primary artist URI is REQUIRED, not merely an artist entry: a name-only primary would navigate to an
            // empty route — a dead page.
            IsEnabled = static c => c.S.Go is not null && ActionRules.CanGoToArtist(c.Target),
            Execute = static c =>
            {
                if (c.Target.Single is not { } t || c.S.Go is not { } go) return;
                var slots = t.ArtistSlots;
                if (slots.Length == 0) return;
                var primary = new Artist(slots[0]);
                if (primary.IsValid && primary.Uri.IsValid) go(Shell.For(primary.Uri, primary.Name));
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.GoToSongRadio, IconKey = ActionIcons.Radio,
            Label = static _ => Loc.Get(Strings.Menu.GoToSongRadio),
            // The radio seam (`Playback.StartRadio`, G-251) resolves the seed's radio playlist, plays it now or parks it
            // behind the current track, and reports the outcome; `Queue.RadioToast` raises "Radio started → Open playlist"
            // or "Couldn't start radio" off that report, not off this click.
            IsEnabled = static c => c.S.StartRadio is not null && ActionRules.CanStartTrackRadio(c.Target),
            Execute = static c =>
            {
                if (c.Target.Single is { } t && c.S.StartRadio is { } start) start(t.Uri);
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.ViewCredits, IconKey = ActionIcons.Credits,
            Label = static _ => Loc.Get(Strings.Menu.ViewCredits),
            IsEnabled = static c => MenuSeams.ViewCredits is not null && ActionRules.CanViewCredits(c.Target),
            Execute = static c =>
            {
                if (c.Target.Single is { } t && MenuSeams.ViewCredits is { } open) open(t);
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.CopySpotifyUri, IconKey = ActionIcons.CopyUri,
            Label = static _ => Loc.Get(Strings.Menu.CopySpotifyUri),
            IsEnabled = static c => c.S.Clipboard is not null && Actions.MenuRules.SingleShareableUri(c.Target).IsValid,
            Execute = static c =>
            {
                var uri = Actions.MenuRules.SingleShareableUri(c.Target);
                if (uri.IsValid) Copy(c.S, uri.Text, Strings.Menu.UriCopied);
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.OpenInSpotifyWeb, IconKey = ActionIcons.OpenWeb,
            Label = static _ => Loc.Get(Strings.Menu.OpenInSpotifyWeb),
            IsEnabled = static c => c.S.OpenExternal is not null
                                    && Actions.WebLinkOf(Actions.MenuRules.SingleShareableUri(c.Target)).Length > 0,
            Execute = static c =>
            {
                string url = Actions.WebLinkOf(Actions.MenuRules.SingleShareableUri(c.Target));
                if (url.Length > 0) c.S.OpenExternal?.Invoke(url);
            },
        });

        // ── the Video ▸ submenu (Track.Menu.Video.cs) ────────────────────────────────────────────────────────────────
        // Without this call `VideoItem`'s `Actions.Menu.Row(ActionId.*Video, …)` finds nothing and the whole submenu is
        // dropped — the rows and their verbs ship together or not at all.
        InstallVideoActions();

        // ── destructive ──────────────────────────────────────────────────────────────────────────────────────────────
        AppActions.Register(new AppAction
        {
            Id = ActionId.RemoveFromThisPlaylist, IconKey = ActionIcons.Remove, Destructive = true,
            Label = static _ => Loc.Get(Strings.Menu.RemoveFromThisPlaylist),
            IsEnabled = static c => MenuSeams.RemoveRows is not null && ActionRules.CanRemoveFromPlaylist(c.Target.Host),
            Execute = static c =>
            {
                if (MenuSeams.RemoveRows is not { } remove || !ActionRules.CanRemoveFromPlaylist(c.Target.Host)) return;
                remove(c.Target.Host, c.Target.Tracks);
            },
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.RemoveFromQueue, IconKey = ActionIcons.Remove, Destructive = true,
            Label = static _ => Loc.Get(Strings.Menu.RemoveFromQueue),
            IsEnabled = static c => MenuSeams.RemoveFromQueue is not null && c.Target.Kind == TargetKind.QueueEntry
                                    && c.Target.QueueItemId != 0,
            Execute = static c =>
            {
                if (MenuSeams.RemoveFromQueue is { } remove && c.Target.QueueItemId != 0) remove(c.Target.QueueItemId);
            },
        });
    }

    /// <summary>Checked iff EVERY track (≥ 1) is in the account's Liked edge — the toggle's checked state and its
    /// direction.</summary>
    static bool AllLiked(IReadOnlyList<Track> tracks)
    {
        if (tracks is not { Count: > 0 }) return false;
        var me = User.Me;
        if (me.Slot <= 0) return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.Slot <= 0 || !me.Likes(t)) return false;
        }
        return true;
    }

    /// <summary>The web links of every shareable track in the target, newline-joined ("" when none).</summary>
    static string LinkTextOf(ActionTarget target)
    {
        var tracks = target.Tracks;
        if (tracks is not { Count: > 0 }) return "";
        StringBuilder? sb = null;
        string? single = null;
        for (int i = 0; i < tracks.Count; i++)
        {
            var uri = tracks[i].Uri;
            if (!Actions.MenuRules.IsShareable(uri)) continue;
            string link = Actions.WebLinkOf(uri);
            if (link.Length == 0) continue;
            if (single is null) { single = link; continue; }
            sb ??= new StringBuilder(single);
            sb.Append('\n').Append(link);
        }
        return sb?.ToString() ?? single ?? "";
    }

    /// <summary>Put text on the clipboard, announce "Copied", toast. A clipboard failure toasts rather than silently doing
    /// nothing (ch 01 §6.4).</summary>
    static void Copy(ActionServices s, string text, string toastKey)
    {
        if (s.Clipboard is not { } clip || text.Length == 0) return;
        try { clip(text); }
        catch
        {
            Notify.Say(Loc.Get(Strings.Common.ErrorTitle), InfoBarSeverity.Error);
            return;
        }
        if (Announcer.IsAvailable) Announcer.Say(Loc.Get(Strings.Auth.Copied));
        Notify.Say(Loc.Get(toastKey), InfoBarSeverity.Success);
    }
}
