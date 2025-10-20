namespace Wavee.Connect.Protocol;

/// <summary>
/// Dealer message type.
/// </summary>
public enum MessageType : byte
{
    Unknown = 0,
    Ping = 1,
    Pong = 2,
    Message = 3,
    Request = 4
}

/// <summary>
/// Request result codes for dealer replies.
/// </summary>
public enum RequestResult : byte
{
    Success = 0,
    UnknownSendCommandResult = 1,
    DeviceNotFound = 2,
    ContextPlayerError = 3,
    DeviceDisappeared = 4,
    UpstreamError = 5,
    DeviceDoesNotSupportCommand = 6,
    RateLimited = 7
}
