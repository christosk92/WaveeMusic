using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Input;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

public sealed class AlbumMenuContext
{
    public required string AlbumId { get; init; }
    public required string AlbumName { get; init; }
    public string? ArtistId { get; init; }
    public string? ArtistName { get; init; }
    public bool IsSaved { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? ShuffleCommand { get; init; }
    public ICommand? ToggleSaveCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
}

public static class AlbumContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(AlbumMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:album:" + ctx.AlbumId;

        // Primary: Play · Shuffle · Save
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Glyph = FluentGlyphs.Play,
            AccentIconStyleKey = "App.AccentIcons.Media.Play",
            Command = ctx.PlayCommand,
            Invoke = ctx.PlayCommand is null ? () => Debug.WriteLine($"PlayAlbum: {uri}") : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("PlaylistMenu_Shuffle"),
            Glyph = FluentGlyphs.Shuffle,
            AccentIconStyleKey = "App.AccentIcons.Media.Shuffle",
            Command = ctx.ShuffleCommand,
            Invoke = ctx.ShuffleCommand is null ? () => Debug.WriteLine($"ShuffleAlbum: {uri}") : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsSaved
                ? "CardMenu_RemoveFromLibrary"
                : "CardMenu_SaveToLibrary"),
            Glyph = ctx.IsSaved ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = ctx.IsSaved ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            Command = ctx.ToggleSaveCommand,
            Invoke = ctx.ToggleSaveCommand is null ? () => Debug.WriteLine($"ToggleAlbumSave: {uri}") : null,
            IsPrimary = true
        });

        items.Add(ContextMenuItemModel.Separator);

        // Secondary
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.AddToQueue,
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => Debug.WriteLine($"AddAlbumToQueue: {uri}") : null
        });

        if (!string.IsNullOrEmpty(ctx.ArtistId))
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_GoToArtist"),
                Glyph = FluentGlyphs.Artist,
                Invoke = () => NavigationHelpers.OpenArtist(ctx.ArtistId!, ctx.ArtistName ?? string.Empty)
            });
        }

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("AlbumMenu_AlbumRadio"),
            Glyph = FluentGlyphs.Radio,
            Invoke = () => Debug.WriteLine($"AlbumRadio: {uri}")
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Share"),
            Glyph = FluentGlyphs.Share,
            Command = ctx.ShareCommand,
            Invoke = ctx.ShareCommand is null ? () => Debug.WriteLine($"ShareAlbum: {uri}") : null
        });

        return items;
    }
}
