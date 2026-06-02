namespace Wavee.UI.WinUI.Controls.ImageEditor;

/// <summary>
/// Configuration for <see cref="ImageReframeDialog"/>. Kept source-agnostic so the editor can be
/// reused for any square-image upload, not just playlist covers.
/// </summary>
public sealed class ImageReframeOptions
{
    /// <summary>Side length, in pixels, of the square JPEG the editor produces. Spotify caps
    /// playlist covers at 640×640.</summary>
    public int OutputSide { get; init; } = 640;

    /// <summary>Dialog title.</summary>
    public string Title { get; init; } = "Change cover photo";

    /// <summary>Primary (confirm) button text.</summary>
    public string PrimaryButtonText { get; init; } = "Set photo";
}
