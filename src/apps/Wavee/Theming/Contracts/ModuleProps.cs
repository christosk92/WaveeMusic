// ── Theming/Contracts/ModuleProps.cs — the props of the module page view (themes plan 6.0, T10b3) ─────────────────────────────
//
// `Modules.PageHost` keeps the decisions: the route parse, the load, the claim of the stage playable, the watch projection and the verbs. The view
// draws the document the `Loadable` carries. The facts a view derives from the document (`WatchPageModel.From`, `Modules.Pages.StagePlayableUri`) are pure
// core rules it calls; what is playing is a subscribing read (`Modules.PlayingModuleUri`), so the view re-runs when the bar's item changes.
// The verbs that name core state a theme may not reference (WVTH002: Shell, Playback, Notify) are the static `Modules` verbs.

using FluentGpu.Signals;
using Wavee.Sdk;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ModulePage"/>. The host builds one per mount; it never compares equal to another instance.</summary>
public sealed record ModulePageProps
{
    /// <inheritdoc />
    public bool Equals(ModulePageProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>The page uri the route addresses ("" when the route does not parse): the keys, the group and the scroll key derive from it.</summary>
    public required string PageUri { get; init; }

    /// <summary>The module id the route names ("" when the route does not parse).</summary>
    public required string ModuleId { get; init; }

    /// <summary>False when the route is not a module page route: the view draws the error vacancy and nothing else.</summary>
    public required bool Parsed { get; init; }

    /// <summary>The label the navigating surface sent (the entity reading's fallback title).</summary>
    public string? RouteArg { get; init; }

    /// <summary>The scroll key of the page's offset (the tab's scope plus the page uri).</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The page's load: Pending (the seed or the cached document), Ready, Failed. Reads subscribe.</summary>
    public required Loadable<ModulePageDoc> Loadable { get; init; }

    /// <summary>Ask the module for the page again (the failed state's Retry).</summary>
    public required Action Retry { get; init; }
}
