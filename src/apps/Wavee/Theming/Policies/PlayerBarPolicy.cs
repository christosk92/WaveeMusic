// ── Theming/Policies/PlayerBarPolicy.cs — what a structure decides about the player bar (themes plan 5.5) ──────────────
//
// The bar's pressure machine is core: the window width picks one of six pressure levels (`Shell.PlayerBarTier`, Minimal ..
// Full), widening commits at once and narrowing holds through a dip (`Shell.PlayerBar`), and each level lays out the bar
// (`Shell.PlayerBarLayout.ForTier`). Which width starts each level, which slots each level carries and the spacing and art
// of each level are the structure's, and arrive as this value. Deviation from the plan: the plan's `VisibleSlots` is a list
// of strings; here it is a flags enum (`PlayerBarSlots`), so a level's slot set is compared and tested without a string, and
// the level count is the core's six (a structure re-tunes the six, T6 may generalise the vocabulary).
//
// `Default` is the shell before themes (what Wavee Classic declares): 440 / 760 / 900 / 1100 / 1240 with a 24-DIP narrowing
// hold and a 72-DIP bar.

using System.Collections.Immutable;

namespace Wavee.Theming;

/// <summary>The optional parts of the player bar, one bit each. A level carries the parts in its set; a part a level drops moves
/// to the overflow menu where it has a verb there (<c>Shell.PlayerBarRules.Overflow</c>).</summary>
[Flags]
public enum PlayerBarSlots : ushort
{
    /// <summary>Nothing.</summary>
    None = 0,
    /// <summary>The expand (fullscreen stage) button.</summary>
    Expand = 1 << 0,
    /// <summary>The device picker button: the only route to another output, so every level carries it.</summary>
    Devices = 1 << 1,
    /// <summary>The queue button (and the video split button beside it).</summary>
    Queue = 1 << 2,
    /// <summary>The inline volume rail.</summary>
    VolumeSlider = 1 << 3,
    /// <summary>The shuffle and repeat buttons.</summary>
    ShuffleRepeat = 1 << 4,
    /// <summary>The heart beside the track.</summary>
    LikeSlot = 1 << 5,
    /// <summary>The mute / volume button.</summary>
    VolumeButton = 1 << 6,
    /// <summary>The lyrics button.</summary>
    Lyrics = 1 << 7,
    /// <summary>The "playing on ..." line under the track of a remote output.</summary>
    RemoteDeviceLine = 1 << 8,
    /// <summary>The elapsed time beside the seek bar.</summary>
    TimesElapsed = 1 << 9,
    /// <summary>The remaining / total time beside the seek bar.</summary>
    TimesRemaining = 1 << 10,
    /// <summary>The previous and next buttons.</summary>
    PrevNext = 1 << 11,
    /// <summary>The artist line under the title: with the title one indivisible identity block, so every level carries it.</summary>
    Subtitle = 1 << 12,
}

/// <summary>One pressure level of the player bar: where it starts, what it carries and how it spaces it.</summary>
/// <param name="MinWidth">The window width at which this level starts (the first level starts at 0).</param>
/// <param name="VisibleSlots">The parts the level carries.</param>
/// <param name="LeftW">The identity block's width (art + title + heart).</param>
/// <param name="ArtSize">The cover.</param>
/// <param name="RowGap">The gap between the left block, the centre and the right cluster.</param>
/// <param name="RowPad">The row's horizontal padding.</param>
/// <param name="ClusterGap">The gap between transport buttons.</param>
/// <param name="LeftGap">The gap between the cover and the text.</param>
/// <param name="SeekGap">The gap between the seek bar and its time labels.</param>
/// <param name="RightGap">The gap between the right cluster's buttons.</param>
/// <param name="ButtonBox">A secondary button's box (never below the 32-DIP hit-target floor).</param>
/// <param name="ButtonGlyph">A secondary button's glyph (never below 16).</param>
/// <param name="PrimaryBox">The play button's box (never below 36).</param>
/// <param name="PrimaryGlyph">The play button's glyph (never below 18).</param>
public sealed record PlayerTier(
    float MinWidth, PlayerBarSlots VisibleSlots,
    float LeftW, float ArtSize, float RowGap, float RowPad, float ClusterGap, float LeftGap, float SeekGap, float RightGap,
    float ButtonBox, float ButtonGlyph, float PrimaryBox, float PrimaryGlyph);

/// <summary>The player bar's policy.</summary>
/// <param name="Tiers">Exactly <see cref="TierCount"/> levels, narrowest first, each starting wider than the one before.</param>
/// <param name="Hysteresis">Narrowing holds the current level through this many DIP of dip, so a pointer resize cannot chatter
/// structural controls at a threshold. Widening commits at once.</param>
/// <param name="Height">The bar's height. The page bottom reserve (<c>Design.Dock.Reserve</c>) still carries the Classic value
/// until the page paddings follow the policy (T4/T6).</param>
public sealed record PlayerBarPolicy(ImmutableArray<PlayerTier> Tiers, float Hysteresis, float Height)
{
    /// <summary>The six pressure levels of the core vocabulary (<c>Shell.PlayerBarTier</c>).</summary>
    public const int TierCount = 6;

