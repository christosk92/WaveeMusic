using System;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services.Data;

/// <summary>
/// Configuration for switching between real and mock data services.
/// </summary>
public sealed class DataServiceConfiguration : IDataServiceConfiguration
{
    private bool _isDemoMode;

    public DataServiceConfiguration(bool startInDemoMode = false)
    {
        _isDemoMode = startInDemoMode;
    }

    public bool IsDemoMode => _isDemoMode;

    public void SetDemoMode(bool enabled)
    {
        if (_isDemoMode != enabled)
        {
            _isDemoMode = enabled;
            DemoModeChanged?.Invoke(this, enabled);
        }
    }

    public event EventHandler<bool>? DemoModeChanged;
}
