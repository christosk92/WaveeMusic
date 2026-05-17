using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels.Home;

namespace Wavee.UI.WinUI.Views.Home;

/// <summary>
/// Bottom-of-Home Browse All surface — a flat-but-grouped grid of category
/// chips fetched lazily from the Pathfinder <c>browseAll</c> persistedQuery.
/// Renders a shimmer skeleton at rest and triggers the fetch only when the
/// section enters within ~one viewport of being on-screen, then swaps to the
/// real chip grid.
/// </summary>
/// <remarks>
/// State is driven imperatively (not via x:Bind) because nested chains
/// through a UserControl DP (Adapter.IsBrowseLoading / Adapter.BrowseGroups)
/// don't always re-evaluate reliably when the DP changes after the user
/// control's internal bindings have already initialized. Subscribing to the
/// adapter's <see cref="INotifyPropertyChanged"/> directly avoids that.
/// </remarks>
public sealed partial class BrowseAllSection : UserControl
{
    private const double FetchTriggerDistance = 600.0;
    private bool _fetchKicked;
    private double _lastViewportDistance = double.MaxValue;
    private HomeHeroAdapter? _subscribedAdapter;

    public BrowseAllSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe on attach so the handler doesn't accumulate in the WinRT
        // EventSource table across HomePage navigation-cache realizations.
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    public static readonly DependencyProperty AdapterProperty = DependencyProperty.Register(
        nameof(Adapter), typeof(HomeHeroAdapter), typeof(BrowseAllSection),
        new PropertyMetadata(null, (d, _) => ((BrowseAllSection)d).OnAdapterChanged()));

    /// <summary>The home adapter the section binds to. Source for IsBrowseLoading
    /// + BrowseGroups, and the target of <see cref="HomeHeroAdapter.LoadBrowseAsync"/>.</summary>
    public HomeHeroAdapter? Adapter
    {
        get => (HomeHeroAdapter?)GetValue(AdapterProperty);
        set => SetValue(AdapterProperty, value);
    }

    private void OnAdapterChanged()
    {
        // Detach from the previous adapter (if any).
        if (_subscribedAdapter is not null)
            _subscribedAdapter.PropertyChanged -= OnAdapterPropertyChanged;

        _subscribedAdapter = Adapter;

        if (_subscribedAdapter is not null)
            _subscribedAdapter.PropertyChanged += OnAdapterPropertyChanged;

        ApplyLoadingState();
        ApplyGroups();
        // Adapter may have arrived after EffectiveViewportChanged already fired
        // (the page mounts with the section partly in view). Re-check the gate
        // now that we have an adapter to fire the load against.
        TryKickFetch();
    }

    private void OnAdapterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomeHeroAdapter.IsBrowseLoading):
            case nameof(HomeHeroAdapter.BrowseGroups):
                ApplyLoadingState();
                ApplyGroups();
                break;
        }
    }

    private void ApplyLoadingState()
    {
        // Default to "loading" until an adapter is attached and explicitly
        // says otherwise — keeps the shimmer visible from the first frame.
        var loading = Adapter?.IsBrowseLoading ?? true;
        var hasData = (Adapter?.BrowseGroups?.Count ?? 0) > 0;

        // Collapse the whole section if the fetch resolved with no data
        // (network failure, region-locked, empty response). No value in
        // surfacing a "Discover something new" header above empty space.
        Root.Visibility = (loading || hasData) ? Visibility.Visible : Visibility.Collapsed;

        ShimmerHost.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        GroupsRepeater.Visibility = (!loading && hasData) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyGroups()
    {
        GroupsRepeater.ItemsSource = Adapter?.BrowseGroups;
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        _lastViewportDistance = args.BringIntoViewDistanceY;
        TryKickFetch();
    }

    private void TryKickFetch()
    {
        if (_fetchKicked || Adapter is null) return;
        // BringIntoViewDistanceY: 0 means in-view, positive means below viewport.
        // < 600 DIP ≈ within one viewport of being visible.
        if (_lastViewportDistance > FetchTriggerDistance) return;

        _fetchKicked = true;
        _ = Adapter.LoadBrowseAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;

        if (_subscribedAdapter is not null)
        {
            _subscribedAdapter.PropertyChanged -= OnAdapterPropertyChanged;
            _subscribedAdapter = null;
        }
    }
}
