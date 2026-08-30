namespace Wavee;

/// <summary>The identity-first player-dock pressure tiers, ordered from the 300-DIP floor to the full desktop bar.</summary>
public enum PlayerBarTier : byte
{
    Minimal,
    Compact,
    Medium,
    Comfortable,
    Wide,
    Full,
}

/// <summary>
/// Pure width-to-tier resolver for the player dock. Widening commits immediately; narrowing holds the current
/// presentation through a 24-DIP dip so pointer resize cannot chatter structural controls at a threshold.
/// </summary>
internal static class PlayerBarResponsiveLayout
{
    public const float CompactW = 440f;
    public const float MediumW = 760f;
    public const float ComfortableW = 900f;
    public const float WideW = 1100f;
    public const float FullW = 1240f;
    public const float NarrowHysteresis = 24f;

    public static PlayerBarTier Nominal(float width) =>
        width >= FullW ? PlayerBarTier.Full :
        width >= WideW ? PlayerBarTier.Wide :
        width >= ComfortableW ? PlayerBarTier.Comfortable :
        width >= MediumW ? PlayerBarTier.Medium :
        width >= CompactW ? PlayerBarTier.Compact :
        PlayerBarTier.Minimal;

    public static PlayerBarTier Resolve(float width, PlayerBarTier current, bool initialized)
    {
        if (width <= 0f) return current;
        var nominal = Nominal(width);
        if (!initialized || nominal >= current) return nominal;
        var dipped = Nominal(width + NarrowHysteresis);
        return dipped < current ? dipped : current;
    }
}

