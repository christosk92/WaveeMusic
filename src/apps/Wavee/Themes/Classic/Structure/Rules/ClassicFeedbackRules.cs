// ── Themes/Classic/Structure/Rules/ClassicFeedbackRules.cs — the numbers of Classic's report dialog body (themes plan 6.1, T11c) ────
//
// What `Feedback.UI.cs` held as file-level constants and inline literals, moved verbatim with the picture: the form gets 500 DIP inside the stock
// 548 card (WinUI ContentDialog padding 24), the free-text boxes' heights, the combo width and the preview box. Data, not decisions (WVTH003 allows
// literals only here, in `Rules\**`).

namespace Wavee.Themes.Classic;

/// <summary>The constants of the report dialog's body.</summary>
internal static class ClassicFeedbackRules
{
    /// <summary>The form's width: the stock dialog's 548 less its 24 padding either side.</summary>
    public const float FieldWidth = 548f - 48f;

    /// <summary>A short free-text box.</summary>
    public const float FieldHeight = 72f;

    /// <summary>The Question / Idea body box.</summary>
    public const float BodyFieldHeight = 140f;

    /// <summary>The preview box's height.</summary>
    public const float PreviewHeight = 220f;

    /// <summary>A dropdown's width.</summary>
    public const float ComboWidth = 260f;

    /// <summary>The subtitle's text size.</summary>
    public const float SubtitleSize = 14f;

    /// <summary>The preview heading's and the note's text size.</summary>
    public const float SmallTextSize = 12f;

    /// <summary>The preview heading's weight.</summary>
    public const int HeadingWeight = 600;

    /// <summary>The preview box's inner padding (all sides).</summary>
    public const float PreviewPadding = 10f;

    /// <summary>The preview text's width inset: the box's padding either side.</summary>
    public const float PreviewTextInset = 20f;
}