    /// <summary>The shell before themes.</summary>
    public static PlayerBarPolicy Default { get; } = new(
        [
            // Minimal: the 300-DIP floor. Identity-first, flat hit targets, nothing optional.
            new PlayerTier(0f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle,
                LeftW: 132f, ArtSize: 40f, RowGap: 3f, RowPad: 6f, ClusterGap: 2f, LeftGap: 4f, SeekGap: 4f, RightGap: 0f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f),
            // Compact: the remaining time joins.
            new PlayerTier(440f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.TimesRemaining,
                LeftW: 172f, ArtSize: 40f, RowGap: 4f, RowPad: 8f, ClusterGap: 3f, LeftGap: 6f, SeekGap: 5f, RightGap: 1f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f),
            // Medium: the 48 cover, the roomy play button, previous / next, elapsed time, lyrics and the volume button.
            new PlayerTier(760f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext,
                LeftW: 220f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f),
            // Comfortable: shuffle / repeat and the remote-device line join.
            new PlayerTier(900f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine,
                LeftW: 230f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f),
            // Wide: the queue and the inline volume rail join; the row breathes (gap 8, pad 12).
            new PlayerTier(1100f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VolumeSlider,
                LeftW: 240f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f),
            // Full: the expand button joins; the widest identity block.
            new PlayerTier(1240f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VolumeSlider
                    | PlayerBarSlots.Expand,
                LeftW: 260f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f),
        ],
        Hysteresis: 24f, Height: Design.Size.PlayerBarH);

    /// <summary>The level (0 = narrowest) a window width belongs to before any narrowing hold: the widest level whose start
    /// the width has reached. A level never starts below the first one.</summary>
    public int LevelOf(float width)
    {
        for (int i = Tiers.Length - 1; i > 0; i--)
            if (width >= Tiers[i].MinWidth) return i;
        return 0;
    }

    /// <summary>Value equality over the levels (an <see cref="ImmutableArray{T}"/> compares by reference otherwise), so a
    /// bundle rebuilt with the same levels is not a change when it is republished.</summary>
    public bool Equals(PlayerBarPolicy? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || Hysteresis != other.Hysteresis || Height != other.Height) return false;
        if (Tiers.IsDefault || other.Tiers.IsDefault) return Tiers.IsDefault && other.Tiers.IsDefault;
        if (Tiers.Length != other.Tiers.Length) return false;
        for (int i = 0; i < Tiers.Length; i++)
            if (!Tiers[i].Equals(other.Tiers[i])) return false;
        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Hysteresis);
        hash.Add(Height);
        if (!Tiers.IsDefault)
            foreach (var tier in Tiers) hash.Add(tier);
        return hash.ToHashCode();
    }

    /// <summary>Everything wrong with the policy, empty when it can drive the bar.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Tiers.IsDefault || Tiers.Length != TierCount)
        {
            problems.Add($"PlayerBar.Tiers must hold exactly {TierCount} levels");
            return problems;
        }
        if (!float.IsFinite(Hysteresis) || Hysteresis < 0f) problems.Add($"PlayerBar.Hysteresis must be zero or more and finite (was {Hysteresis})");
        if (!float.IsFinite(Height) || Height <= 0f) problems.Add($"PlayerBar.Height must be positive and finite (was {Height})");
        for (int i = 0; i < Tiers.Length; i++)
        {
            var t = Tiers[i];
            string at = $"PlayerBar.Tiers[{i}]";
            if (t is null) { problems.Add(at + " is missing"); continue; }
            if (i == 0 ? t.MinWidth != 0f : !(t.MinWidth > Tiers[i - 1]?.MinWidth) || !float.IsFinite(t.MinWidth))
                problems.Add(i == 0 ? at + ".MinWidth must be 0" : at + ".MinWidth must exceed the level before it");
            if ((t.VisibleSlots & (PlayerBarSlots.Devices | PlayerBarSlots.Subtitle)) != (PlayerBarSlots.Devices | PlayerBarSlots.Subtitle))
                problems.Add(at + " must carry the Devices and Subtitle slots (the output route and the identity block)");
            float[] metrics = [t.LeftW, t.ArtSize, t.RowGap, t.RowPad, t.ClusterGap, t.LeftGap, t.SeekGap, t.RightGap];
            foreach (float m in metrics)
                if (!float.IsFinite(m) || m < 0f) { problems.Add(at + " has a negative or non-finite metric"); break; }
            if (!(t.ButtonBox >= Shell.PlayerBarLayout.MinButtonBox) || !(t.ButtonGlyph >= Shell.PlayerBarLayout.MinButtonGlyph)
                || !(t.PrimaryBox >= Shell.PlayerBarLayout.MinPrimaryBox) || !(t.PrimaryGlyph >= Shell.PlayerBarLayout.MinPrimaryGlyph))
                problems.Add(at + " has a hit target below the 32 / 36 DIP floors");
        }
        return problems;
    }
}
