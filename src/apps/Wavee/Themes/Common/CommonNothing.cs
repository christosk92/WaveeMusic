// ── Themes/Common/CommonNothing.cs — "this surface is not drawn" (themes plan 6.0 / 6.5, T11c) ──────────────────────────────────────
//
// `Common.Nothing`: the template a theme registers under a key whose surface it deliberately has no picture for (the surface is unreachable by the
// theme's policy or capability: WinUI's Material is Mica, Zune's crumbs live in the title bar, a theme without Quickplay). An empty, zero-size,
// hit-test-transparent box, so a mount that still happens (a core host mounting the key) draws and captures nothing and logs no "[view-slot] renders
// nothing" line. Classic registers no key through it: it draws every key it publishes.

using FluentGpu.Dsl;
using FluentGpu.Theming;

namespace Wavee.Themes.Common;

/// <summary>The empty template.</summary>
public static class CommonNothing
{
    /// <summary>The template: nothing. Usable under any key: <c>views.Add(key, CommonNothing.Template, CommonNothing.TemplateId)</c>.</summary>
    public static Element Template<TProps>(TProps props, in ViewContext ctx) where TProps : class
        => new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };

    /// <summary>The template id every registration of the empty template shares.</summary>
    public const string TemplateId = "common.nothing";
}
