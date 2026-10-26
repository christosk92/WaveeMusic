// ── Theming/Contracts/ViewKeys.Shell.cs — the Shell family of published view keys (themes plan 6.0, T4a) ─────────────────
//
// T4a publishes the first two keys of the Shell family: the window frame and the content region's states. The rest of the
// family (title bar, search, masthead, material, drawer, palette, ...) arrives with T4b / T4c, the sidebar with T5.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The window frame: the column (chords, title row, content region, player dock) under the material, the stage and
    /// the overlay layers. Mounted by the core frame root. Props: <see cref="ShellFrameProps"/>.</summary>
    public static readonly ViewKey<ShellFrameProps> ShellFrame = new("Shell.Frame");

    /// <summary>The content region's states: a page, an empty body, the not-found page and the offline lane
    /// (<see cref="ContentStateKind"/>). Mounted by the core content host. Props: <see cref="ContentStateProps"/>.</summary>
    public static readonly ViewKey<ContentStateProps> ShellContentState = new("Shell.ContentState");
}
