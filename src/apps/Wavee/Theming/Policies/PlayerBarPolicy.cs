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
// hold and a 72-DIP bar. T6a: each level also states what the PODCAST bar carries (`PodcastSlots`, `PodcastLeftW`), which is what
// `Shell.PodcastBarLayout` used to derive from the level ordinals.
//
// PER WINDOW TIER (plan 3.2: `PlayerBarPolicy.Tiers` keyed by `WidthTier`, TC2). The six levels are the structure's SUB-LADDER over the
// window width; `Windows` says which of them each window tier may use (`PlayerBarWindowRule`: the lowest and the highest level). The
// level machine resolves the width through the sub-ladder with its hold, then clamps it to the committed window tier's range, so a level
// that belongs to a tier changes on the crossing frame (animated) and one inside a tier tracks the width as before. `Default` carries no
// window rules (every level in every tier).

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
    /// <summary>The queue button.</summary>
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
    /// <summary>The title and artist text beside the cover. Every level of the music bar carries it; the podcast bar's narrowest
    /// level drops it and shows the cover alone.</summary>
    Title = 1 << 13,
    /// <summary>The video split button (the video toggle and its placement chevron), present only while the track has a video. A
    /// level without it carries the video verb in the overflow menu. Contract 1.1: it used to ride the <see cref="Queue"/> bit.</summary>
    VideoSplit = 1 << 14,
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
/// <param name="PodcastSlots">The parts the PODCAST bar carries at this level, or null for the generic rule (the level's parts without
/// the shuffle and repeat buttons and the inline volume rail: the podcast transport spends that room on its ±10 s steps and the
/// speed button). It is a subset of <paramref name="VisibleSlots"/>. This replaces the level ordinals the podcast pressure map
/// used to compare (previous / next from Wide, the time labels from Comfortable, the heart from Medium).</param>
/// <param name="PodcastLeftW">The identity block's width on the podcast bar at this level, or NaN for <paramref name="LeftW"/>.</param>
public sealed record PlayerTier(
    float MinWidth, PlayerBarSlots VisibleSlots,
    float LeftW, float ArtSize, float RowGap, float RowPad, float ClusterGap, float LeftGap, float SeekGap, float RightGap,
    float ButtonBox, float ButtonGlyph, float PrimaryBox, float PrimaryGlyph,
    PlayerBarSlots? PodcastSlots = null, float PodcastLeftW = float.NaN)
{
    /// <summary>The parts the podcast bar carries at this level.</summary>
    public PlayerBarSlots PodcastSet
        => PodcastSlots ?? (VisibleSlots & ~(PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.VolumeSlider));
}

/// <summary>Which levels of the player bar one window tier may use.</summary>
/// <param name="MinLevel">The lowest level (0 = Minimal) the tier lays the bar out at.</param>
/// <param name="MaxLevel">The highest level (5 = Full) the tier lays the bar out at.</param>
public readonly record struct PlayerBarWindowRule(int MinLevel, int MaxLevel)
{
    /// <summary>Every level: the rule of a structure that declares none.</summary>
    public static readonly PlayerBarWindowRule Any = new(0, PlayerBarPolicy.TierCount - 1);

    /// <summary><paramref name="level"/> brought inside the rule's range.</summary>
    public int Clamp(int level) => Math.Clamp(level, MinLevel, Math.Max(MinLevel, MaxLevel));
}

/// <summary>The player bar's policy.</summary>
/// <param name="Tiers">Exactly <see cref="TierCount"/> levels, narrowest first, each starting wider than the one before.</param>
/// <param name="Hysteresis">Narrowing holds the current level through this many DIP of dip, so a pointer resize cannot chatter
/// structural controls at a threshold. Widening commits at once.</param>
/// <param name="Height">The bar's height. The page bottom reserve follows it (T6a): <c>R.Space.DockReserve</c> answers this height, the style
/// table's own <c>SpaceRoles.DockReserve</c> being the value it equals in Classic (72).</param>
public sealed record PlayerBarPolicy(ImmutableArray<PlayerTier> Tiers, float Hysteresis, float Height)
{
    /// <summary>The six pressure levels of the core vocabulary (<c>Shell.PlayerBarTier</c>).</summary>
    public const int TierCount = 6;

