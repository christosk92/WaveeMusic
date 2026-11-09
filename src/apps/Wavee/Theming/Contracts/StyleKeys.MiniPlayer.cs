// ── Theming/Contracts/StyleKeys.MiniPlayer.cs — the mini player's look (#197) ────────────────────────────────────────────

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The mini player's look (<see cref="MiniPlayerStyle"/>): every theme registers one; the Common template draws with it.</summary>
    public static readonly StyleKey<MiniPlayerStyle> MiniPlayer = new("Player.Mini");
}
