namespace Wavee.Connect.Commands;

/// <summary>
/// Result codes for command execution (maps to dealer reply success codes).
/// </summary>
public enum CommandResult
{
    /// <summary>Command executed successfully.</summary>
    Success = 1,

    /// <summary>Device not found or inactive.</summary>
    DeviceNotFound = 2,

    /// <summary>Context player error (invalid context, unavailable track, etc.).</summary>
    ContextPlayerError = 3,

    /// <summary>Device does not support this command.</summary>
    DeviceDoesNotSupportCommand = 4,

    /// <summary>Upstream error (Spotify API failure).</summary>
    UpstreamError = 5,

    /// <summary>Device disappeared during command execution.</summary>
    DeviceDisappeared = 6,

    /// <summary>Command rate limited or generic failure.</summary>
    CommandFailed = 7
}
