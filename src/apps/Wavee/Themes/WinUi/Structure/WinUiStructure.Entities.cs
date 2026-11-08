// ── Themes/WinUi/Structure/WinUiStructure.Entities.cs — the entities family's registrar (themes plan 6.2; W6b, winui show pages) ─────────────────────────
//
// The episode page (W-Episode, -Compact, -Small, -Huge): the header's slots, the pivot reader, About and the chapters (`Views\Episode\WinUiEpisode.cs`).
// The discussion's keys (the thread, the cards, the reaction flyout) stay the stand-in's: no WinUI board draws a thread.

using FluentGpu.Theming;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterEntities(ViewRegistryBuilder views) => WinUiEpisode.Register(views);
}
