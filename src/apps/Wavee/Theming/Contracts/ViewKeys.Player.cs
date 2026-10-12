// ── Theming/Contracts/ViewKeys.Player.cs — the Player family of published view keys (themes plan 6.0, T6a) ───────────────
//
// T6a publishes the first key of the Player family: the player bar. The rest of the family (device picker, on-media transport,
// now-playing hero, deck flyout) arrives with T6b.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The player bar: the dock under the content with the now-playing identity, the transport and seek rail, and the
    /// right cluster. Mounted by the frame. Props: <see cref="PlayerBarProps"/>.</summary>
    public static readonly ViewKey<PlayerBarProps> PlayerBar = new("Player.Bar");
}
