// ── Themes/Classic/Structure/Rules/ClassicFeedbackRules.cs — the numbers of Classic's report dialog body (themes plan 6.1, T11c) ────
//
// What `Feedback.UI.cs` held as file-level constants and inline literals, moved verbatim with the picture: the free-text boxes' heights, the combo width and
// the preview box's padding. The form's WIDTH and everything that differs per window tier (the preview's place, the 40-DIP targets, the Huge second pane) is
// the tier record in `Tiers.Dialogs.cs`. Data, not decisions (WVTH003 allows literals only here, in `Rules\**`).

namespace Wavee.Themes.Classic;

/// <summary>The constants of the report dialog's body.</summary>
internal static class ClassicFeedbackRules
{
    /// <summary>A short free-text box.</summary>
    public const float FieldHeight = 72f;

    /// <summary>The Question / Idea body box.</summary>
    public const float BodyFieldHeight = 140f;

    /// <summary>A dropdown's width.</summary>
    public const float ComboWidth = 260f;

    /// <summary>The subtitle's text size.</summary>
    public const float SubtitleSize = 14f;

    /// <summary>The preview heading's and the note's text size.</summary>
    public const float SmallTextSize = 12f;

    /// <summary>The preview heading's weight.</summary>
    public const int HeadingWeight = 600;

    /// <summary>The Compact preview row's chevron.</summary>
    public const float FoldChevronSize = 12f;

    /// <summary>The preview box's inner padding (all sides).</summary>
    public const float PreviewPadding = 10f;

    /// <summary>The preview text's width inset inside its box: the padding either side.</summary>
    public const float PreviewTextInset = 20f;
}
