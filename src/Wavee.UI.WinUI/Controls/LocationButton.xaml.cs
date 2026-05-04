using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Reusable location button that shows "📍 City" or "Set location",
/// and opens a LocationPickerDialog on click.
/// </summary>
public sealed partial class LocationButton : UserControl
{
    private readonly ILocationService? _locationService;
    private readonly ILogger? _logger;

    /// <summary>The displayed city name. Bind this two-way from the parent ViewModel.</summary>
    public string? LocationName
    {
        get => (string?)GetValue(LocationNameProperty);
        set => SetValue(LocationNameProperty, value);
    }

    public static readonly DependencyProperty LocationNameProperty =
        DependencyProperty.Register(nameof(LocationName), typeof(string), typeof(LocationButton),
            new PropertyMetadata(null));

    /// <summary>Raised after the user selects a new location and it's saved.</summary>
    public event EventHandler<string>? LocationChanged;

    public LocationButton()
    {
        _locationService = Ioc.Default.GetService<ILocationService>();
        _logger = Ioc.Default.GetService<ILogger<LocationButton>>();
        InitializeComponent();
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        if (_locationService == null) return;

        var city = await LocationPickerDialog.ShowAsync(
            _locationService, LocationName, XamlRoot, _logger);

        if (city != null)
        {
            LocationName = city;
            LocationChanged?.Invoke(this, city);
        }
    }
}
