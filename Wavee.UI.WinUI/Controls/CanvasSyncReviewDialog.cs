using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Controls;

public enum CanvasSyncReviewResult
{
    Cancel = 0,
    KeepCurrent = 1,
    UseNew = 2,
}

public static class CanvasSyncReviewDialog
{
    public static async Task<CanvasSyncReviewResult> ShowAsync(
        XamlRoot xamlRoot,
        string? currentCanvasUrl,
        string? newCanvasUrl)
    {
        var mediaPlayers = new List<MediaPlayer>();
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "New canvas available",
            PrimaryButtonText = "Use new",
            SecondaryButtonText = "Keep current",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = BuildDialogContent(currentCanvasUrl, newCanvasUrl, mediaPlayers),
        };

        try
        {
            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => CanvasSyncReviewResult.UseNew,
                ContentDialogResult.Secondary => CanvasSyncReviewResult.KeepCurrent,
                _ => CanvasSyncReviewResult.Cancel,
            };
        }
        finally
        {
            foreach (var player in mediaPlayers)
            {
                try
                {
                    player.Source = null;
                    player.Dispose();
                }
                catch
                {
                    // Best effort dialog cleanup only.
                }
            }
        }
    }

    private static UIElement BuildDialogContent(
        string? currentCanvasUrl,
        string? newCanvasUrl,
        List<MediaPlayer> mediaPlayers)
    {
        var root = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 920,
        };

        root.Children.Add(new TextBlock
        {
            Text = "Review the current saved canvas against the newly detected Spotify canvas before switching.",
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var currentCard = CreatePreviewCard("Current", currentCanvasUrl, mediaPlayers);
        Grid.SetColumn(currentCard, 0);
        grid.Children.Add(currentCard);

        var newCard = CreatePreviewCard("New", newCanvasUrl, mediaPlayers);
        Grid.SetColumn(newCard, 1);
        grid.Children.Add(newCard);

        root.Children.Add(grid);
        return root;
    }

    private static FrameworkElement CreatePreviewCard(
        string title,
        string? canvasUrl,
        List<MediaPlayer> mediaPlayers)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var border = new Border
        {
            Height = 320,
            CornerRadius = new CornerRadius(12),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(8),
        };

        if (Uri.TryCreate(canvasUrl, UriKind.Absolute, out var uri))
        {
            var player = new MediaPlayer
            {
                IsMuted = true,
                IsLoopingEnabled = true,
                AutoPlay = true,
            };
            player.Source = MediaSource.CreateFromUri(uri);
            mediaPlayers.Add(player);

            var element = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.UniformToFill,
                AutoPlay = true,
            };
            element.SetMediaPlayer(player);
            border.Child = element;
        }
        else
        {
            border.Child = new TextBlock
            {
                Text = "Preview unavailable",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
        }

        stack.Children.Add(border);
        return stack;
    }
}
