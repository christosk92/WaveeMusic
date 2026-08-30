using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The "Copy to playlist" / "Add to playlist" affordance: an anchored, light-dismissable flyout with a search box +
// cover thumbnails, listing the user's EDITABLE Spotify playlists (owned + collaborator) plus a "New playlist" entry that
// creates a REAL playlist over the wire. Modelled on WaveeSidebar.SidebarCreateButton (the anchored Overlay.Service flyout).

/// <summary>Shared launcher for the anchored playlist-picker flyout: opens (or toggles) <see cref="PlaylistPickerPanel"/>
/// below <paramref name="anchor"/> through the overlay service — one open path for every call site (the rail, the
/// vertical hero's More menu), so they stay behavior-identical.</summary>
internal static class PlaylistPickerLauncher
{
    public static void OpenFlyout(IOverlayService svc, Func<NodeHandle> anchor, Func<IReadOnlyList<Track>> getTracks,
        Ref<OverlayHandle?> handle, FlyoutPlacement placement = FlyoutPlacement.BottomLeft)
    {
        if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
        handle.Value = svc.Open(
            anchor,
            () => Embed.Comp(() => new PlaylistPickerPanel { GetTracks = getTracks, Close = () => handle.Value?.Close() }),
            placement,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            { ConstrainToRootBounds = false });
        handle.Value.ClosedAction = () => handle.Value = null;
    }
}

/// <summary>The flyout content: a live search field + a "New playlist" row + the scrollable list of editable playlists.
/// Runs inside the open thunk, so it mounts fresh each open (always the current list).</summary>
public sealed class PlaylistPickerPanel : Component
{
    public required Func<IReadOnlyList<Track>> GetTracks;
    public required Action Close;
    /// <summary>Optional override for what picking a playlist DOES — the MOVE variant deposits into the target and then
    /// removes the rows from their source list. Null (the default) is the plain add, with its own toast.</summary>
    public Action<string, string>? Deposit;
    /// <summary>Hide this playlist from the list — a move's own source, which it can only be a no-op against.</summary>
    public string? ExcludeUri;

    public override Element Render()
    {
        var store = UseContext(LibraryStore.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var acts = UseContext(ActionServices.Slot);
        store?.EnsurePlaylists();

        var query = UseSignal("");
        string q = query.Value;
        var pls = store?.Playlists.Value.Value ?? Array.Empty<PlaylistSummary>();
        var getTracks = GetTracks;
        var close = Close;
        var deposit = Deposit;
        string? exclude = ExcludeUri;
        var post = UsePost();

        void AddTo(string uri, string name)
        {
            if (deposit is not null) { deposit(uri, name); close(); return; }
            if (lib is null) return;
            close();
            _ = Run();
            // AWAITED, then confirmed — this used to fire the write and toast "Added to X" unconditionally, so a failed
            // add reported success. Same correction as Menus.AddTo; the toast's action is Undo for the same reason.
            async System.Threading.Tasks.Task Run()
            {
                try
                {
                    long id = await lib.AddTracksTrackedAsync(uri, getTracks()).ConfigureAwait(false);
                    post(() =>
                    {
                        if (acts is not null) { Menus.RememberDeposit(acts, uri); Menus.ToastDeposited(acts, name, id); }
                        else Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions { Severity = InfoBarSeverity.Success });
                    });
                }
                catch (Exception ex) { post(() => PlaylistEditErrors.Toast(ex)); }
            }
        }

        void CreateAndAdd()
        {
            // The ONE create path (PlaylistCreateFlow): the numbered "My Playlist #N" name, the optimistic row in the
            // store before it returns, and the CreateFailed toast/notice observed off-thread. It needs the action bag,
            // so a host that provides none simply has no inline create rather than a second, divergent copy of it.
            if (lib is null || acts is null) return;
            if (PlaylistCreateFlow.Create(acts, default, navigate: false, out string name) is not { } created) return;
            string uri = created.Uri;
            _ = Run();
            async System.Threading.Tasks.Task Run()
            {
                try
                {
                    if (deposit is not null) { post(() => { deposit(uri, name); close(); }); return; }
                    await lib.AddTracksAsync(uri, getTracks()).ConfigureAwait(false);
                    post(() =>
                    {
                        close();
                        Menus.RememberDeposit(acts, uri);
                        // Open, not Undo: a freshly created playlist needs a name, and inline rename lives on its page.
                        Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
                        {
                            Severity = InfoBarSeverity.Success,
                            ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => go?.Invoke("pl:" + uri, name),
                        });
                    });
                }
                catch (Exception ex) { post(() => PlaylistEditErrors.Toast(ex)); }
            }
        }

