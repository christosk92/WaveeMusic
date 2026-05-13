using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Local.Classification;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Builds the context menu shown when the user right-clicks any local item
/// (track / episode / movie / music video / other). Mirrors the static-method
/// shape used by <c>SidebarPlaylistContextMenuBuilder</c> so it slots into
/// <see cref="ContextMenuHost.Show"/> the same way.
/// </summary>
public static class LocalItemContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(LocalItemMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var isVideo = ctx.Kind.IsVideo();

        // Play / Resume
        items.Add(new ContextMenuItemModel
        {
            Text = ctx.LastPositionMs > 0 ? "Resume" : "Play",
            Glyph = FluentGlyphs.Play,
            IsPrimary = true,
            Invoke = () => ctx.OnPlay?.Invoke(),
        });

        // Mark watched (video) / Like (audio)
        if (isVideo)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = ctx.WatchedAt.HasValue ? "Mark unwatched" : "Mark watched",
                Glyph = FluentGlyphs.CheckMark,
                Invoke = () => ctx.OnToggleWatched?.Invoke(),
            });
        }
        else
        {
            items.Add(new ContextMenuItemModel
            {
                Text = ctx.IsLiked ? "Remove from liked" : "Add to liked",
                Glyph = ctx.IsLiked ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
                Invoke = () => ctx.OnToggleLike?.Invoke(),
            });
        }

        if (ctx.Kind == LocalContentKind.MusicVideo)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = string.IsNullOrWhiteSpace(ctx.LinkedSpotifyTrackUri)
                    ? "Link Spotify track..."
                    : "Change Spotify track link...",
                Glyph = FluentGlyphs.Video,
                Invoke = () => ctx.OnLinkSpotifyTrack?.Invoke(),
            });

            if (!string.IsNullOrWhiteSpace(ctx.LinkedSpotifyTrackUri))
            {
                items.Add(new ContextMenuItemModel
                {
                    Text = "Remove Spotify track link",
                    Glyph = FluentGlyphs.Remove,
                    Invoke = () => ctx.OnUnlinkSpotifyTrack?.Invoke(),
                });
            }
        }

        items.Add(ContextMenuItemModel.Separator);

        // Set kind submenu
        items.Add(new ContextMenuItemModel
        {
            Text = "Set kind",
            Glyph = FluentGlyphs.Album,
            Items = new[]
            {
                MakeKind("Music",       LocalContentKind.Music, ctx),
                MakeKind("Music video", LocalContentKind.MusicVideo, ctx),
                MakeKind("TV episode",  LocalContentKind.TvEpisode, ctx),
                MakeKind("Movie",       LocalContentKind.Movie, ctx),
                MakeKind("Other",       LocalContentKind.Other, ctx),
            },
        });

        // Add to collection
        items.Add(new ContextMenuItemModel
        {
            Text = "Add to collection",
            Glyph = FluentGlyphs.Add,
            LoadSubMenuAsync = async () =>
            {
                var collections = ctx.Facade is not null
                    ? await ctx.Facade.GetCollectionsAsync()
                    : Array.Empty<LocalCollection>();
                var subItems = collections.Select(c => new ContextMenuItemModel
                {
                    Text = c.Name,
                    Glyph = FluentGlyphs.Playlist,
                    Invoke = () => ctx.OnAddToCollection?.Invoke(c.Id),
                }).ToList();
                subItems.Add(ContextMenuItemModel.Separator);
                subItems.Add(new ContextMenuItemModel
                {
                    Text = "New collection…",
                    Glyph = FluentGlyphs.Add,
                    Invoke = () => ctx.OnNewCollection?.Invoke(),
                });
                return subItems;
            },
        });

        // Video-only — Subtitles + Audio submenus
        if (isVideo)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = "Subtitles",
                Glyph = FluentGlyphs.ClosedCaption,
                LoadSubMenuAsync = async () =>
                {
                    var subs = ctx.Facade is not null
                        ? await ctx.Facade.GetSubtitlesForAsync(ctx.FilePath)
                        : Array.Empty<LocalSubtitle>();
                    var subItems = subs.Select(s => new ContextMenuItemModel
                    {
                        Text = $"{s.Language ?? "Unknown"}{(s.Forced ? " (forced)" : "")}{(s.Embedded ? " · embedded" : "")}",
                        Glyph = FluentGlyphs.ClosedCaption,
                        Invoke = () => ctx.OnSelectSubtitle?.Invoke(s),
                    }).ToList();
                    subItems.Add(ContextMenuItemModel.Separator);
                    subItems.Add(new ContextMenuItemModel
                    {
                        Text = "Add subtitle file…",
                        Glyph = FluentGlyphs.Add,
                        Invoke = () => ctx.OnAddSubtitleFile?.Invoke(),
                    });
                    return subItems;
                },
            });

            items.Add(new ContextMenuItemModel
            {
                Text = "Audio",
                Glyph = FluentGlyphs.AudioTrack,
                LoadSubMenuAsync = async () =>
                {
                    var audios = ctx.Facade is not null
                        ? await ctx.Facade.GetAudioTracksForAsync(ctx.FilePath)
                        : Array.Empty<LocalEmbeddedTrack>();
                    return audios.Select(a => new ContextMenuItemModel
                    {
                        Text = $"{a.Language ?? "Unknown"}{(string.IsNullOrEmpty(a.Codec) ? "" : " · " + a.Codec)}{(a.IsDefault ? " · default" : "")}",
                        Glyph = FluentGlyphs.AudioTrack,
                        Invoke = () => ctx.OnSelectAudioTrack?.Invoke(a),
                    }).ToList();
                },
            });
        }

        items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = "Edit details",
            Glyph = FluentGlyphs.Edit,
            Invoke = () => ctx.OnEditDetails?.Invoke(),
        });
        items.Add(new ContextMenuItemModel
        {
            Text = "Refresh metadata",
            Glyph = FluentGlyphs.Refresh,
            Invoke = () => ctx.OnRefreshMetadata?.Invoke(),
        });
        items.Add(new ContextMenuItemModel
        {
            Text = "Show in Explorer",
            Glyph = FluentGlyphs.Folder,
            Invoke = () => ctx.OnShowInExplorer?.Invoke(),
        });

        items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = "Remove from library",
            Glyph = FluentGlyphs.Remove,
            Invoke = () => ctx.OnRemoveFromLibrary?.Invoke(),
        });
        items.Add(new ContextMenuItemModel
        {
            Text = "Delete from disk",
            Glyph = FluentGlyphs.Delete,
            IsDestructive = true,
            Invoke = () => ctx.OnDeleteFromDisk?.Invoke(),
        });

        return items;
    }

    private static ContextMenuItemModel MakeKind(string label, LocalContentKind kind, LocalItemMenuContext ctx) =>
        new()
        {
            Text = label,
            Glyph = ctx.Kind == kind ? FluentGlyphs.CheckMark : null,
            Invoke = () => ctx.OnSetKind?.Invoke(kind),
        };
}

