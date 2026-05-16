namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Character-count-tiered font sizing for album / playlist titles. Mirrors
/// Spotify's hero-title scaling — short titles get the maximum size, longer
/// titles step down through 4 buckets so they fit cleanly without runaway
/// wrapping. Cheaper than a SizeChanged-driven measure loop and matches
/// how production music apps ship the same problem.
///
/// <para>
/// Bind via x:Bind function syntax:
/// <c>FontSize="{x:Bind helpers:TitleFontSizer.Hero(ViewModel.AlbumName), Mode=OneWay}"</c>.
/// CJK / emoji widths aren't perfectly measured — those texts trigger one tier
/// earlier than ideal — still readable, just slightly smaller.
/// </para>
/// </summary>
public static class TitleFontSizer
{
    /// <summary>Hero title scaling (AlbumPage / PlaylistPage main title). Range 22 → 40.</summary>
    public static double Hero(string? text) => Length(text) switch
    {
        <= 20 => 40,
        <= 40 => 32,
        <= 70 => 26,
        _     => 22,
    };

    /// <summary>
    /// Matching line-height for <see cref="Hero"/>. 1.1× the font size — enough
    /// vertical headroom so descenders ("y", "p", "g") don't collide with the
    /// next line's ascenders when the title wraps. <c>TextLineBounds="Tight"</c>
    /// + <c>FontWeight="Black"</c> in the consuming TextBlock make the bounding
    /// box compact, so 1.1× is the sweet spot — much tighter and descenders
    /// touch the next line.
    /// </summary>
    public static double HeroLineHeight(string? text) => Hero(text) * 1.1;

    /// <summary>Strip / promo card title (e.g. MusicVideoStrip on AlbumPage). Range 13 → 22.</summary>
    public static double Strip(string? text) => Length(text) switch
    {
        <= 20 => 22,
        <= 40 => 18,
        <= 70 => 15,
        _     => 13,
    };

    /// <summary>
    /// Narrow-column hero (PlaylistPage cover-mode left panel, ~200 px wide).
    /// Top tier caps at 28 px because at 40 px the word "Playlist" already
    /// overflows a 200 px column at Black weight — even a short title like
    /// "My Playlist #2" would break mid-word. Buckets are finer-grained at
    /// the short end since the column constrains long words first.
    /// </summary>
    public static double CompactHero(string? text) => Length(text) switch
    {
        <= 8  => 28,
        <= 15 => 22,
        <= 25 => 18,
        <= 40 => 16,
        <= 70 => 14,
        _     => 12,
    };

    private static int Length(string? text)
        => string.IsNullOrEmpty(text) ? 0 : text.Length;
}
