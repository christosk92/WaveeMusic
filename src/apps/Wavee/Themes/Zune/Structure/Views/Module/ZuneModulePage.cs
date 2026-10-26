// ── Themes/Zune/Structure/Views/Module/ZuneModulePage.cs — Zune's `Pages.Module` (themes plan 6.5; P5 ZT7) ────────────────────────────────────
//
// The Common module page, placed on Zune's page column (`ZuneModuleTiers`): the frame's gutter, a centred 2400 column that shares the band's x, a 40 target at Compact and
// no masthead reserve. Everything the page draws (the hero, the sections, the watch stage, the states) is the Common block's, in the style's roles, so the type is Zune's.
//
// Registered by the structure's `CommonBlocks.Register(views, ZuneModulePage.Template, "zune.module.page")` (the registry refuses a second template for the key).

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's module page.</summary>
internal static class ZuneModulePage
{
    /// <summary>The registered template of <see cref="ViewKeys.ModulePage"/>.</summary>
    public static FluentGpu.Dsl.Element Template(ModulePageProps props, in ViewContext ctx)
        => CommonModulePage.Page(props, ZuneModuleTiers.Of(ctx.Tier()), ZuneModuleTiers.TopReserve);
}
