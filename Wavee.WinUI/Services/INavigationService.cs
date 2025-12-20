using System;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.WinUI.Services;

/// <summary>
/// Interface for navigation service that handles page navigation within the app.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Gets the navigation frame.
    /// </summary>
    Frame? Frame { get; }

    /// <summary>
    /// Gets a value indicating whether the navigation service can navigate back.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// Navigates to the specified page type.
    /// </summary>
    /// <typeparam name="T">The page type to navigate to.</typeparam>
    /// <param name="parameter">Optional navigation parameter.</param>
    /// <returns>True if navigation succeeded; otherwise, false.</returns>
    bool Navigate<T>(object? parameter = null) where T : Page;

    /// <summary>
    /// Navigates to the specified page type.
    /// </summary>
    /// <param name="pageType">The type of page to navigate to.</param>
    /// <param name="parameter">Optional navigation parameter.</param>
    /// <returns>True if navigation succeeded; otherwise, false.</returns>
    bool Navigate(Type pageType, object? parameter = null);

    /// <summary>
    /// Navigates back in the navigation history.
    /// </summary>
    /// <returns>True if back navigation succeeded; otherwise, false.</returns>
    bool GoBack();

    /// <summary>
    /// Initializes the navigation service with a frame.
    /// </summary>
    /// <param name="frame">The frame to use for navigation.</param>
    void Initialize(Frame frame);
}
