namespace Wavee.Sdk;

/// <summary>
/// The one type a module author writes. Everything else — process launch, framing, JSON-RPC ids, cancellation,
/// shutdown — is <see cref="ModuleRunner"/>'s job. A module is stateless-per-request and must never write to
/// <see cref="Console.Out"/> expecting it to be stdout: the runner redirects it to stderr so a stray
/// <c>Console.WriteLine</c> cannot corrupt the protocol channel.
/// </summary>
public abstract partial class WaveeModule
{
    private IModuleHost? _host;

    /// <summary>The app, as this module sees it. Valid from the moment the runner starts the module.</summary>
    /// <exception cref="InvalidOperationException">The module has not been attached to a host yet.</exception>
    protected IModuleHost Host =>
        _host ?? throw new InvalidOperationException("The module is not attached to a host yet.");

    /// <summary>True once a host has been attached (useful in constructors-adjacent setup code).</summary>
    protected bool HasHost => _host is not null;

    /// <summary>Attaches the host. Called exactly once by <see cref="ModuleRunner"/> or <see cref="ModuleTestHost"/>.</summary>
    internal void AttachHost(IModuleHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_host is not null) throw new InvalidOperationException("The module is already attached to a host.");
        _host = host;
    }

    /// <summary>One-time setup. Throw <see cref="ModuleException"/> to fail the handshake with a typed reason.</summary>
    /// <param name="ctx">What the host told us at <c>module/initialize</c>.</param>
    /// <param name="ct">Cancels initialization.</param>
    public virtual ValueTask InitializeAsync(ModuleContext ctx, CancellationToken ct) => default;

    /// <summary>Claims (or declines) a pasted link / text. The default declines everything.</summary>
    /// <param name="input">Trimmed user input, usually a url.</param>
    /// <param name="ct">Cancels the match.</param>
    /// <returns>A match, or null when this module does not own the input.</returns>
    public virtual ValueTask<MatchResult?> MatchAsync(string input, CancellationToken ct)
        => new((MatchResult?)null);

    /// <summary>Turns a module-private playable id into something the app can play.</summary>
    /// <param name="playableId">The id from <see cref="MatchAsync"/> or from a <c>wavee:module:</c> uri.</param>
    /// <param name="ct">Cancels the resolve.</param>
    public abstract ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct);

    /// <summary>
    /// The overload the runner actually dispatches, carrying the app's playback preferences for this request.
    /// The default ignores them and calls <see cref="ResolveAsync(string,CancellationToken)"/>; a module that picks
    /// a quality rung overrides this one instead (and can then implement the two-argument overload by delegating
    /// here with <c>null</c>).
    /// </summary>
    /// <param name="playableId">The id to resolve.</param>
    /// <param name="prefs">The app's preferences, or null when it sent none.</param>
    /// <param name="ct">Cancels the resolve.</param>
    public virtual ValueTask<ResolvedPlayable> ResolveAsync(string playableId, ResolvePreferences? prefs,
        CancellationToken ct) => ResolveAsync(playableId, ct);

    /// <summary>Optional fire-and-forget pre-warm (DNS, token, manifest) for a playable the user is likely to start.</summary>
    /// <param name="playableId">The playable to warm.</param>
    /// <param name="ct">Cancels the warm-up.</param>
    public virtual ValueTask WarmAsync(string playableId, CancellationToken ct) => default;

    /// <summary>Opens a module-served byte stream for a <c>"stream"</c> locator. The default serves nothing.</summary>
    /// <param name="streamId">The <see cref="MediaLocator.StreamId"/> that was resolved.</param>
    /// <param name="ct">Cancels the open.</param>
    /// <returns>The stream, or null when the id is unknown.</returns>
    public virtual ValueTask<IModuleStream?> OpenStreamAsync(string streamId, CancellationToken ct)
        => new((IModuleStream?)null);

    /// <summary>
    /// Describes one of this module's entities as a page the app renders (see <see cref="ModulePageDoc"/>). The
    /// default serves nothing, which is what a module without the <c>pages</c> capability wants.
    /// </summary>
    /// <param name="entityId">A module-namespaced entity id, e.g. <c>video:tRsQsTMvPNg</c> or
    /// <c>channel:examplestreamer</c> — the value a <see cref="ResolvedPlayable.PageEntityId"/>,
    /// a <see cref="ResolvedPlayable.SubtitleEntityId"/> or a <see cref="PageItem.EntityId"/> carried.</param>
    /// <param name="ct">Cancels the fetch.</param>
    /// <returns>The page, or null when this module has nothing to show for that id.</returns>
    public virtual ValueTask<ModulePageDoc?> GetPageAsync(string entityId, CancellationToken ct) => default;

    /// <summary>Rows for the app's diagnostics page. The default reports nothing.</summary>
    /// <param name="ct">Cancels the call.</param>
    public virtual ValueTask<DiagnosticsReport> GetDiagnosticsAsync(CancellationToken ct)
        => new(DiagnosticsReport.Empty);

    /// <summary>Runs a user-invoked <see cref="ModuleAction"/> (from a status card or the diagnostics page).</summary>
    /// <param name="actionId">The <see cref="ModuleAction.Id"/> the user pressed.</param>
    /// <param name="ct">Cancels the action.</param>
    public virtual ValueTask InvokeActionAsync(string actionId, CancellationToken ct) => default;

    /// <summary>Last chance to flush and release resources before the process exits.</summary>
    /// <param name="ct">Bounded by the host's shutdown grace period.</param>
    public virtual ValueTask ShutdownAsync(CancellationToken ct) => default;
}
