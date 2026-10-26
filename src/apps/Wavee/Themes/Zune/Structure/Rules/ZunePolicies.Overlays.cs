// ── Themes/Zune/Structure/Rules/ZunePolicies.Overlays.cs — what Zune decides for the overlays the core composes (themes plan 5.5, 6.3; P5 Z8a) ───────
//
// The overlay hook of `ZunePolicies` (see its header): `ZuneOverlayTiers.Policy`, one `OverlayLayout` per window band.
//
//   · DIALOGS: the engine's ContentDialog card at the board's 548, 16 from the window's edges; at Compact it fills the window less 16 with 40-DIP command targets; at Small the form
//     dialogs (report, crash) take the two-pane 752 card, 24 from the window's edges (`OverlayPolicy.Small`, ZT8); at Huge the 548 plate again (the board draws nothing wider).
//   · TOASTS: at most three (the engine lane's cap), bottom right 12 above the bar at the desktop tiers; at Compact the window less 16, bottom centre 12 above the 64 mini bar,
//     the action a 40-DIP word. The slab's width (360) is the control table's.
//   · MENUS: a pointer menu is the engine's menu at the desktop tiers and the context sheet (`Overlay.ContextSheet`) at Compact, its rows 40.
//   · VACANCY: the quiet title and 40-DIP actions at Compact.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void OverlaysPolicy(ZuneParameters parameters, ref OverlayPolicy overlays) => overlays = ZuneOverlayTiers.Policy;
}
