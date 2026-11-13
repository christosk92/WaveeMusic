// ── Themes/FluentV8/Structure/FluentV8Structure.cs — the FluentV8 structure: a few v8 views over a base structure (SPIKE) ─────────────────
//
// A throwaway prototype. FluentV8 draws only the views where Fluent UI React v8's ARRANGEMENT differs from the base structure's (the
// composition hands it WinUI's, `FluentV8Theme.Create`); every other key, the options, the policies and the capabilities are the base's. The
// structure cannot name WinUI (WVTH001: a structure references only its own theme and Common), so the base arrives as an `IStructureLayer`.

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.FluentV8;

/// <summary>The structure of the FluentV8 spike theme.</summary>
public sealed partial class FluentV8Structure : IStandInStructure
{
    /// <summary>The structure id.</summary>
    public const string LayerId = "FluentV8.Structure";

    private readonly IStructureLayer _base;

    /// <summary>FluentV8's views over <paramref name="baseStructure"/>.</summary>
    public FluentV8Structure(IStructureLayer baseStructure) => _base = baseStructure ?? throw new ArgumentNullException(nameof(baseStructure));

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public bool UsesStandIn => true;

    /// <inheritdoc />
    public void RegisterOwnViews(ViewRegistryBuilder views) => RegisterOwn(views);

    /// <inheritdoc />
    public void Register(ViewRegistryBuilder views)
    {
        ArgumentNullException.ThrowIfNull(views);
        RegisterOwn(views);
        var fallback = new ViewRegistryBuilder();
        _base.Register(fallback);
        var built = fallback.Build();
        // The track table: the base rows, command bar and cards under the v8 DetailsHeader.
        if (built.TryGetStyle(StyleKeys.TrackTable, out var table)) views.AddStyle(StyleKeys.TrackTable, FluentV8DetailsHeader.Over(table));
        views.AddMissing(built);
    }

    /// <summary>FluentV8's own views.</summary>
    public static void RegisterOwn(ViewRegistryBuilder views)
    {
        ArgumentNullException.ThrowIfNull(views);
        RegisterViews(views);
    }

    /// <summary>The v8 views (each in its own file under <c>Structure\Views</c>).</summary>
    static partial void RegisterViews(ViewRegistryBuilder views);

    /// <inheritdoc />
    public ThemeOptionSchema Options => _base.Options;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => _base.Policies(structure, metrics);

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => _base.Capabilities(structure);

    /// <inheritdoc />
    public RoleSet RequiredRoles => _base.RequiredRoles;

    /// <inheritdoc />
    public IReadOnlyList<LegacyOptionRow> LegacyRows => _base.LegacyRows;
}
