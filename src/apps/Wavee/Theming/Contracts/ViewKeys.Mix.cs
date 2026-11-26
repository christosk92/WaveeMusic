// ── Theming/Contracts/ViewKeys.Mix.cs — the Mix transition chips and editor (#48 Mix) ───────────────────────────────────────────
//
// A mixed playlist shows each pair's transition INLINE, as Spotify does: a chip on a thin connector between the two rows ("Auto", "Fade",
// "Custom", "No transition"), drawn by each theme from `MixSeamProps` (the core's `MixSeamLane` decides what it says). The chip opens the
// pair's editor ("Edit transition") in the right panel, which expands to the full page. The core host (`MixEditorHost`) keeps the draft,
// the undo stack, the snapping and the saves; each theme draws it in its own idiom from `MixEditorProps`.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The transition editor of one pair, in the right panel or full-page. Mounted by `MixEditorHost` (Entities/Mix.Editor.Host.cs).
    /// Props: <see cref="MixEditorProps"/>.</summary>
    public static readonly ViewKey<MixEditorProps> MixEditor = new("Playlist.MixEditor");

    /// <summary>The transition chip between two rows of a mixed playlist, on its connector. Mounted by `MixSeamLane` (Entities/Mix.Seams.cs).
    /// Props: <see cref="MixSeamProps"/>.</summary>
    public static readonly ViewKey<MixSeamProps> MixSeam = new("Playlist.MixSeam");

    /// <summary>The Mix lesson's page, "Build a transition together": the editor in its teaching mode (placed as given) and the guide.
    /// Mounted by `MixLessonPage` (Entities/Mix.Lesson.Host.cs). Props: <see cref="MixLessonProps"/>.</summary>
    public static readonly ViewKey<MixLessonProps> MixLesson = new("Mix.Lesson");
}
