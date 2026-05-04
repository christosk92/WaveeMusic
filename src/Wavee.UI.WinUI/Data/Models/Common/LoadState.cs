namespace Wavee.UI.WinUI.Data.Models.Common;

/// <summary>
/// Represents the loading state for async operations.
/// </summary>
public enum LoadState
{
    /// <summary>
    /// Initial state, no loading has been attempted.
    /// </summary>
    Idle,

    /// <summary>
    /// Currently loading data.
    /// </summary>
    Loading,

    /// <summary>
    /// Data loaded successfully.
    /// </summary>
    Loaded,

    /// <summary>
    /// An error occurred during loading.
    /// </summary>
    Error,

    /// <summary>
    /// Loading completed but no data was found.
    /// </summary>
    Empty
}
