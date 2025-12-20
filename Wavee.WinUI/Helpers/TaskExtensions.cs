using System;
using System.Threading.Tasks;

namespace Wavee.WinUI.Helpers;

/// <summary>
/// Extension methods for safer Task handling
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Safely executes a fire-and-forget task with proper exception handling.
    /// This prevents unhandled exceptions from causing application crashes.
    /// </summary>
    /// <param name="task">The task to execute</param>
    /// <param name="onException">Optional callback for handling exceptions</param>
    public static async void SafeFireAndForget(this Task task, Action<Exception>? onException = null)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Task was cancelled - this is expected and safe to ignore
        }
        catch (Exception ex)
        {
            // Log or handle the exception
            onException?.Invoke(ex);

            // Also write to debug output
            System.Diagnostics.Debug.WriteLine($"[SafeFireAndForget] Exception in background task: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[SafeFireAndForget] Stack trace: {ex.StackTrace}");
        }
    }
}
