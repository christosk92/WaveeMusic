namespace Wavee.Sdk.Protocol;

/// <summary>Every method name on the wire, in one place, so host and module cannot drift.</summary>
public static class ModuleMethods
{
    // ---- host -> module -------------------------------------------------------------------------------------

    /// <summary>Handshake: version negotiation + the module's effective capabilities.</summary>
    public const string Initialize = "module/initialize";

    /// <summary>Asks the module to wind down; the process exits once it answers.</summary>
    public const string Shutdown = "module/shutdown";

    /// <summary>Does this module own the pasted input?</summary>
    public const string Match = "playback/match";

    /// <summary>Turn a playable id into something the app can play.</summary>
    public const string Resolve = "playback/resolve";

    /// <summary>Fire-and-forget pre-warm.</summary>
    public const string Warm = "playback/warm";

    /// <summary>Open a module-served byte stream.</summary>
    public const string StreamOpen = "stream/open";

    /// <summary>Read a range from a module-served byte stream; answered with a binary frame.</summary>
    public const string StreamRead = "stream/read";

    /// <summary>Close a module-served byte stream.</summary>
    public const string StreamClose = "stream/close";

    /// <summary>Fetch the declarative page a module describes for one of its entity ids.</summary>
    public const string Page = "module/page";

    /// <summary>Generic diagnostics rows for the app's diagnostics page.</summary>
    public const string Diagnostics = "module/diagnostics";

    /// <summary>Invoke a user-pressed <see cref="ModuleAction"/>.</summary>
    public const string Action = "module/action";

    // ---- module -> host -------------------------------------------------------------------------------------

    /// <summary>Notification: a live "now playing" correction.</summary>
    public const string Metadata = "playback/metadata";

    /// <summary>Notification: a resolved locator has expired.</summary>
    public const string Expired = "playback/expired";

    /// <summary>Notification: the module's state changed (drives the setup card).</summary>
    public const string Status = "module/status";

    /// <summary>Notification: progress of a long-running setup step.</summary>
    public const string Progress = "module/progress";

    /// <summary>Notification: one log line.</summary>
    public const string Log = "module/log";

    /// <summary>Request: the host's current bearer token for a provider it owns.</summary>
    public const string AuthToken = "host/auth/token";

    /// <summary>Request: the non-secret session identity of a provider the host owns.</summary>
    public const string AuthContext = "host/auth/context";

    /// <summary>Request: read a per-module secret.</summary>
    public const string SecretsGet = "host/secrets/get";

    /// <summary>Request: write a per-module secret.</summary>
    public const string SecretsSet = "host/secrets/set";

    /// <summary>Request: the Spotify AP audio key for a file. The AP socket stays host-side; this is the one
    /// provider-named host method, implemented by the live Spotify session.</summary>
    public const string AudioKey = "spotify/audioKey";

    // ---- both directions ------------------------------------------------------------------------------------

    /// <summary>Notification: cancel an in-flight request by id (LSP's convention).</summary>
    public const string CancelRequest = "$/cancelRequest";
}

/// <summary>The wire-protocol version this SDK build speaks.</summary>
public static class ModuleProtocol
{
    /// <summary>The newest version this SDK speaks.</summary>
    public const int Version = 1;

    /// <summary>The oldest version this SDK still speaks.</summary>
    public const int MinSupported = 1;

    /// <summary>
    /// Picks the highest version both sides speak, or null when the ranges do not overlap.
    /// </summary>
    /// <param name="hostMin">The host's lowest acceptable version.</param>
    /// <param name="hostMax">The host's highest acceptable version.</param>
    public static int? Negotiate(int hostMin, int hostMax)
    {
        int high = Math.Min(Version, hostMax);
        int low = Math.Max(MinSupported, hostMin);
        return high < low ? null : high;
    }
}
