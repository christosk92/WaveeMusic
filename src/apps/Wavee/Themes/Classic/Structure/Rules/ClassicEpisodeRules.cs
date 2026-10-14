// ── Themes/Classic/Structure/Rules/ClassicEpisodeRules.cs — the numbers of Classic's episode page, reader and discussion (themes plan 6.1, T9c) ──
//
// What `Entities\Episode.Page.cs`, `Episode.Reader.cs` and `Episode.Discussion.cs` wrote as literals beside their pictures, moved verbatim with
// them: the reader's measure and transcript height, the chapter row's stamp, the comment grid's avatars and type, the reaction pill's emoji
// cluster and tints, the palette and the flyout's clamp. They are data, not decisions: the views under `Views\Episode` read them (WVTH003
// allows literals only here, in `Rules\**`).

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's episode family.</summary>
internal static class ClassicEpisodeRules
{
    // ── the reader ──

    /// <summary>The reading measure of a section, of the chapters and of a comment thread.</summary>
    public const float ReaderMeasure = 760f, ThreadMeasure = 760f;

    /// <summary>The transcript section's height (its view scrolls inside the page's scroll).</summary>
    public const float TranscriptHeight = 560f;

    /// <summary>The reader scroll's edge fade band.</summary>
    public const float EdgeFadeBand = 24f;

    /// <summary>The rail's About block clamps to this many lines.</summary>
    public const int RailAboutLines = 4;

    // ── a chapter row ──

    /// <summary>The fixed-width timestamp (and the playing row's equalizer beside it) and its type size.</summary>
    public const float ChapterStampWidth = 64f, ChapterStampSize = 13f;

    /// <summary>The chapter title's rung.</summary>
    public const float ChapterTitleSize = 14f, ChapterTitleLine = 20f;

    // ── the show rating popover ──

    /// <summary>The popover's width and its stars' size.</summary>
    public const float RatingWidth = 250f, RatingStar = 24f;

    // ── a comment ──

    /// <summary>The avatar of a top-level comment and of a reply.</summary>
    public const float ThreadAvatar = 32f, ReplyAvatar = 24f;

    /// <summary>The comment body's rung.</summary>
    public const float CommentSize = 14f, CommentLine = 21f;

    /// <summary>The name line: the author (13 / 20) and the date (12), both on a 20 line.</summary>
    public const float AuthorSize = 13f, DateSize = 12f, AuthorLine = 20f;

    /// <summary>The reaction pill's emoji, their overlap, and the label's rung.</summary>
    public const float PillEmojiSize = 14f, PillEmojiLine = 16f, PillEmojiOverlap = -4f, PillSize = 12f, PillLine = 16f;

    /// <summary>The pill's accent fill at rest, hovered and pressed when the viewer has reacted.</summary>
    public const float PillPressedAlpha = 0.22f, PillPressedHoverAlpha = 0.32f, PillPressedDownAlpha = 0.16f;

    /// <summary>The replies disclosure: the stacked repliers' avatar and overlap, its rung and its chevron.</summary>
    public const float ReplyStackAvatar = 20f, ReplyStackOverlap = -7f, DisclosureSize = 13f, DisclosureLine = 18f, DisclosureChevron = 12f;

    // ── the composer ──

    /// <summary>The composer's text box height.</summary>
    public const float ComposerHeight = 80f;

    /// <summary>The consent label: 12 on an 18 line, up to three lines.</summary>
    public const float ConsentSize = 12f, ConsentLine = 18f;

    /// <summary>The consent label's line cap.</summary>
    public const int ConsentLines = 3;

    // ── the reaction flyout ──

    /// <summary>The palette's columns and its buttons.</summary>
    public const int PaletteColumns = 4;

    /// <summary>A palette button.</summary>
    public const float PaletteButtonWidth = 56f, PaletteButtonHeight = 36f;

    /// <summary>The flyout's size and how far it keeps from the window's edges.</summary>
    public const float FlyoutWidth = 320f, FlyoutHeight = 420f, FlyoutMarginX = 24f, FlyoutMarginY = 32f;

    /// <summary>The flyout's title and error rungs.</summary>
    public const float FlyoutTitleSize = 14f, FlyoutErrorSize = 12f;

    /// <summary>A person of the roster: avatar, name rung and emoji rung.</summary>
    public const float PersonAvatar = 28f, PersonSize = 13f, PersonEmojiSize = 18f;
}
