using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Context for a sidebar playlist entry's menu, shaped to match Spotify's layout:
/// Add to queue · Add to profile · Report · Remove/Save · Create playlist · Create folder
/// · Exclude from taste profile · Move to folder ▶ · Share ▶.
/// </summary>
public sealed class SidebarPlaylistMenuContext
{
    public required string PlaylistId { get; init; }
    public required string PlaylistName { get; init; }
    public bool IsInLibrary { get; init; }
    public bool IsOwner { get; init; }

    public Action? PlayAction { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public Action? AddToProfileAction { get; init; }
    public Action? ReportAction { get; init; }
    public Action? ToggleLibraryAction { get; init; }
    public Action? CreatePlaylistAction { get; init; }
    public Action? CreateFolderAction { get; init; }
    public Action? ExcludeFromTasteAction { get; init; }
    public Action? DeleteAction { get; init; }

    public Func<IReadOnlyList<ContextMenuItemModel>>? BuildMoveTargets { get; init; }
    public Func<IReadOnlyList<ContextMenuItemModel>>? BuildShareTargets { get; init; }
}

public static class SidebarPlaylistContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(SidebarPlaylistMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:playlist:" + ctx.PlaylistId;

        // ── Quick actions ─────────────────────────────────────────────────
        // Play comes first — Spotify-desktop parity. Default behavior fires
        // IPlaybackService.PlayContextAsync(uri); hosts can override via
        // PlayAction (e.g. to add play-origin / preserve cluster context).
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Glyph = FluentGlyphs.Play,
            AccentIconStyleKey = "App.AccentIcons.Media.Play",
            IsPrimary = true,
            Invoke = ctx.PlayAction ?? (() => PlayContextDefault(uri))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.Queue,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayAfter",
            Command = ctx.AddToQueueCommand,
            CommandParameter = uri,
            IsPrimary = true,
            Invoke = ctx.AddToQueueCommand is null
                ? () => Ioc.Default.GetService<IPlaybackStateService>()?.AddToQueue(uri)
                : null
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsInLibrary
                ? "SidebarMenu_RemoveFromLibrary"
                : "SidebarMenu_SaveToLibrary"),
            Glyph = ctx.IsInLibrary ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = ctx.IsInLibrary ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            IsPrimary = true,
            // Owners delete instead of unfollowing — hide the heart for them.
            ShowItem = !ctx.IsOwner,
            Invoke = ctx.ToggleLibraryAction ?? (() => ToggleLibraryDefault(uri, ctx.IsInLibrary))
        });

        // ── Primary dropdown items ────────────────────────────────────────
        // Only show entries whose action is either explicitly wired OR has a
        // real default behavior. Add to profile / Report / Exclude from taste
        // are Spotify-server features Wavee doesn't yet route — omit them
        // unless the caller wires them. Owners get fewer entries (deleting
        // their own content instead of reporting / excluding).

        var hasOptionalEntries = ctx.AddToProfileAction is not null
            || ctx.ReportAction is not null
            || ctx.ExcludeFromTasteAction is not null;
        if (hasOptionalEntries)
            items.Add(ContextMenuItemModel.Separator);

        if (ctx.AddToProfileAction is { } addToProfile)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_AddToProfile"),
                Glyph = FluentGlyphs.AddToProfile,
                Invoke = addToProfile
            });
        }

        if (!ctx.IsOwner && ctx.ReportAction is { } report)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Report"),
                Glyph = FluentGlyphs.Report,
                Invoke = report
            });
        }

        // ── Creation shortcuts ────────────────────────────────────────────
        items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreatePlaylist"),
            Glyph = FluentGlyphs.CreatePlaylist,
            Invoke = ctx.CreatePlaylistAction ?? (() => NavigationHelpers.OpenCreatePlaylist(isFolder: false))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreateFolder"),
            Glyph = FluentGlyphs.CreateFolder,
            Invoke = ctx.CreateFolderAction ?? (() => NavigationHelpers.OpenCreatePlaylist(isFolder: true))
        });

        // ── Taste profile & move ──────────────────────────────────────────
        if (!ctx.IsOwner && ctx.ExcludeFromTasteAction is { } excludeFromTaste)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_ExcludeFromTaste"),
                Glyph = FluentGlyphs.Exclude,
                Invoke = excludeFromTaste
            });
        }

        if (ctx.BuildMoveTargets is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_MoveToFolder"),
                Glyph = FluentGlyphs.MoveTo,
                Items = ctx.BuildMoveTargets()
            });
        }

        // ── Share ▶ ──────────────────────────────────────────────────────
        if (ctx.BuildShareTargets is not null)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Share"),
                Glyph = FluentGlyphs.Share,
                Items = ctx.BuildShareTargets()
            });
        }

        // ── Delete (owned playlists only, destructive, last) ─────────────
        if (ctx.IsOwner)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Delete"),
                Glyph = FluentGlyphs.Delete,
                IsDestructive = true,
                Invoke = ctx.DeleteAction ?? (() => DeletePlaylistDefault(uri))
            });
        }

        return items;
    }

    private static void PlayContextDefault(string uri)
    {
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        _ = playback.PlayContextAsync(uri);
    }

    private static async void ToggleLibraryDefault(string uri, bool wasSaved)
    {
        var mutations = Ioc.Default.GetService<IPlaylistMutationService>();
        if (mutations is null) return;
        try
        {
            await mutations.SetPlaylistFollowedAsync(uri, !wasSaved).ConfigureAwait(true);
        }
        catch
        {
            Ioc.Default.GetService<INotificationService>()?.Show(
                wasSaved ? "Couldn't remove from library" : "Couldn't save to library",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }

    private static async void DeletePlaylistDefault(string uri)
    {
        var mutations = Ioc.Default.GetService<IPlaylistMutationService>();
        if (mutations is null) return;
        try
        {
            await mutations.DeletePlaylistAsync(uri).ConfigureAwait(true);
        }
        catch
        {
            Ioc.Default.GetService<INotificationService>()?.Show(
                "Couldn't delete playlist",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }
}
