using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// Container (album / artist / playlist / sidebar-playlist) action singletons. Owner-only playlist management reuses the
// existing flows verbatim: SettingsShared.Confirm for delete, LibraryBridge.UpdatePlaylistDetailsAsync(previousName:)
// for the undo-able rename, PlaylistInlineEdit.CopyContributorInviteAsync for invites.
public static class ContainerActions
{
    public static readonly AppAction PlayContext = new()
    {
        Id = ActionId.PlayContext, IconKey = ActionIcons.Play,
        Label = static c => Loc.Get(Strings.Detail.Play),
        IsEnabled = static c => c.S.Svc is not null && c.Target.Uri.Length > 0,
        Execute = static c =>
        {
            var p = c.S.Svc?.Player;
            if (p is null || c.Target.Uri.Length == 0) return;
            _ = p.PlayAsync(c.Target.Uri);
        },
    };

    /// <summary>Save album · follow artist · save playlist — one toggle over the saved-set (LibraryBridge).</summary>
    public static readonly AppAction SaveContext = new()
    {
        Id = ActionId.SaveContext, IconKey = ActionIcons.Save,
        IsChecked = static c => c.S.Library?.IsSaved(c.Target.Uri) ?? false,
        // Short strip-friendly verbs (Explorer labeled-strip columns are one word — the equal-width columns ellipsize
        // "Save to Your Library"). Album/playlist: saved-state-aware Save/Saved (matches TrackActions.ToggleLike);
        // artist: Follow/Following.
        Label = static c =>
        {
            bool saved = c.S.Library?.IsSaved(c.Target.Uri) ?? false;
            return c.Target.Kind == TargetKind.Artist
                ? Loc.Get(saved ? Strings.Artist.Following : Strings.Artist.Follow)
                : Loc.Get(saved ? Strings.Menu.Saved : Strings.Menu.Save);
        },
        IsEnabled = static c => c.S.Library is not null && c.Target.Uri.Length > 0,
        Execute = static c => c.S.Library?.ToggleSaved(c.Target.Uri, c.Target.Name),
    };

    // ── the container half of the TRANSPORT verbs (menu-grammar convergence, D48) ────────────────────────────────────
    /// <summary>Play next, for a CONTAINER (album / playlist / mix card). Same label, same queue table and same toast as
    /// <see cref="TrackActions.PlayNext"/> — the only difference is that the tracks are not in hand at menu-open time and
    /// resolve through <see cref="ContainerTracks"/> at invoke. Absent (not disabled) on a kind with no resolver.</summary>
    public static readonly AppAction PlayContextNext = new()
    {
        Id = ActionId.PlayContextNext, IconKey = ActionIcons.PlayNext,
        Label = static c => Loc.Get(Strings.Detail.PlayNext),
        IsEnabled = static c => c.S.Svc?.Player is not null && ContainerTracks.CanResolve(in c),
        Execute = static c => ContainerTracks.Queue(in c, next: true),
    };

    /// <summary>Play after (add to the end of the queue), for a CONTAINER. The counterpart of
    /// <see cref="TrackActions.AddToQueue"/>; see <see cref="PlayContextNext"/>.</summary>
    public static readonly AppAction AddContextToQueue = new()
    {
        Id = ActionId.AddContextToQueue, IconKey = ActionIcons.Queue,
        Label = static c => Loc.Get(Strings.Detail.PlayAfter),
        IsEnabled = static c => c.S.Svc?.Player is not null && ContainerTracks.CanResolve(in c),
        Execute = static c => ContainerTracks.Queue(in c, next: false),
    };

