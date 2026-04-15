using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Handles play action prompting (first-time setup + "ask every time" dialog).
/// Called by <see cref="IPlaybackService"/> before executing play commands.
/// Decouples UI dialogs from the playback service layer.
/// </summary>
public interface IPlaybackPromptService
{
    /// <summary>
    /// Resolves what play action to take. May show dialogs if configured.
    /// Returns <see cref="PlayAction.Cancelled"/> if user cancelled.
    /// </summary>
    Task<PlayAction> ResolvePlayActionAsync();
}
