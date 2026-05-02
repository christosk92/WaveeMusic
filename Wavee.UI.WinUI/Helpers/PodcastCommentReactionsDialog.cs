using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Shared "reactions on a comment" dialog used by both the library episode
/// detail view and the right-sidebar comments list. Lifted out of
/// <c>YourEpisodesView.xaml.cs</c> so both surfaces render the same dialog.
/// Caller passes the <see cref="XamlRoot"/> for the host window plus a
/// <c>loadPageAsync</c> callback that resolves a page of reactions for either
/// a top-level comment or a reply (<c>PodcastCommentViewModel.GetReactionsAsync</c>
/// or <c>PodcastReplyViewModel.GetReactionsAsync</c>).
/// </summary>
internal static class PodcastCommentReactionsDialog
{
    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        Func<string?, string?, Task<PodcastCommentReactionsPageDto?>> loadPageAsync)
    {
        var reactions = new List<PodcastCommentReactionDto>();
        IReadOnlyList<PodcastCommentReactionCountDto> reactionCounts = [];
        string? selectedReaction = null;
        string? nextPageToken = null;

        var chipsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };

        var listPanel = new StackPanel { Spacing = 6 };
        var statusText = new TextBlock
        {
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var loadMoreButton = new Button
        {
            Content = "Load more",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 6, 14, 6),
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel
        {
            Spacing = 14,
            MinWidth = 420,
            MaxWidth = 540,
        };
        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = chipsPanel,
        });
        content.Children.Add(statusText);
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listPanel,
        });
        content.Children.Add(loadMoreButton);

        async Task LoadAsync(bool reset)
        {
            if (reset)
            {
                reactions.Clear();
                nextPageToken = null;
            }
            else if (string.IsNullOrWhiteSpace(nextPageToken))
            {
                return;
            }

            loadMoreButton.IsEnabled = false;
            statusText.Text = reset ? "Loading reactions..." : "Loading more reactions...";
            statusText.Visibility = Visibility.Visible;

            PodcastCommentReactionsPageDto? page;
            try
            {
                page = await loadPageAsync(reset ? null : nextPageToken, selectedReaction);
            }
            catch
            {
                statusText.Text = "Could not load reactions.";
                statusText.Visibility = Visibility.Visible;
                loadMoreButton.IsEnabled = true;
                RenderChips();
                return;
            }

            if (page is not null)
            {
                reactionCounts = page.ReactionCounts;
                reactions.AddRange(page.Items);
                nextPageToken = page.NextPageToken;
            }

            RenderChips();
            RenderList();
            loadMoreButton.IsEnabled = true;
        }

        void RenderChips()
        {
            chipsPanel.Children.Clear();
            var total = reactionCounts.Sum(static count => count.Count);
            chipsPanel.Children.Add(BuildFilterButton(
                total > 0 ? $"All {total:N0}" : "All",
                selectedReaction is null,
                async () =>
                {
                    selectedReaction = null;
                    await LoadAsync(reset: true);
                }));

            foreach (var count in reactionCounts)
            {
                chipsPanel.Children.Add(BuildFilterButton(
                    $"{count.ReactionUnicode} {count.CountFormatted}",
                    string.Equals(selectedReaction, count.ReactionUnicode, StringComparison.Ordinal),
                    async () =>
                    {
                        selectedReaction = count.ReactionUnicode;
                        await LoadAsync(reset: true);
                    }));
            }
        }

        void RenderList()
        {
            listPanel.Children.Clear();
            statusText.Visibility = Visibility.Collapsed;

            if (reactions.Count == 0)
            {
                statusText.Text = "No reactions yet.";
                statusText.Visibility = Visibility.Visible;
            }
            else
            {
                foreach (var reaction in reactions)
                    listPanel.Children.Add(BuildReactionRow(reaction));
            }

            loadMoreButton.Visibility = string.IsNullOrWhiteSpace(nextPageToken)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        loadMoreButton.Click += async (_, _) => await LoadAsync(reset: false);

        await LoadAsync(reset: true);

        var dialog = new ContentDialog
        {
            Title = "Reactions",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = content,
            XamlRoot = xamlRoot,
        };

        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var styleObject) &&
            styleObject is Style style)
        {
            dialog.Style = style;
        }

        await dialog.ShowAsync();
    }

    private static Button BuildFilterButton(string label, bool selected, Func<Task> click)
    {
        var button = new Button
        {
            Content = label,
            MinHeight = 0,
            Padding = new Thickness(12, 6, 12, 6),
            Background = selected
                ? Brush("AccentFillColorDefaultBrush")
                : Brush("SubtleFillColorSecondaryBrush"),
            Foreground = selected
                ? Brush("TextOnAccentFillColorPrimaryBrush")
                : Brush("TextFillColorPrimaryBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        };
        button.Click += async (_, _) => await click();
        return button;
    }

    private static FrameworkElement BuildReactionRow(PodcastCommentReactionDto reaction)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(0, 6, 0, 6),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var person = new PersonPicture
        {
            Width = 40,
            Height = 40,
            DisplayName = reaction.AuthorName,
        };
        if (!string.IsNullOrWhiteSpace(reaction.AuthorImageUrl) &&
            Uri.TryCreate(reaction.AuthorImageUrl, UriKind.Absolute, out var imageUri))
        {
            person.ProfilePicture = new BitmapImage(imageUri);
        }
        Grid.SetColumn(person, 0);
        grid.Children.Add(person);

        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(reaction.AuthorName) ? "Spotify user" : reaction.AuthorName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"],
        });
        text.Children.Add(new TextBlock
        {
            Text = reaction.CreatedAtFormatted,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Brush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var emoji = new TextBlock
        {
            Text = reaction.ReactionUnicode,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(emoji, 2);
        grid.Children.Add(emoji);

        return grid;
    }

    private static Brush Brush(string resourceKey)
        => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[resourceKey];
}
