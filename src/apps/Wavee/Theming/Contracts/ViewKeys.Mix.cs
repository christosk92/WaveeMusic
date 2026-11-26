// ── Theming/Contracts/ViewKeys.Mix.cs — the Mix transition editor (#48 Mix M6) ────────────────────────────────────────────────
//
// The editor of one pair of a mixed playlist, opened under the Transitions list (a seam's click, a Transitions row, the Mix menu's
// Transitions). The core host (`MixEditorHost`) keeps the draft, the undo stack, the snapping and the saves; each theme draws it in its
// own idiom from `MixEditorProps`.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The transition editor of one pair. Mounted by `MixEditorHost` (Entities/Mix.Editor.Host.cs). Props: <see cref="MixEditorProps"/>.</summary>
    public static readonly ViewKey<MixEditorProps> MixEditor = new("Playlist.MixEditor");
}
