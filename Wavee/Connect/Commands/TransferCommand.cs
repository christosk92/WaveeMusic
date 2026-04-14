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
        // The transfer_state blob is optional — some self-directed or minimal transfer
        // commands (e.g. dealer echoes of from/self/to/self requests) don't carry it.
        // Fall back to an empty TransferState so the parser doesn't crash the handler.
        TransferState transferState;
        if (json.TryGetProperty("transfer_state", out var tsElement) &&
            tsElement.ValueKind == JsonValueKind.String)
        {
            var transferStateB64 = tsElement.GetString();
            var transferStateBytes = Convert.FromBase64String(transferStateB64!);
            transferState = TransferState.Parser.ParseFrom(transferStateBytes);
        }
        else
        {
            transferState = new TransferState();
        }

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