    /// <summary>An ALBUM card's "Go to artist". The card model carries a uri and a name and nothing else, so the artist
    /// is resolved at INVOKE time through the same album reader the track resolver uses (cold — one read per click,
    /// never per render), then navigated to with the app's own <c>artist:</c> route. The album's PRIMARY artist: an
    /// album menu has no place to host a cascade the way a multi-artist TRACK row does, and Spotify's own album menu
    /// navigates to the primary artist too.</summary>
    public static readonly AppAction GoToAlbumArtist = new()
    {
        Id = ActionId.GoToAlbumArtist, IconKey = ActionIcons.Artist,
        Label = static c => Loc.Get(Strings.Detail.GoToArtist),
        // Post is REQUIRED, not optional: this verb's entire effect (the navigation) happens after an await, so a host
        // without a UI-thread marshal could not complete it. Gating here renders the row disabled instead of dropping
        // the click silently — unlike the queue verbs, whose effect lands without Post and whose only marshalled step
        // is the confirmation toast.
        IsEnabled = static c => c.S.Go is not null && c.S.Svc is not null && c.S.Post is not null
                                && c.Target.Kind == TargetKind.Album && c.Target.Uri.Length > 0,
        Execute = static c =>
        {
            if (c.S.Go is not { } go || c.S.Svc is not { } svc || c.Target.Uri is not { Length: > 0 } uri) return;
            var post = c.S.Post;
            _ = Run();

            async Task Run()
            {
                Album? album;
                try { album = await svc.Library.GetAlbumAsync(uri).ConfigureAwait(false); }
                catch (Exception ex) { Post(post, () => Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error })); return; }

                if (album?.Artists is not { Count: > 0 } artists || artists[0].Uri.Length == 0)
                {
                    Post(post, () => Toast.Show(Loc.Get(Strings.Menu.ArtistUnavailable),
                        new ToastOptions { Severity = InfoBarSeverity.Warning }));
                    return;
                }
                var a = artists[0];
                Post(post, () => go("artist:" + a.Uri, a.Name));
            }
        },
    };

    /// <summary>Run a completion on the UI thread. A host without a marshal (the vertical slice, a test) simply does not
    /// get the completion — the <c>WaveeResourceDrop.DepositTracks</c> precedent — rather than touching signals from a
    /// pool thread.</summary>
    internal static void Post(Action<Action>? post, Action work) => post?.Invoke(work);

    /// <summary>Start an artist radio (Apple-Music-style): resolves the artist seed → a radio playlist, parks it after
    /// the current track (never interrupting playback), and raises the "Radio started → Open playlist" toast. Artist
    /// target with a player present.</summary>
    public static readonly AppAction GoToArtistRadio = new()
    {
        Id = ActionId.GoToArtistRadio, IconKey = ActionIcons.Radio,
        Label = static c => Loc.Get(Strings.Menu.GoToArtistRadio),
        IsEnabled = static c => c.S.Svc is not null && ActionRules.CanStartArtistRadio(in c.Target),
        Execute = static c => RadioLaunch.Start(c.S.Svc?.Player, c.Target.Uri, c.Target.Name, c.S.Go),
    };

    /// <summary>Open the target's detail page (the card/sidebar "Open" row).</summary>
    public static readonly AppAction OpenItem = new()
    {
        Id = ActionId.OpenItem, IconKey = ActionIcons.Open,
        Label = static c => Loc.Get(Strings.Menu.Open),
        IsEnabled = static c => c.S.Go is not null && ActionRules.RouteFor(in c.Target) is not null,
        Execute = static c =>
        {
            if (ActionRules.RouteFor(in c.Target) is { } route)
                c.S.Go?.Invoke(route, c.Target.Name);
        },
    };

    /// <summary>Rename (owner): a small ContentDialog with a name field → the undo-able
    /// <c>UpdatePlaylistDetailsAsync(previousName:)</c> rename.</summary>
    public static readonly AppAction RenamePlaylist = new()
    {
        Id = ActionId.RenamePlaylist, IconKey = ActionIcons.Rename,
        Label = static c => Loc.Get(Strings.Menu.RenamePlaylist),
        IsEnabled = static c => c.S.Library is not null && c.S.Overlay is not null
                                && c.Target.Host is { Caps.CanEditMetadata: true },
        Execute = static c =>
        {
            if (c.S.Library is not { } lib || c.S.Overlay is not { } overlay) return;
            string uri = c.Target.Uri;
            string current = c.Target.Name;
            RenameDialog(overlay, Loc.Get(Strings.Menu.RenamePlaylist), current, next =>
            {
                if (string.Equals(next, current, StringComparison.Ordinal)) return;
                _ = RunRename(lib, uri, next, current);
            });
        },
    };

    /// <summary>The shared single-field naming dialog: a titled <c>ContentDialog</c> over one <c>EditableText</c> seeded
    /// with <paramref name="current"/>. <paramref name="onCommit"/> receives the TRIMMED text and is never called with
    /// an empty string; deciding "unchanged" is the caller's, because a create legitimately commits the seed name.
    ///
    /// <para><b>Why a dialog and not an inline editor</b> — the question the sidebar's folder rename raises. Sidebar rows
    /// are recycled, bound <c>ItemsView</c> slots that re-plan on every rootlist push, and a rename lands a rootlist push
    /// by definition: an inline editor would lose focus mid-push, mid-word. The playlist rename one row away has always
    /// used this dialog, so the folder verbs joining it is also the consistent answer, not just the safe one.</para></summary>
    internal static void RenameDialog(IOverlayService overlay, string title, string current, Action<string> onCommit,
                                      string? primaryText = null)
    {
        var text = new Signal<string>(current);
        ContentDialog.Show(overlay, d =>
        {
            d.Title = title;
            d.PrimaryText = primaryText ?? Loc.Get(Strings.Menu.Rename);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.Content = new BoxEl
            {
                Direction = 1, MinWidth = 320f,
                Children = [Embed.Comp(() => new EditableText { Text = text, Width = 320f, Height = 32f })],
            };
            d.PrimaryClick = () =>
            {
                string next = text.Peek().Trim();
                if (next.Length == 0) return;
                onCommit(next);
            };
        });
    }

    static async Task RunRename(LibraryBridge lib, string uri, string next, string previous)
    {
        try { await lib.UpdatePlaylistDetailsAsync(uri, next, null, null, previousName: previous).ConfigureAwait(false); }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
    }

    /// <summary>Copy a contributor-invite link to the clipboard (owner, live Spotify session only) — reuses
    /// <see cref="PlaylistInlineEdit.CopyContributorInviteAsync"/> verbatim (a minimal DetailModel carries the uri;
    /// the web-url fallback inside resolves the share link).</summary>
    public static readonly AppAction InviteCollaborators = new()
    {
        Id = ActionId.InviteCollaborators, IconKey = ActionIcons.People,
        Label = static c => Loc.Get(Strings.Detail.Edit.InviteCollaborators),
        IsEnabled = static c => c.S.Library is not null
                                && c.Target.Host is { Caps.IsOwner: true }
                                && PlaylistInlineEdit.SpotifyEditsLive(c.S.Svc),
        Execute = static c =>
        {
            if (c.S.Library is not { } lib || c.Target.Uri.Length == 0) return;
            _ = RunInvite(lib, c.Target.Uri);
        },
    };

    static async Task RunInvite(LibraryBridge lib, string uri)
    {
        if (await PlaylistInlineEdit.CopyContributorInviteAsync(lib, DetailModel.Empty with { ContextUri = uri }).ConfigureAwait(false))
            Toast.Show(Loc.Get(Strings.Menu.LinkCopied), new ToastOptions { Severity = InfoBarSeverity.Success });
    }

    /// <summary>Delete playlist (owner) — destructive, behind the existing confirm dialog
    /// (<see cref="SettingsShared.Confirm"/>, the OwnerOverflowMenu precedent).</summary>
    public static readonly AppAction DeletePlaylist = new()
    {
        Id = ActionId.DeletePlaylist, IconKey = ActionIcons.Delete, Destructive = true,
        // Overlay required: Confirm(null overlay) would run the delete WITHOUT confirmation — never allow that path.
        IsEnabled = static c => c.S.Library is not null && c.S.Overlay is not null
                                && c.Target.Host is { Caps.IsOwner: true },
        Label = static c => Loc.Get(Strings.Detail.Edit.DeletePlaylist),
        Execute = static c =>
        {
            if (c.S.Library is not { } lib || c.S.Overlay is not { } overlay || c.Target.Uri.Length == 0) return;
            string uri = c.Target.Uri;
            SettingsShared.Confirm(overlay,
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                Loc.Get(Strings.Detail.Edit.DeletePlaylistConfirm),
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                () => _ = RunDelete(lib, uri));
        },
    };

    static async Task RunDelete(LibraryBridge lib, string uri)
    {
        try { await lib.DeletePlaylistAsync(uri).ConfigureAwait(false); }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
    }

    /// <summary>Set playlist visibility to an ABSOLUTE state (public/private). Explicit rows instead of a toggle: the
    /// sidebar summary carries no live IsPublic, and a mis-checked toggle would invert the user's intent.</summary>
    internal static void SetVisibility(ActionServices s, string uri, bool isPublic)
    {
        if (s.Library is not { } lib) return;
        _ = Run();
        async Task Run()
        {
            try { await lib.SetPlaylistVisibilityAsync(uri, isPublic).ConfigureAwait(false); }
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
        }
    }

    /// <summary>Turn collaboration on/off from a menu. A TOGGLE rather than the absolute pair visibility uses, because
    /// this one state IS carried by the store header the menu reads at open — so the row can show where it stands.
    /// The write rides <c>UpdateDetailsAsync(collaborative:)</c>, the same call the detail page's access flyout makes;
    /// the resulting store change is what re-skins every surface (no re-read here).</summary>
    internal static void SetCollaborative(ActionServices s, string uri, bool collaborative)
    {
        if (s.Library is not { } lib) return;
        _ = Run();
        async Task Run()
        {
            try { await lib.UpdatePlaylistDetailsAsync(uri, null, null, collaborative).ConfigureAwait(false); }
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
        }
    }
}