/// <summary>
/// Builder input — supplies the data the menu reads to make decisions, plus
/// callbacks the menu items invoke. Pages assemble this in their right-click
/// handlers.
/// </summary>
public sealed class LocalItemMenuContext
{
    public required string TrackUri { get; init; }
    public required string FilePath { get; init; }
    public required LocalContentKind Kind { get; init; }
    public long LastPositionMs { get; init; }
    public long? WatchedAt { get; init; }
    public bool IsLiked { get; init; }
    public string? LinkedSpotifyTrackUri { get; init; }
    public ILocalLibraryFacade? Facade { get; init; }

    // Callbacks — page code-behind wires these to specific behaviour.
    public Action? OnPlay { get; init; }
    public Action? OnToggleWatched { get; init; }
    public Action? OnToggleLike { get; init; }
    public Action? OnLinkSpotifyTrack { get; init; }
    public Action? OnUnlinkSpotifyTrack { get; init; }
    public Action<LocalContentKind>? OnSetKind { get; init; }
    public Action<string>? OnAddToCollection { get; init; }
    public Action? OnNewCollection { get; init; }
    public Action<LocalSubtitle>? OnSelectSubtitle { get; init; }
    public Action? OnAddSubtitleFile { get; init; }
    public Action<LocalEmbeddedTrack>? OnSelectAudioTrack { get; init; }
    public Action? OnEditDetails { get; init; }
    public Action? OnRefreshMetadata { get; init; }
    public Action? OnShowInExplorer { get; init; }
    public Action? OnRemoveFromLibrary { get; init; }
    public Action? OnDeleteFromDisk { get; init; }
}
