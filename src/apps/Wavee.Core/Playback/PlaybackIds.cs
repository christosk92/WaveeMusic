using System;
using System.Security.Cryptography;

namespace Wavee.Core;

/// <summary>Anti-fraud ID bundle minted per track load and shared across PutState + gabo planes.</summary>
public sealed class PlaybackIds
{
    public byte[] PlaybackId { get; }
    public byte[] StreamId { get; }
    public byte[] MediaId { get; }
    public string CommandIdHex { get; }
    public string PlaybackIdHex { get; }
    public string StreamIdHex { get; }
    public string SessionId { get; }
    public string InteractionId { get; }
    public string PageInstanceId { get; }

    PlaybackIds(byte[] playbackId, byte[] streamId, byte[] mediaId, string commandIdHex,
        string sessionId, string interactionId, string pageInstanceId)
    {
        PlaybackId = playbackId;
        StreamId = streamId;
        MediaId = mediaId;
        CommandIdHex = commandIdHex;
        PlaybackIdHex = Convert.ToHexString(playbackId).ToLowerInvariant();
        StreamIdHex = Convert.ToHexString(streamId).ToLowerInvariant();
        SessionId = sessionId;
        InteractionId = interactionId;
        PageInstanceId = pageInstanceId;
    }

    public static PlaybackIds Mint(string commandIdHex, byte[]? mediaId = null,
        string? sessionId = null, string? interactionId = null, string? pageInstanceId = null)
    {
        var playback = RandomNumberGenerator.GetBytes(16);
        var stream = RandomNumberGenerator.GetBytes(16);
        return new PlaybackIds(
            playback,
            stream,
            mediaId ?? Array.Empty<byte>(),
            commandIdHex,
            sessionId ?? Guid.NewGuid().ToString("N"),
            interactionId ?? Guid.NewGuid().ToString(),
            pageInstanceId ?? Guid.NewGuid().ToString());
    }

    public static string MintCommandId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

public enum QueueRowKind { Playable, Delimiter, PageMarker }