        // MOST-RECENTLY-FILED FIRST, then rootlist order — the SAME ordering + eligibility the "Add to playlist ▸"
        // submenu uses, so "More playlists…" continues the list the submenu truncated instead of reshuffling it.
        var ordered = PlaylistDepositTargets.Order(
            pls, acts is not null ? Menus.RecentDeposits(acts) : null, exclude, q);
        var rows = new List<Element>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            rows.Add(PlaylistRow(p, () => AddTo(p.Uri, p.Name)));
        }

        Element list = rows.Count > 0
            ? new ScrollEl
            {
                ContentSized = true, MaxHeight = 360f,
                Content = new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() },
            }
            : new BoxEl
            {
                Height = 44f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                Children = [new TextEl(Loc.Get(Strings.Detail.NoPlaylists)) { Size = 13f, Color = Tok.TextSecondary }],
            };

        return new BoxEl
        {
            Direction = 1, Width = 320f, Gap = Spacing.XS, Padding = new Edges4(8f, 8f, 8f, 8f),
            Children =
            [
                Embed.Comp(() => new EditableText
                {
                    Placeholder = Loc.Get(Strings.Detail.FindPlaylist),
                    Width = 300f, Height = 32f, Text = query,
                }),
                NewPlaylistRow(CreateAndAdd),
                list,
            ],
        };
    }

    static Image? CoverOf(PlaylistSummary p)
    {
        if (p.Cover is { } c) return c;
        if (p.MosaicTiles is { Count: > 0 } tiles)
            return tiles.Count >= 4 ? new Image("", MosaicTiles: tiles) : new Image(tiles[0]);
        return null;
    }

    static int SeedFrom(string uri)
    {
        int h = 17;
        for (int i = 0; i < uri.Length; i++) h = h * 31 + uri[i];
        return h & 0x7fffffff;
    }

    static Element NewPlaylistRow(Action onClick) => new BoxEl
    {
        Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 10f,
        Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(4f),
        Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(6f), Fill = Tok.FillSubtleSecondary,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Add, 20f, Tok.TextSecondary)],
            },
            new TextEl(Loc.Get(Strings.Detail.NewPlaylist)) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    }.Interactive(Interaction.Subtle);

    static Element PlaylistRow(PlaylistSummary p, Action onClick) => new BoxEl
    {
        Key = p.Uri,
        Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 10f,
        Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(4f),
        Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            Surfaces.Artwork(CoverOf(p), SeedFrom(p.Uri), 40f, 40f, 6f, decodePx: 80),
            new BoxEl { Direction = 1, Grow = 1f, Gap = 1f, Children = NameColumn(p) },
        ],
    }.Interactive(Interaction.Subtle);

    static Element[] NameColumn(PlaylistSummary p) =>
        p.CanEdit && !p.IsOwner
            ? new Element[]
              {
                  new TextEl(p.Name) { Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                  new TextEl(Loc.Get(Strings.Detail.Collaborator)) { Size = 12f, Color = Tok.TextSecondary },
              }
            : new Element[]
              {
                  new TextEl(p.Name) { Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
              };
}
