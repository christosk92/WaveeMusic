using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

public sealed class ArtistMenuContext
{
    public required string ArtistId { get; init; }
    public required string ArtistName { get; init; }
    public bool IsFollowing { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? ToggleFollowCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
}

public static class ArtistContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(ArtistMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:artist:" + ctx.ArtistId;

        // Primary: Play · Follow/Unfollow
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Glyph = FluentGlyphs.Play,
            AccentIconStyleKey = "App.AccentIcons.Media.Play",
            Command = ctx.PlayCommand,
            Invoke = ctx.PlayCommand is null ? () => Debug.WriteLine($"PlayArtist: {uri}") : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsFollowing
                ? "ArtistMenu_Unfollow"
                : "ArtistMenu_Follow"),
            Glyph = ctx.IsFollowing ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = ctx.IsFollowing ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            Command = ctx.ToggleFollowCommand,
            Invoke = ctx.ToggleFollowCommand is null ? () => Debug.WriteLine($"ToggleFollowArtist: {uri}") : null,
            IsPrimary = true
        });

        items.Add(ContextMenuItemModel.Separator);

        // Secondary
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.AddToQueue,
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => Debug.WriteLine($"AddArtistToQueue: {uri}") : null
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("ArtistMenu_ArtistRadio"),
            Glyph = FluentGlyphs.Radio,
            Invoke = () => _ = Ioc.Default.GetService<IPlaybackStateService>()
                                ?.StartRadioAsync(uri, ctx.ArtistName is { Length: > 0 } name ? $"{name} Radio" : "Artist Radio")
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Share"),
            Glyph = FluentGlyphs.Share,
            Command = ctx.ShareCommand,
            Invoke = ctx.ShareCommand is null ? () => Debug.WriteLine($"ShareArtist: {uri}") : null
        });

        return items;
    }
}
