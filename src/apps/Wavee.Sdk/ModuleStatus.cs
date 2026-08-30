namespace Wavee.Sdk;

/// <summary>
/// A module's self-reported state, pushed as a <c>module/status</c> notification. Drives the app's generic setup
/// card: a module that needs a one-time install or a sign-in says so here instead of owning any UI.
/// </summary>
/// <param name="State"><see cref="Ready"/>, <see cref="NeedsSetup"/> or <see cref="Error"/>.</param>
/// <param name="Message">Human-readable detail shown under the state.</param>
/// <param name="Actions">Buttons the user can press; each maps to a <c>module/action</c> request.</param>
public sealed record ModuleStatus(string State, string? Message, ModuleAction[] Actions)
{
    /// <summary><see cref="State"/> value: the module can serve playback right now.</summary>
    public const string Ready = "ready";

    /// <summary><see cref="State"/> value: the module needs a user-driven setup step first.</summary>
    public const string NeedsSetup = "needsSetup";

    /// <summary><see cref="State"/> value: the module is broken and says why.</summary>
    public const string Error = "error";
}

/// <summary>A user-invokable action a module offers (e.g. "Install runtime", "Retry", "Sign in").</summary>
/// <param name="Id">Module-private action id, echoed back on <c>module/action</c>.</param>
/// <param name="Label">Button label.</param>
/// <param name="RequiresConfirmation">True when the app must confirm before invoking.</param>
/// <param name="ConfirmText">Confirmation body text when <paramref name="RequiresConfirmation"/> is true.</param>
public sealed record ModuleAction(string Id, string Label, bool RequiresConfirmation, string? ConfirmText);

/// <summary>The answer to <c>module/diagnostics</c>: generic rows the app renders on its diagnostics page.</summary>
/// <param name="Sections">One section per topic.</param>
public sealed record DiagnosticsReport(DiagnosticsSection[] Sections)
{
    /// <summary>An empty report; the default for modules that do not override diagnostics.</summary>
    public static DiagnosticsReport Empty { get; } = new([]);
}

/// <summary>One titled block of diagnostics rows; each row is a flat array of cells.</summary>
/// <param name="Title">Section heading.</param>
/// <param name="Rows">Rows, each an array of cells (typically name/value pairs).</param>
public sealed record DiagnosticsSection(string Title, string[][] Rows);