/// <summary>
/// The container → ordered-track-set seam behind the CARD menu's transport and collection verbs (Play next / Play after
/// / Add to playlist). It is deliberately the SAME resolver drag &amp; drop already deposits with
/// (<see cref="WaveeResourceDragPayload.ResolverFor"/>), so "drop this album on a playlist" and "Add to playlist" from
/// its menu can never disagree about what the album's tracks are — and no second reader path exists to drift.
///
/// <para>A PLAYLIST and an ALBUM resolve (the library reader). Every other kind resolves to NOTHING, by the locked
/// decisions recorded on that resolver: an ARTIST has no single obvious track set, and a SHOW/EPISODE is not a
/// <c>Track</c> at all. A menu therefore OMITS these rows for those kinds rather than offering a row that would have to
/// invent an answer — the same "never a promise we are not keeping" rule the sidebar folder arm follows.</para>
/// </summary>
static class ContainerTracks
{
    public static Func<CancellationToken, Task<IReadOnlyList<Track>>>? ResolverFor(in ActionContext c)
        => c.Target.Uri is { Length: > 0 } uri
            ? WaveeResourceDragPayload.ResolverFor(WaveeDragKindMap.OfUri(uri), uri, c.S.Svc)
            : null;

    /// <summary>Does this target have a track set at all? The gate every container track-set row is built behind.</summary>
    public static bool CanResolve(in ActionContext c) => ResolverFor(in c) is not null;

