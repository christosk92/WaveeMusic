namespace Wavee.Connect.Connection;

/// <summary>
/// WebSocket connection state.
/// </summary>
public enum ConnectionState : byte
{
    /// <summary>
    /// Not connected.
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// Connection in progress.
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// Connected and active.
    /// </summary>
    Connected = 2
}
