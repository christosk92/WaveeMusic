using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Reusable location picker dialog. Shows a ContentDialog with city search + "Use current location".
/// Returns the selected city name, or null if cancelled.
/// </summary>
public static class LocationPickerDialog
{
    public static async Task<string?> ShowAsync(
        ILocationService locationService,
        string? currentCity,
        XamlRoot xamlRoot,
        ILogger? logger = null)
    {
        string? result = null;

        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = "Search city...",
            QueryIcon = new SymbolIcon(Symbol.Find),
            Width = 300,
            DisplayMemberPath = "FullName"
        };

        var useCurrentBtn = new HyperlinkButton
        {
            Content = "Use current location",
            Padding = new Thickness(0)
        };

        var panel = new StackPanel { Spacing = 16 };

        if (!string.IsNullOrEmpty(currentCity))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Current: {currentCity}",
                Opacity = 0.6,
                FontSize = 13
            });
        }

        panel.Children.Add(searchBox);
        panel.Children.Add(useCurrentBtn);

        var dialog = new ContentDialog
        {
            Title = "Concert location",
            Content = panel,
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot
        };

        // Search with throttle
        CancellationTokenSource? searchCts = null;
        searchBox.TextChanged += async (s, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = s.Text?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length < 2) return;

            searchCts?.Cancel();
            searchCts = new CancellationTokenSource();
            var ct = searchCts.Token;

            try
            {
                await Task.Delay(300, ct);
                var results = await locationService.SearchAsync(query, ct);
                if (!ct.IsCancellationRequested)
                    s.ItemsSource = results;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger?.LogWarning(ex, "Location search failed"); }
        };

        // Save selected location
        searchBox.SuggestionChosen += async (s, args) =>
        {
            if (args.SelectedItem is not LocationSearchResult loc || string.IsNullOrEmpty(loc.GeonameId)) return;
            try
            {
                await locationService.SaveByGeonameIdAsync(loc.GeonameId, loc.Name);
                result = loc.Name;
                dialog.Hide();
            }
            catch (Exception ex) { logger?.LogWarning(ex, "Failed to save location"); }
        };

        // Resolve current GPS location (confirm before saving)
        useCurrentBtn.Click += async (s, args) =>
        {
            try
            {
                useCurrentBtn.IsEnabled = false;
                useCurrentBtn.Content = "Detecting location...";

                var geolocator = new Windows.Devices.Geolocation.Geolocator();
                var position = await geolocator.GetGeopositionAsync();
                var resolved = await locationService.SearchByCoordinatesAsync(
                    position.Coordinate.Point.Position.Latitude,
                    position.Coordinate.Point.Position.Longitude);

                if (resolved == null)
                {
                    useCurrentBtn.Content = "Could not detect location";
                    useCurrentBtn.IsEnabled = true;
                    return;
                }

                // Pre-fill for user confirmation
                searchBox.Text = resolved.FullName ?? resolved.Name ?? "";
                searchBox.ItemsSource = new[] { resolved };
                useCurrentBtn.Content = $"Detected: {resolved.Name} — select above to confirm";
                useCurrentBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to get current location");
                useCurrentBtn.Content = "Failed to detect location";
                useCurrentBtn.IsEnabled = true;
            }
        };

        await dialog.ShowAsync();
        return result;
    }
}
