using System;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Provides runtime switching between mock and real data modes.
/// </summary>
public interface IDataModeProvider
{
    /// <summary>
    /// Gets or sets whether to use mock data instead of real data.
    /// </summary>
    bool UseMockData { get; set; }

    /// <summary>
    /// Event fired when the data mode changes.
    /// </summary>
    event EventHandler<bool>? DataModeChanged;
}
