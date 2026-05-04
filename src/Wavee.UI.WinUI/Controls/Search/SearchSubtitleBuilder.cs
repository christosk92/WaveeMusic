using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.Search;

/// <summary>
/// Builds the subtitle for a <see cref="SearchResultItem"/> into a <see cref="TextBlock"/>
/// as inline runs + clickable artist <see cref="Hyperlink"/>s. Shared by
/// <see cref="SearchResultRowCard"/> and <see cref="SearchResultHeroCard"/>.
/// </summary>
internal static class SearchSubtitleBuilder
{
    public static void Build(TextBlock target, SearchResultItem item)
    {
        target.Inlines.Clear();

        switch (item.Type)
        {
            case SearchResultType.Track:
                target.Inlines.Add(new Run { Text = "Song" });
                AppendArtistInlines(target, item.ArtistUris, item.ArtistNames);
                break;

            case SearchResultType.Album:
                target.Inlines.Add(new Run { Text = "Album" });
                AppendArtistInlines(target, item.ArtistUris, item.ArtistNames);
                if (item.ReleaseYear is int year)
                    target.Inlines.Add(new Run { Text = $" · {year}" });
                break;

            case SearchResultType.Artist:
                target.Inlines.Add(new Run { Text = "Artist" });
                break;

            case SearchResultType.Playlist:
                target.Inlines.Add(new Run
                {
                    Text = string.IsNullOrWhiteSpace(item.OwnerName)
                        ? "Playlist"
                        : $"Playlist · {item.OwnerName}"
                });
                break;
        }
    }

    private static void AppendArtistInlines(TextBlock target, List<string>? uris, List<string>? names)
    {
        if (names is null || names.Count == 0)
            return;

        target.Inlines.Add(new Run { Text = " · " });

        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0)
                target.Inlines.Add(new Run { Text = ", " });

            var uri = uris != null && i < uris.Count ? uris[i] : null;
            var name = names[i];

            if (string.IsNullOrEmpty(uri))
            {
                target.Inlines.Add(new Run { Text = name });
                continue;
            }

            var hyperlink = new Hyperlink
            {
                UnderlineStyle = UnderlineStyle.None
            };
            hyperlink.Inlines.Add(new Run { Text = name });

            // Capture for the handler; the event fires when the user taps the hyperlink
            // inside the TextBlock — the TextBlock itself does NOT bubble the tap up to
            // the card, so the row-level tap handler still only runs for clicks outside
            // the hyperlink area (which is what we want).
            var artistUri = uri;
            var artistName = name;
            hyperlink.Click += (_, _) => NavigationHelpers.OpenArtist(artistUri, artistName);

            target.Inlines.Add(hyperlink);
        }
    }
}
