using System;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Configuration for switching between real and mock data services.
/// </summary>
public interface IDataServiceConfiguration
{
    /// <summary>
    /// Gets whether the app is running in demo/offline mode.
    /// </summary>
    bool IsDemoMode { get; }

    /// <summary>
    /// Sets demo mode on/off at runtime.
    /// </summary>
    void SetDemoMode(bool enabled);

    /// <summary>
    /// Event raised when mode changes.
    /// </summary>
    event EventHandler<bool>? DemoModeChanged;
}
