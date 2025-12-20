using System;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Wavee.WinUI.Services;

/// <summary>
/// Service for handling page navigation within the app.
/// </summary>
public class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private Frame? _frame;

    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogDebug("NavigationService constructed");
    }

    /// <inheritdoc/>
    public Frame? Frame => _frame;

    /// <inheritdoc/>
    public bool CanGoBack => _frame?.CanGoBack ?? false;

    /// <inheritdoc/>
    public void Initialize(Frame frame)
    {
        _logger.LogInformation("Initializing NavigationService with Frame");
        _frame = frame;
        _frame.Navigated += OnNavigated;
        _logger.LogDebug("NavigationService initialized, Navigated event subscribed");
    }

    /// <inheritdoc/>
    public bool Navigate<T>(object? parameter = null) where T : Page
    {
        return Navigate(typeof(T), parameter);
    }

    /// <inheritdoc/>
    public bool Navigate(Type pageType, object? parameter = null)
    {
        if (_frame == null)
        {
            _logger.LogWarning("Navigation failed: Frame is not initialized (Target: {PageType})", pageType.Name);
            return false;
        }

        // Don't navigate if we're already on the target page
        if (_frame.Content?.GetType() == pageType)
        {
            _logger.LogDebug("Navigation skipped: Already on page {PageType}", pageType.Name);
            return false;
        }

        _logger.LogInformation("Navigating to {PageType} (Parameter: {HasParameter})",
            pageType.Name,
            parameter != null);

        var result = _frame.Navigate(pageType, parameter);

        if (!result)
        {
            _logger.LogWarning("Navigation to {PageType} returned false", pageType.Name);
        }

        return result;
    }

    /// <inheritdoc/>
    public bool GoBack()
    {
        if (_frame == null || !_frame.CanGoBack)
        {
            _logger.LogDebug("GoBack failed: Frame is {FrameState} or cannot go back",
                _frame == null ? "null" : "initialized");
            return false;
        }

        _logger.LogInformation("Navigating back");
        _frame.GoBack();
        return true;
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        _logger.LogInformation("Navigation completed: {PageType} (Mode: {NavigationMode})",
            e.SourcePageType.Name,
            e.NavigationMode);
    }
}
