// ── Themes/SwiftUI/Structure/SwiftUIStructure.cs — the SwiftUI spike structure (SPIKE, not for release) ──────────────────────────────
//
// A thin structure over a BASE structure (the composition hands in WinUI's, itself over Classic's stand-in): it draws its own frame (the
// floating glass sidebar and the glass capsule player, `SwiftUIFrame`) and takes every other view, its options, policies and capabilities from
// the base, key by key (`ViewRegistryBuilder.AddMissing`). A structure may only name its own theme and Common, so the base arrives as a value.

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.SwiftUI;

/// <summary>The SwiftUI spike structure.</summary>
public sealed class SwiftUIStructure : IStandInStructure
{
    /// <summary>The structure id.</summary>
    public const string LayerId = "SwiftUI.Structure";

    private readonly IStructureLayer _base;
    private readonly ViewTemplate<ShellSearchProps> _search;

    /// <summary>The structure over <paramref name="baseStructure"/>; <paramref name="search"/> is the base's search field, which the toolbar
    /// draws narrower (a macOS toolbar's search field, so the borrowed bar's window-centred clusters fit beside the floating sidebar).</summary>
    public SwiftUIStructure(IStructureLayer baseStructure, ViewTemplate<ShellSearchProps> search)
    {
        ArgumentNullException.ThrowIfNull(baseStructure);
        ArgumentNullException.ThrowIfNull(search);
        _base = baseStructure;
        _search = search;
    }

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public bool UsesStandIn => true;

    /// <inheritdoc />
    void IStandInStructure.RegisterOwnViews(ViewRegistryBuilder views) => RegisterOwn(views);

    /// <inheritdoc />
    public void Register(ViewRegistryBuilder views)
    {
        ArgumentNullException.ThrowIfNull(views);
        RegisterOwn(views);
        var fallback = new ViewRegistryBuilder();
        _base.Register(fallback);
        views.AddMissing(fallback.Build());
    }

    private void RegisterOwn(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellFrame, SwiftUIFrame.Template, "swiftui.shell.frame");
        var search = _search;
        views.Add(ViewKeys.ShellSearch,
            (ShellSearchProps p, in ViewContext ctx) => search(p with { FieldWidth = SwiftUIFrameRules.ToolbarSearchWidth(p.FieldWidth) }, in ctx),
            "swiftui.shell.search");
    }

    /// <inheritdoc />
    public ThemeOptionSchema Options => _base.Options;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => _base.Policies(structure, in metrics);

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => _base.Capabilities(structure);

    /// <inheritdoc />
    public RoleSet RequiredRoles => _base.RequiredRoles;

    /// <inheritdoc />
    public IReadOnlyList<LegacyOptionRow> LegacyRows => _base.LegacyRows;
}
