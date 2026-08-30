using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Sdk.Protocol;

/// <summary>
/// One JSON-RPC 2.0 envelope, in either direction. A request has <see cref="Method"/> and <see cref="Id"/>;
/// a notification has <see cref="Method"/> only; a response has <see cref="Id"/> plus exactly one of
/// <see cref="Result"/> / <see cref="Error"/>.
/// </summary>
public sealed class JsonRpcMessage
{
    /// <summary>Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>Request/response correlation id. Positive from the host, negative from the module.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    /// <summary>Method name for requests and notifications; null on a response.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>Raw params, deserialized by the registered handler with its own source-generated type info.</summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    /// <summary>Raw result, deserialized by the waiting caller with its own source-generated type info.</summary>
    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    /// <summary>The failure, when the request failed.</summary>
    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

/// <summary>The <c>error</c> member of a JSON-RPC response.</summary>
/// <param name="Code">Numeric code: a <see cref="ModuleErrorCode"/> value, or a JSON-RPC reserved code.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Data">Typed detail, when the failure came from a module.</param>
public sealed record JsonRpcError(int Code, string Message, JsonRpcErrorData? Data);

/// <summary>The typed <c>error.data</c> payload.</summary>
/// <param name="Kind">The typed failure reason.</param>
/// <param name="RetryAfterMs">How long to wait before retrying, when the module knows.</param>
/// <param name="Detail">Machine-readable detail (an upstream status token, an http code, …).</param>
public sealed record JsonRpcErrorData(ModuleErrorCode? Kind, int? RetryAfterMs, string? Detail);

/// <summary>The JSON-RPC reserved error codes this protocol uses.</summary>
public static class JsonRpcErrorCodes
{
    /// <summary>Malformed JSON.</summary>
    public const int ParseError = -32700;

    /// <summary>The envelope was not a valid request.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>Unknown method. The host reads this as "capability absent", never as a failure.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>The params did not deserialize.</summary>
    public const int InvalidParams = -32602;

    /// <summary>An unclassified handler failure.</summary>
    public const int InternalError = -32603;

    /// <summary>The request was cancelled (LSP's convention).</summary>
    public const int RequestCancelled = -32800;
}

/// <summary>A JSON-RPC failure that is not a typed <see cref="ModuleException"/> (e.g. an unknown method).</summary>
/// <param name="code">The numeric JSON-RPC code.</param>
/// <param name="message">The message from the peer.</param>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    /// <summary>The numeric JSON-RPC code; <see cref="JsonRpcErrorCodes.MethodNotFound"/> means "capability absent".</summary>
    public int Code => code;

    /// <summary>The typed detail, when the peer sent any.</summary>
    public JsonRpcErrorData? ErrorData { get; init; }
}
