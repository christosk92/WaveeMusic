namespace Wavee.UI.WinUI.Styles;

/// <summary>
/// Named constants for Fluent / Segoe MDL2 icon codepoints used from C#.
///
/// XAML should inline glyph literals directly because XAML-literal PUA codepoints are stable —
/// but in C# they cause two classes of bugs:
///   (1) source-file encoding churn (editors silently save-as CP-1252 on Windows and mangle PUA),
///   (2) Edit-tool byte-matching fails (PUA characters don't survive tool/clipboard round-trips
///       reliably, so text replacements targeting a line with an inline glyph become unreliable).
/// Centralising the codepoints here — expressed as <c>\uXXXX</c> escapes — sidesteps both.
///
/// When adding a new entry, keep the name aligned with the official Fluent / MDL2 entry name
/// (<see href="https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font"/>).
/// </summary>
public static class FluentGlyphs
{
    // ── Playback ──────────────────────────────────────────────────────────
    /// <summary>Play — E768.</summary>
    public const string Play = "";
    /// <summary>Next — E893 (play-next glyph historically E71A Forward; we keep E71A).</summary>
    public const string PlayNext = "";
    /// <summary>Add (plus) — E710. Used for "Add to queue".</summary>
    public const string AddToQueue = "";
    /// <summary>Shuffle — E8B1.</summary>
    public const string Shuffle = "";

    // ── Library / save state ──────────────────────────────────────────────
    /// <summary>HeartFill — EB52.</summary>
    public const string HeartFilled = "";
    /// <summary>Heart — EB51.</summary>
    public const string HeartOutline = "";
    /// <summary>Pinned — E840.</summary>
    public const string Pin = "";
    /// <summary>Unpin — E77A.</summary>
    public const string Unpin = "";

    // ── Navigation targets ────────────────────────────────────────────────
    /// <summary>Contact — E77B. Used as generic "artist" glyph.</summary>
    public const string Artist = "";
    /// <summary>MusicAlbum — E93C.</summary>
    public const string Album = "";
    /// <summary>MusicNote — E8D6 / Playlist surface E8FD.</summary>
    public const string Playlist = "";
    /// <summary>Folder (closed) — E8B7.</summary>
    public const string Folder = "";
    /// <summary>FolderOpen — E838.</summary>
    public const string FolderOpen = "";
    /// <summary>Radio — EC05.</summary>
    public const string Radio = "";

    // ── Actions ──────────────────────────────────────────────────────────
    /// <summary>Share — E72D.</summary>
    public const string Share = "";
    /// <summary>Delete — E74D.</summary>
    public const string Delete = "";
    /// <summary>Delete — E74D. Semantic alias used for "Remove from playlist" etc.</summary>
    public const string Remove = "";
    /// <summary>Edit — E70F.</summary>
    public const string Edit = "";
    /// <summary>Rename — E8AC.</summary>
    public const string Rename = "";
    /// <summary>Download — E896.</summary>
    public const string Download = "";
    /// <summary>OpenInNewWindow / OpenWith — E8A7.</summary>
    public const string Open = "";
    /// <summary>OpenInNewWindow — E8A7.</summary>
    public const string OpenInNewTab = "";
    /// <summary>MoveToFolder — E8DE.</summary>
    public const string MoveTo = "";
    /// <summary>Add — E710. Semantic alias.</summary>
    public const string Add = "";
    /// <summary>Add — E710.</summary>
    public const string CreatePlaylist = "";
    /// <summary>NewFolder — E8F4.</summary>
    public const string CreateFolder = "";
    /// <summary>Contact — E77B.</summary>
    public const string AddToProfile = "";
    /// <summary>Flag — E814.</summary>
    public const string Report = "";
    /// <summary>Blocked — ECCA.</summary>
    public const string Exclude = "";

    // ── Misc surfaces ─────────────────────────────────────────────────────
    /// <summary>Info — E946. Used for "Show credits".</summary>
    public const string Credits = "";
    /// <summary>Picture — E91B. Used for "Background".</summary>
    public const string Background = "";
    /// <summary>Edit — E70F. Reused for Canvas editor.</summary>
    public const string Canvas = "";
    /// <summary>ChevronRight — E76C.</summary>
    public const string ChevronRight = "";

    // -- Social-link icons -------------------------------------------------
    // Maps a URL/name to a FontAwesome6.EFontAwesomeIcon brand enum (via the
    // FontAwesome6.Svg.WinUI package). FontAwesome's Brands family carries
    // the actual recognised marks (Instagram, YouTube, Spotify, etc.) that
    // the generic Fluent icon set deliberately omits. Centralised here so
    // the resolution lives alongside the rest of the icon vocabulary instead
    // of being duplicated across view-models. Best-effort substring match on
    // hostname; falls back to a generic external-link arrow when nothing
    // matches.

    public static FontAwesome6.EFontAwesomeIcon ResolveSocialIcon(string? url, string? name)
    {
        var lower = (url ?? string.Empty).ToLowerInvariant();
        var n = (name ?? string.Empty).ToLowerInvariant();

        if (lower.Contains("instagram") || n.Contains("instagram"))
            return FontAwesome6.EFontAwesomeIcon.Brands_Instagram;
        if (lower.Contains("twitter") || lower.Contains("x.com") || n.Contains("twitter"))
            return FontAwesome6.EFontAwesomeIcon.Brands_Twitter;
        if (lower.Contains("facebook") || n.Contains("facebook"))
            return FontAwesome6.EFontAwesomeIcon.Brands_FacebookF;
        if (lower.Contains("youtube") || n.Contains("youtube"))
            return FontAwesome6.EFontAwesomeIcon.Brands_Youtube;
        if (lower.Contains("tiktok") || n.Contains("tiktok"))
            return FontAwesome6.EFontAwesomeIcon.Brands_Tiktok;
        if (lower.Contains("soundcloud") || n.Contains("soundcloud"))
            return FontAwesome6.EFontAwesomeIcon.Brands_Soundcloud;
        if (lower.Contains("spotify") || n.Contains("spotify"))
            return FontAwesome6.EFontAwesomeIcon.Brands_Spotify;
        if (lower.Contains("apple.com/music") || n.Contains("apple music")
            || lower.Contains("apple.com") || n.Contains("apple"))
            return FontAwesome6.EFontAwesomeIcon.Brands_Apple;
        return FontAwesome6.EFontAwesomeIcon.Solid_ArrowUpRightFromSquare;
    }
}
