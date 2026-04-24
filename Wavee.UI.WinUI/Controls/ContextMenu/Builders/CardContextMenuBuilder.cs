using System;
using System.Collections.Generic;
using System.Windows.Input;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

public enum CardEntityType
{
    Unknown,
    Artist,
    Album,
    Playlist,
    Show,
    Episode,
    LikedSongs
}

/// <summary>
/// Input for the card-surface context menu. Callers provide either a <see cref="OpenAction"/>
/// pair (Open / Open in new tab) for full control, or just the <see cref="Uri"/> + entity
/// type and we derive navigation via <see cref="NavigationHelpers"/>.
/// </summary>
public sealed class CardMenuContext
{
    public required string Uri { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public CardEntityType EntityType { get; init; } = CardEntityType.Unknown;

    /// <summary>
    /// Called when the user picks "Open". When null, the builder navigates via
    /// <see cref="NavigationHelpers"/> based on the URI.
    /// </summary>
    public Action<bool>? OpenAction { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? SaveToggleCommand { get; init; }
    public bool IsSaved { get; init; }
    public ICommand? ShareCommand { get; init; }
}

public static class CardContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(CardMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();

        // Quick actions (top icon row)
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("CardMenu_Open"),
            Glyph = FluentGlyphs.Open,
            IsPrimary = true,
            Invoke = () => Open(ctx, openInNewTab: false)
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
            Glyph = FluentGlyphs.OpenInNewTab,
            IsPrimary = true,
            Invoke = () => Open(ctx, openInNewTab: true)
        });

        if (ctx.PlayCommand is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("CardMenu_Play"),
                Glyph = FluentGlyphs.Play,
                IsPrimary = true,
                Command = ctx.PlayCommand,
                CommandParameter = ctx.Uri
            });
        }

        // Library state
        if (ctx.SaveToggleCommand is not null)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString(ctx.IsSaved
                    ? "CardMenu_RemoveFromLibrary"
                    : "CardMenu_SaveToLibrary"),
                Glyph = ctx.IsSaved ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
                Command = ctx.SaveToggleCommand,
                CommandParameter = ctx.Uri
            });
        }

        // Share
        if (ctx.ShareCommand is not null)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("CardMenu_Share"),
                Glyph = FluentGlyphs.Share,
                Command = ctx.ShareCommand,
                CommandParameter = ctx.Uri
            });
        }

        return items;
    }

    private static void Open(CardMenuContext ctx, bool openInNewTab)
    {
        if (ctx.OpenAction is not null)
        {
            ctx.OpenAction(openInNewTab);
            return;
        }

        NavigateByUri(ctx.Uri, ctx.Title, ctx.EntityType, openInNewTab);
    }

    private static void NavigateByUri(string uri, string title, CardEntityType type, bool openInNewTab)
    {
        if (string.IsNullOrEmpty(uri)) return;

        switch (type)
        {
            case CardEntityType.Artist:
                NavigationHelpers.OpenArtist(ExtractId(uri, "spotify:artist:"), title, openInNewTab);
                return;
            case CardEntityType.Album:
                NavigationHelpers.OpenAlbum(ExtractId(uri, "spotify:album:"), title, openInNewTab);
                return;
            case CardEntityType.Playlist:
                NavigationHelpers.OpenPlaylist(ExtractId(uri, "spotify:playlist:"), title, openInNewTab);
                return;
            case CardEntityType.LikedSongs:
                NavigationHelpers.OpenLikedSongs(openInNewTab);
                return;
        }

        // Unknown: parse the URI prefix.
        var parts = uri.Split(':');
        if (parts.Length < 3) return;
        switch (parts[1])
        {
            case "artist":   NavigationHelpers.OpenArtist(uri, title, openInNewTab); break;
            case "album":    NavigationHelpers.OpenAlbum(uri, title, openInNewTab); break;
            case "playlist": NavigationHelpers.OpenPlaylist(uri, title, openInNewTab); break;
            case "user" when uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                NavigationHelpers.OpenLikedSongs(openInNewTab); break;
        }
    }

    private static string ExtractId(string uri, string prefix) =>
        uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
}
