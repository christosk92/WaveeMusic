namespace Wavee.Sdk.Protocol;

/// <summary>Params of <see cref="ModuleMethods.Initialize"/>.</summary>
/// <param name="HostVersion">The app's informational version string.</param>
/// <param name="MinProtocol">Lowest protocol version the host accepts.</param>
/// <param name="MaxProtocol">Highest protocol version the host accepts.</param>
/// <param name="DataDir">The module's private, writable directory.</param>
/// <param name="Locale">BCP-47 locale of the app.</param>
/// <param name="CacheBudgetBytes">On-disk budget for the module; 0 means "not declared".</param>
/// <param name="Prefs">The app's playback preferences.</param>
public sealed record InitializeParams(
    string HostVersion,
    int MinProtocol,
    int MaxProtocol,
    string DataDir,
    string Locale,
    long CacheBudgetBytes,
    ResolvePreferences? Prefs);

/// <summary>Result of <see cref="ModuleMethods.Initialize"/>.</summary>
/// <param name="ProtocolVersion">The version the module will speak; always inside the host's range.</param>
/// <param name="Capabilities">The module's effective capabilities (may be narrower than the manifest's).</param>
/// <param name="Manifest">The module's own manifest, when it could read it from disk.</param>
public sealed record InitializeResult(int ProtocolVersion, string[] Capabilities, ModuleManifest? Manifest);

/// <summary>Params of <see cref="ModuleMethods.Match"/>.</summary>
/// <param name="Input">Trimmed user input, usually a url.</param>
public sealed record MatchParams(string Input);

/// <summary>Params of <see cref="ModuleMethods.Resolve"/>.</summary>
/// <param name="PlayableId">The module-private playable id.</param>
/// <param name="Prefs">The app's playback preferences at the moment of the request.</param>
public sealed record ResolveParams(string PlayableId, ResolvePreferences? Prefs);

/// <summary>Params of <see cref="ModuleMethods.Warm"/>.</summary>
/// <param name="PlayableId">The playable to pre-warm.</param>
public sealed record WarmParams(string PlayableId);

/// <summary>Params of <see cref="ModuleMethods.StreamOpen"/>.</summary>
/// <param name="StreamId">The <see cref="MediaLocator.StreamId"/> that was resolved.</param>
public sealed record StreamOpenParams(string StreamId);

/// <summary>Result of <see cref="ModuleMethods.StreamOpen"/>.</summary>
/// <param name="Handle">Opaque handle used by subsequent reads and the close.</param>
/// <param name="Length">Total length in bytes when known.</param>
/// <param name="Seekable">True when arbitrary offsets are served.</param>
/// <param name="ContentType">MIME type when known.</param>
public sealed record StreamOpenResult(string Handle, long? Length, bool Seekable, string? ContentType);

/// <summary>Params of <see cref="ModuleMethods.StreamRead"/>; answered with a binary frame, not JSON.</summary>
/// <param name="Handle">The handle from <see cref="StreamOpenResult"/>.</param>
/// <param name="Offset">Absolute byte offset to read from.</param>
/// <param name="Count">Maximum number of bytes to return.</param>
public sealed record StreamReadParams(string Handle, long Offset, int Count);

/// <summary>Params of <see cref="ModuleMethods.StreamClose"/>.</summary>
/// <param name="Handle">The handle to release.</param>
public sealed record StreamCloseParams(string Handle);

/// <summary>Params of <see cref="ModuleMethods.Page"/>.</summary>
/// <param name="EntityId">The module-namespaced entity id whose page is wanted, e.g. <c>video:tRsQsTMvPNg</c>.</param>
public sealed record PageParams(string EntityId);

/// <summary>Params of <see cref="ModuleMethods.Action"/>.</summary>
/// <param name="Id">The <see cref="ModuleAction.Id"/> the user pressed.</param>
public sealed record ModuleActionParams(string Id);

/// <summary>Result of <see cref="ModuleMethods.Action"/>.</summary>
/// <param name="Ok">True when the action completed.</param>
/// <param name="Message">Optional detail to show the user.</param>
public sealed record ModuleActionResult(bool Ok, string? Message);

/// <summary>Params of <see cref="ModuleMethods.Log"/>.</summary>
/// <param name="Level">Severity.</param>
/// <param name="Message">The line.</param>
public sealed record LogNotification(ModuleLogLevel Level, string Message);

/// <summary>Params of <see cref="ModuleMethods.Expired"/>.</summary>
/// <param name="PlayableId">The playable whose locator died.</param>
public sealed record ExpiredNotification(string PlayableId);

/// <summary>Params of <see cref="ModuleMethods.Progress"/>.</summary>
/// <param name="Stage">Short stage name.</param>
/// <param name="Percent">0..100; negative means indeterminate.</param>
public sealed record ProgressNotification(string Stage, double Percent);

/// <summary>Params of <see cref="ModuleMethods.CancelRequest"/>.</summary>
/// <param name="Id">The in-flight request id to cancel.</param>
public sealed record CancelParams(long Id);

/// <summary>Params of <see cref="ModuleMethods.AuthToken"/>.</summary>
/// <param name="Provider">Provider key, e.g. <c>"spotify"</c>.</param>
/// <param name="Force">True to force a refresh.</param>
public sealed record AuthTokenParams(string Provider, bool Force);

/// <summary>Params of <see cref="ModuleMethods.AuthContext"/>.</summary>
/// <param name="Provider">Provider key, e.g. <c>"spotify"</c>.</param>
public sealed record AuthContextParams(string Provider);

/// <summary>Params of <see cref="ModuleMethods.SecretsGet"/>.</summary>
/// <param name="Key">Module-private key.</param>
public sealed record SecretGetParams(string Key);

/// <summary>Result of <see cref="ModuleMethods.SecretsGet"/>.</summary>
/// <param name="Value">The protected bytes (base64 on the wire), or null when nothing is stored.</param>
public sealed record SecretGetResult(byte[]? Value);

/// <summary>Params of <see cref="ModuleMethods.SecretsSet"/>.</summary>
/// <param name="Key">Module-private key.</param>
/// <param name="Value">The bytes to protect (base64 on the wire).</param>
public sealed record SecretSetParams(string Key, byte[] Value);

/// <summary>Params of <see cref="ModuleMethods.AudioKey"/> — the AP audio-key exchange the host keeps on its side.</summary>
/// <param name="FileIdHex">The audio file id, hex-encoded (20 bytes).</param>
/// <param name="TrackGidHex">The track/episode gid, hex-encoded (16 bytes).</param>
public sealed record AudioKeyParams(string FileIdHex, string TrackGidHex);

/// <summary>Result of <see cref="ModuleMethods.AudioKey"/>.</summary>
/// <param name="Key">The 16-byte AES key (base64 on the wire).</param>
public sealed record AudioKeyResult(byte[] Key);

/// <summary>The empty params/result value for methods that carry neither.</summary>
public sealed record RpcUnit
{
    /// <summary>The singleton empty value.</summary>
    public static RpcUnit Value { get; } = new();
}
