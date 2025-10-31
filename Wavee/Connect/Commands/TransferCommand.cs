using System.Text.Json;
using Wavee.Connect.Protocol;
using Wavee.Protocol.Transfer;

namespace Wavee.Connect.Commands;

/// <summary>
/// Command to transfer playback from another device.
/// </summary>
public sealed record TransferCommand : ConnectCommand
{
    /// <summary>
    /// Transfer state protobuf payload.
    /// </summary>
    public required TransferState TransferState { get; init; }

    internal static TransferCommand FromJson(DealerRequest request, JsonElement json)
    {
        // Parse base64-encoded protobuf
        var transferStateB64 = json.GetProperty("transfer_state").GetString();
        var transferStateBytes = Convert.FromBase64String(transferStateB64!);
        var transferState = TransferState.Parser.ParseFrom(transferStateBytes);

        return new TransferCommand
        {
            Endpoint = "transfer",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            TransferState = transferState
        };
    }
}