    /// <summary>Resolve, then hand the tracks to the SAME <see cref="DetailQueueActions"/> table the track verbs ride —
    /// including its batch cap and its "report what was issued" contract. Cold: one reader call per invoke.</summary>
    public static void Queue(in ActionContext c, bool next)
    {
        if (ResolverFor(in c) is not { } resolve || c.S.Svc?.Player is not { } player) return;
        var post = c.S.Post;
        _ = Run();

        async Task Run()
        {
            IReadOnlyList<Track> tracks;
            try { tracks = await resolve(default).ConfigureAwait(false); }
            catch (Exception ex)
            {
                ContainerActions.Post(post, () => Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }));
                return;
            }

            int n = next ? DetailQueueActions.PlayNext(player, tracks) : DetailQueueActions.AddToEnd(player, tracks);
            if (n > 0)
                ContainerActions.Post(post, () => Toast.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)),
                    new ToastOptions { Severity = InfoBarSeverity.Success }));
            else
                ContainerActions.Post(post, () => Toast.Show(Loc.Get(Strings.Drag.NothingToAdd),
                    new ToastOptions { Severity = InfoBarSeverity.Warning }));
        }
    }

    /// <summary>Add a container's tracks to a playlist — the menu equivalent of dragging the card onto that playlist,
    /// resolving through the same seam and landing on the same <c>AddTracksAsync</c> write (with the Add-to-playlist
    /// toast every other add raises, "Go to playlist" action included).</summary>
    public static void AddTo(ActionServices s, string targetUri, string targetName,
                             Func<CancellationToken, Task<IReadOnlyList<Track>>> resolve)
    {
        if (s.Library is not { } lib) return;
        var post = s.Post;
        var go = s.Go;
        _ = Run();

        async Task Run()
        {
            try
            {
                var tracks = await resolve(default).ConfigureAwait(false);
                if (tracks.Count == 0)
                {
                    ContainerActions.Post(post, () => Toast.Show(Loc.Get(Strings.Drag.NothingToAdd),
                        new ToastOptions { Severity = InfoBarSeverity.Warning }));
                    return;
                }
                await lib.AddTracksAsync(targetUri, tracks).ConfigureAwait(false);
                ContainerActions.Post(post, () => Toast.Show(Strings.Detail.AddedToPlaylist(targetName), new ToastOptions
                {
                    Severity = InfoBarSeverity.Success,
                    ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist),
                    OnAction = () => go?.Invoke("pl:" + targetUri, targetName),
                }));
            }
            catch (Exception ex)
            {
                ContainerActions.Post(post, () => PlaylistEditErrors.Toast(ex));
            }
        }
    }
}