    /// <summary>The shell before themes.</summary>
    public static PlayerBarPolicy Default { get; } = new(
        [
            // Minimal: the 300-DIP floor. Identity-first, flat hit targets, nothing optional.
            new PlayerTier(0f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title,
                LeftW: 132f, ArtSize: 40f, RowGap: 3f, RowPad: 6f, ClusterGap: 2f, LeftGap: 4f, SeekGap: 4f, RightGap: 0f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.Subtitle, PodcastLeftW: 40f),
            // Compact: the remaining time and the queue join (the queue stays from here up; its room comes from the seek bar).
            new PlayerTier(440f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining,
                LeftW: 172f, ArtSize: 40f, RowGap: 4f, RowPad: 8f, ClusterGap: 3f, LeftGap: 6f, SeekGap: 5f, RightGap: 1f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue, PodcastLeftW: 120f),
            // Medium: the 48 cover, the roomy play button, previous / next, elapsed time, lyrics and the volume button.
            new PlayerTier(760f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext,
                LeftW: 220f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.VolumeButton
                    | PlayerBarSlots.Lyrics),
            // Comfortable: shuffle / repeat and the remote-device line join.
            new PlayerTier(900f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine,
                LeftW: 230f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.RemoteDeviceLine),
            // Wide: the inline volume rail and the video split join; the row breathes (gap 8, pad 12).
            new PlayerTier(1100f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit | PlayerBarSlots.VolumeSlider,
                LeftW: 240f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit),
            // Full: the expand button joins; the widest identity block.
            new PlayerTier(1240f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit | PlayerBarSlots.VolumeSlider
                    | PlayerBarSlots.Expand,
                LeftW: 260f, ArtSize: Design.Size.ArtPlayerBar, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit | PlayerBarSlots.Expand),
        ],
        Hysteresis: 24f, Height: Design.Size.PlayerBarH);

    /// <summary>The levels each window tier may use, Compact first (<c>WidthTier</c> order; Huge 4K reads the last rule given). Empty: every
    /// level in every tier.</summary>
    public ImmutableArray<PlayerBarWindowRule> Windows { get; init; } = ImmutableArray<PlayerBarWindowRule>.Empty;

    /// <summary>The window rule of tier index <paramref name="windowTier"/> (a <c>WidthTier</c>; negative: no tier known, every level).</summary>
    public PlayerBarWindowRule WindowRule(int windowTier)
        => Windows.IsDefaultOrEmpty || windowTier < 0 ? PlayerBarWindowRule.Any : Windows[Math.Min(windowTier, Windows.Length - 1)];

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
        bool noWindows = Windows.IsDefaultOrEmpty, otherNoWindows = other.Windows.IsDefaultOrEmpty;
        if (noWindows != otherNoWindows || (!noWindows && !Windows.AsSpan().SequenceEqual(other.Windows.AsSpan()))) return false;
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
        if (!Windows.IsDefaultOrEmpty)
            foreach (var rule in Windows) hash.Add(rule);
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
            if (t.PodcastSlots is { } podcast)
            {
                if ((podcast & ~t.VisibleSlots) != PlayerBarSlots.None)
                    problems.Add(at + ".PodcastSlots must be a subset of VisibleSlots");
                if ((podcast & (PlayerBarSlots.Devices | PlayerBarSlots.Subtitle)) != (PlayerBarSlots.Devices | PlayerBarSlots.Subtitle))
                    problems.Add(at + ".PodcastSlots must carry the Devices and Subtitle slots");
            }
            if (!float.IsNaN(t.PodcastLeftW) && !(float.IsFinite(t.PodcastLeftW) && t.PodcastLeftW >= 0f))
                problems.Add(at + ".PodcastLeftW must be NaN (the level's own) or zero or more");
            if (!(t.ButtonBox >= Shell.PlayerBarLayout.MinButtonBox) || !(t.ButtonGlyph >= Shell.PlayerBarLayout.MinButtonGlyph)
                || !(t.PrimaryBox >= Shell.PlayerBarLayout.MinPrimaryBox) || !(t.PrimaryGlyph >= Shell.PlayerBarLayout.MinPrimaryGlyph))
                problems.Add(at + " has a hit target below the 32 / 36 DIP floors");
        }
        if (!Windows.IsDefaultOrEmpty)
        {
            if (Windows.Length > 5) problems.Add($"PlayerBar.Windows holds {Windows.Length} rules, at most 5 fit WidthTier");
            for (int i = 0; i < Windows.Length; i++)
            {
                var w = Windows[i];
                if (w.MinLevel < 0 || w.MaxLevel >= TierCount || w.MinLevel > w.MaxLevel)
                    problems.Add($"PlayerBar.Windows[{i}] must name levels 0..{TierCount - 1} with MinLevel <= MaxLevel");
            }
        }
        return problems;
    }
}