/// <summary>The complete structural/metric policy for one player-bar tier.
///
/// <para>HIT-TARGET FLOORS (WinUI minimums, not a tuning preference). A transport button is a pointer AND touch target,
/// and WinUI's control ladder bottoms out at 32×32 (<c>ControlHeight</c>) with a 16-DIP glyph; the primary transport is
/// the bar's one focal control and takes the 40×40 / 20-DIP rung wherever the row has room. The tier table used to ramp
/// the secondaries 32/30/28/26 and the primary 36/36/34/30, so <b>three of six tiers shipped sub-minimum targets</b> —
/// the narrowest window, i.e. exactly where a mis-click costs most. The metrics below are therefore FLAT across the
/// pressure ladder: pressure is absorbed by what the row DROPS (prev/next, shuffle+repeat, the volume slider, the
/// times, the lyrics/queue/expand commands all fall into the "⋯" overflow) and by the spacing rungs
/// (RowGap/RowPad/ClusterGap/LeftGap/SeekGap/RightGap), never by the buttons.</para>
///
/// <para>The 300-DIP floor still balances, because the Minimal tier has already dropped almost everything: 12 row pad +
/// 132 identity block + 6 row gaps + 36 primary + 2 cluster gap + 32 devices + 32 overflow = 252, leaving the SeekBar
/// the ~48 DIP it grows into. That is the honest trade — a short seek line at the absolute floor, never a 26-DIP
/// Next button.</para></summary>
internal readonly record struct PlayerBarLayout(
    PlayerBarTier Tier,
    bool ShowExpand,
    bool ShowDevices,
    bool ShowQueue,
    bool ShowVolumeSlider,
    bool ShowShuffleRepeat,
    bool ShowLikeSlot,
    bool ShowVolumeButton,
    bool ShowLyrics,
    bool ShowRemoteDeviceLine,
    bool ShowTimesElapsed,
    bool ShowTimesRemaining,
    bool ShowPrevNext,
    bool ShowSubtitle,
    float ButtonBox,
    float ButtonGlyph,
    float PrimaryBox,
    float PrimaryGlyph,
    float LeftW,
    float ArtSize,
    float RowGap,
    float RowPad,
    float ClusterGap,
    float LeftGap,
    float SeekGap,
    float RightGap,
    float TopEdgeWidth)
{
    public static PlayerBarLayout Initial(float width)
        => ForTier(PlayerBarResponsiveLayout.Nominal(width));

    public static PlayerBarLayout Resolve(float width, in PlayerBarLayout current, bool initialized)
        => ForTier(PlayerBarResponsiveLayout.Resolve(width, current.Tier, initialized));

    /// <summary>The WinUI hit-target floors every tier honours. <see cref="MinButtonBox"/>/<see cref="MinButtonGlyph"/>
    /// are the standard icon-button rung — <see cref="WaveeSize.ControlH"/>, which is by construction the same value as
    /// <c>WaveeCta.IconButtonSize</c> (row 1 of the icon-button geometry table; named through the size ladder here
    /// because that is the file the layout gate compiles against). The primary takes
    /// <see cref="PrimaryBoxRoomy"/>/<see cref="PrimaryGlyphRoomy"/> from Medium up and never falls below
    /// <see cref="MinPrimaryBox"/>/<see cref="MinPrimaryGlyph"/>.</summary>
    public const float MinButtonBox = WaveeSize.ControlH;           // 32
    public const float MinButtonGlyph = 16f;
    public const float MinPrimaryBox = 36f;
    public const float MinPrimaryGlyph = 18f;
    public const float PrimaryBoxRoomy = 40f;
    public const float PrimaryGlyphRoomy = 20f;

    public static PlayerBarLayout ForTier(PlayerBarTier tier)
    {
        bool full = tier == PlayerBarTier.Full;
        bool wide = tier >= PlayerBarTier.Wide;
        bool comfortable = tier >= PlayerBarTier.Comfortable;
        bool medium = tier >= PlayerBarTier.Medium;
        bool compact = tier >= PlayerBarTier.Compact;

        return new PlayerBarLayout(
            Tier: tier,
            ShowExpand: full,
            ShowDevices: true,          // the device picker is the only route when local playback is unavailable
            ShowQueue: wide,
            ShowVolumeSlider: wide,
            ShowShuffleRepeat: comfortable,
            ShowLikeSlot: true,         // identity-first: the heart survives down to the 300-DIP floor
            ShowVolumeButton: medium,
            ShowLyrics: medium,
            ShowRemoteDeviceLine: comfortable,
            ShowTimesElapsed: medium,
            ShowTimesRemaining: compact,
            ShowPrevNext: medium,
            ShowSubtitle: true,         // title + artist are one indivisible identity block
            // FLAT across the ladder — see the type doc. A secondary transport is a standard 32/16 icon button at every
            // tier including the 300-DIP floor; the primary keeps the roomy 40/20 rung down to Medium and steps to the
            // 36/18 floor only for Compact/Minimal, where the row has genuinely nothing left to drop.
            ButtonBox: MinButtonBox,
            ButtonGlyph: MinButtonGlyph,
            PrimaryBox: medium ? PrimaryBoxRoomy : MinPrimaryBox,
            PrimaryGlyph: medium ? PrimaryGlyphRoomy : MinPrimaryGlyph,
            // The identity block gives back the DIPs the floors took at the two narrow tiers (the like button grew
            // 30→32 and the primary 30→36 there): 180→172 and 140→132 keeps the SeekBar's share of a 440/300 row
            // roughly where it was, and the metadata column still clears the art + gaps + heart.
            LeftW: tier switch
            {
                PlayerBarTier.Full => 260f,
                PlayerBarTier.Wide => 240f,
                PlayerBarTier.Comfortable => 230f,
                PlayerBarTier.Medium => 220f,
                PlayerBarTier.Compact => 172f,
                _ => 132f,
            },
            ArtSize: medium ? WaveeSize.ArtPlayerBar : 40f,
            RowGap: wide ? 8f : medium ? 6f : compact ? 4f : 3f,
            RowPad: wide ? 12f : medium ? 8f : compact ? 8f : 6f,
            ClusterGap: medium ? 4f : compact ? 3f : 2f,
            LeftGap: medium ? 8f : compact ? 6f : 4f,
            SeekGap: medium ? 6f : compact ? 5f : 4f,
            RightGap: medium ? 2f : compact ? 1f : 0f,
            TopEdgeWidth: 2400f);
    }
}
