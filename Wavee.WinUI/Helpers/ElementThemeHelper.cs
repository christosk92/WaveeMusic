using Microsoft.UI.Xaml;

namespace Wavee.WinUI.Helpers;

/// <summary>
/// Helper class for detecting the current UI theme
/// </summary>
public static class ElementThemeHelper
{
    private static bool _cachedIsDarkTheme = false;
    private static bool _cacheInitialized = false;

    /// <summary>
    /// Initializes the theme cache. Should be called from the UI thread at app startup.
    /// </summary>
    public static void InitializeThemeCache()
    {
        try
        {
            // Try to get the actual theme from the main window
            if (Application.Current is App app && app.Window?.Content is FrameworkElement element)
            {
                _cachedIsDarkTheme = element.ActualTheme == ElementTheme.Dark;
            }
            else
            {
                // Fallback to application-level theme setting
                _cachedIsDarkTheme = Application.Current?.RequestedTheme == ApplicationTheme.Dark;
            }
            _cacheInitialized = true;
        }
        catch
        {
            // If initialization fails, default to light theme
            _cachedIsDarkTheme = false;
            _cacheInitialized = true;
        }
    }

    /// <summary>
    /// Determines if the application is currently using dark theme.
    /// Thread-safe - uses cached value if accessed from non-UI thread.
    /// </summary>
    /// <returns>True if dark theme is active, false otherwise</returns>
    public static bool IsDarkTheme()
    {
        // If cache is initialized, return cached value immediately (thread-safe)
        if (_cacheInitialized)
        {
            return _cachedIsDarkTheme;
        }

        try
        {
            // Try to get the actual theme from the main window
            if (Application.Current is App app && app.Window?.Content is FrameworkElement element)
            {
                // Ensure we're on the UI thread when accessing ActualTheme
                if (element.DispatcherQueue?.HasThreadAccess == true)
                {
                    var isDark = element.ActualTheme == ElementTheme.Dark;
                    // Cache the value for future calls
                    _cachedIsDarkTheme = isDark;
                    _cacheInitialized = true;
                    return isDark;
                }
            }
        }
        catch
        {
            // Swallow exceptions during theme detection - will use fallback
        }

        // Return cached value if available (might have been set from another thread)
        if (_cacheInitialized)
        {
            return _cachedIsDarkTheme;
        }

        // Final fallback: assume light theme (safest default)
        return false;
    }
}
