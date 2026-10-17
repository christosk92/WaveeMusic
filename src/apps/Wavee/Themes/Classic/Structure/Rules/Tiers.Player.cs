// ── Themes/Classic/Structure/Rules/Tiers.Player.cs — Classic's player bar per window tier (themes plan 3.1, 3.2, 3.6; TC2) ─────────
//
// THE BAR PER WINDOW TIER (boards C-Breakpoints "Player" row, C-PlayerBar, C-PlayerBars, C-Shell-Compact / -Small / -Huge / -Short,
// C-NowPlaying-Compact, the Huge 4K boards):
//
//   Compact   the MINI bar, 56 tall (C-PlayerBar tier="mini"): a 2-DIP progress hairline over the top edge, the identity as ONE 48-tall
//             door (art 40, title, artist) that opens the now-playing sheet, the heart and the play disc, both 40 x 40 (the Compact
//             target floor). Nothing else: the sheet carries the transport, lyrics and the queue.
//   Small     the Medium level (C-Breakpoints: "Medium tier (prev/next, times, volume, lyrics, queue)", drawn at 800): the level sub-ladder
//             CAPPED at Medium, so 900-1007 drops shuffle / repeat into "⋯" as the board says. Below 760 the Medium row does not fit (its
//             fixed parts alone are ~600 DIP), so 528-759 keeps the production Compact level: the board draws Small only at 800 (flagged).
//   Standard  the production sub-ladder, which from 1008 is Comfortable (900) / Wide (1100) / Full (1240): "Full tier (production
//             440/760/900/1100/1240)".
//   Huge      the HUGE form (C-PlayerBar tier="huge", 2560 and 3840): the Full level's parts in three zones, a 360 identity zone (art 56,
//             title, artist and the album line), the transport CENTRED in a column of at most 1040 (shuffle, previous, play, next,
//             repeat, the times and the seek rail) and a 360 right zone (lyrics, video, queue, devices, volume, a 128 volume rail,
//             expand, "⋯"); 16 padding and 16 between the zones.
//   Short     every tier keeps its own bar (C-Shell-Short: "player bar kept at 72" at 800 x 330; the mini bar is Compact's alone).
//
// THE SUB-LADDER (plan 3.2: production's finer ladders stay structure-internal). The six levels start at 0 / 440 / 760 / 900 / 1100 /
// 1240 with a 24-DIP narrowing hold (production's "24 for heroes and player tiers"); the core's level machine walks them with the hold and
// clamps the result to the committed window tier's range (`PlayerBarPolicy.Windows`, from the records below), so a level that belongs to
// a tier changes on the crossing frame (the bar's own MoveMotion / ItemMotion FLIP it) and one inside a tier tracks the width as before.
// The levels live HERE (moved from `ClassicPolicies`, which publishes them).
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The form the player bar takes in a window tier.</summary>
public enum ClassicBarForm : byte
{
    /// <summary>The 56-DIP mini bar (Compact).</summary>
    Mini,

    /// <summary>The single centred row of the production levels.</summary>
    Regular,

    /// <summary>The three-zone Huge bar.</summary>
    Huge,
}

/// <summary>The player bar's record for one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Form">The bar's form.</param>
/// <param name="MinLevel">The lowest level of the sub-ladder the tier lays the bar out at (0 Minimal … 5 Full).</param>
/// <param name="MaxLevel">The highest level.</param>
/// <param name="Height">The bar's height.</param>
public sealed record ClassicPlayerBarTier(WidthTier Tier, ClassicBarForm Form, int MinLevel, int MaxLevel, float Height);

/// <summary>Classic's player bar per window tier and its level sub-ladder (public so <c>Wavee.Tests</c> pins them).</summary>
public static class ClassicPlayerTiers
{
    /// <summary>The regular bar's height (every tier but Compact, Short included).</summary>
    public const float BarHeight = 72f;

    /// <summary>The mini bar's height (C-PlayerBar mini, C-Shell-Compact).</summary>
    public const float MiniHeight = 56f;

    /// <summary>The level indices of the sub-ladder (the core's six, narrowest first).</summary>
    public const int Minimal = 0, Compact = 1, Medium = 2, Comfortable = 3, Wide = 4, Full = 5;

    // ── the records ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Compact: the mini bar.</summary>
    public static ClassicPlayerBarTier CompactBar { get; } = new(WidthTier.Compact, ClassicBarForm.Mini, Minimal, Minimal, MiniHeight);

    /// <summary>Small: the Medium level where it fits (the board's 800), the Compact level below 760.</summary>
    public static ClassicPlayerBarTier SmallBar { get; } = new(WidthTier.Small, ClassicBarForm.Regular, Compact, Medium, BarHeight);

    /// <summary>Standard: the production sub-ladder (Comfortable / Wide / Full from 1008).</summary>
    public static ClassicPlayerBarTier StandardBar { get; } = new(WidthTier.Standard, ClassicBarForm.Regular, Comfortable, Full, BarHeight);

    /// <summary>Huge (and Huge 4K at 3840: "the same rules"): the three-zone bar over the Full level.</summary>
    public static ClassicPlayerBarTier HugeBar { get; } = new(WidthTier.Huge, ClassicBarForm.Huge, Full, Full, BarHeight);

    private static readonly ClassicPlayerBarTier[] s_bars = [CompactBar, SmallBar, StandardBar, HugeBar];

    /// <summary>The record of window tier <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static ClassicPlayerBarTier Bar(WidthTier window) => s_bars[Math.Min((int)window, (int)WidthTier.Huge)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicPlayerBarTier BarOf(in TierFacts facts) => Bar(facts.Policy is null ? WidthTier.Standard : facts.Window);

    // ── the mini bar's parts (C-PlayerBar tier="mini") ───────────────────────────────────────────────────────────────

    /// <summary>The progress hairline over the top edge.</summary>
    public const float MiniProgressH = 2f;

    /// <summary>The row's padding (2 above, 8 either side) and its gap.</summary>
    public static readonly Edges4 MiniPadding = new(8f, 2f, 8f, 0f);

    /// <summary>The gap between the identity door, the heart and the play disc.</summary>
    public const float MiniGap = 4f;

    /// <summary>The identity door: 48 tall, art 40 (r4), 10 from the text, 4 of right padding.</summary>
    public const float MiniDoorH = 48f, MiniArt = 40f, MiniArtCorner = 4f, MiniDoorGap = 10f, MiniDoorPadRight = 4f;

    /// <summary>The title (14/18 bold) over the artist line (12/16), 1 apart.</summary>
    public const float MiniTitleSize = 14f, MiniTitleLine = 18f, MiniSubSize = 12f, MiniSubLine = 16f, MiniLinesGap = 1f;

    /// <summary>The heart and the play disc: 40 x 40 (the Compact target floor), glyph 16.</summary>
    public const float MiniButton = 40f, MiniGlyph = 16f;

    /// <summary>The play disc's glyph size before the solid-glyph scale (0.8): a 16-DIP solid glyph.</summary>
    public const float MiniPlayGlyph = 20f;

    // ── the Huge bar's zones (C-PlayerBar tier="huge") ───────────────────────────────────────────────────────────────

    /// <summary>The identity zone and the right zone: 360 each.</summary>
    public const float HugeZoneW = 360f;

    /// <summary>The cover (56) and its gap to the text (12).</summary>
    public const float HugeArt = 56f, HugeLeftGap = 12f;

    /// <summary>The row's padding and the gap between the zones.</summary>
    public const float HugeRowPad = 16f, HugeRowGap = 16f;

    /// <summary>The centred transport column's cap.</summary>
    public const float HugeCentreMaxW = 1040f;

    /// <summary>The inline volume rail.</summary>
    public const float HugeVolumeW = 128f;

    // ── the level sub-ladder (moved from ClassicPolicies) ────────────────────────────────────────────────────────────

    /// <summary>The player bar: the 72-DIP dock, six levels starting at 0 / 440 / 760 / 900 / 1100 / 1240, narrowing held through a
    /// 24-DIP dip. Pressure is absorbed by what a level drops and by the spacing rungs, never by the buttons: the hit targets are
    /// flat at 32 (36 and 18 for the play button, 40 and 20 from Medium up). Each level also states what the PODCAST bar carries
    /// (`PodcastSlots`, `PodcastLeftW`): no shuffle / repeat or inline volume rail (the transport spends that room on its ±10 s steps and
    /// the speed button), previous / next from Wide, the time labels from Comfortable, the heart from Medium, the queue from Compact (as the
    /// music bar), and the identity block a bare cover at Minimal and 120 wide at Compact.</summary>
    public static PlayerBarPolicy Policy { get; } = new(
        ImmutableArray.Create(
            // Minimal: the 300-DIP floor. Identity-first: art, title and artist, the heart and the device picker.
            new PlayerTier(0f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title,
                LeftW: 132f, ArtSize: 40f, RowGap: 3f, RowPad: 6f, ClusterGap: 2f, LeftGap: 4f, SeekGap: 4f, RightGap: 0f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.Subtitle, PodcastLeftW: 40f),
            // Compact: the remaining time and the queue join (tuned P1: the queue stays from here up, ahead of lyrics, volume and
            // shuffle / repeat; its room comes from the seek bar and the gaps, never from the button sizes).
            new PlayerTier(440f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining,
                LeftW: 172f, ArtSize: 40f, RowGap: 4f, RowPad: 8f, ClusterGap: 3f, LeftGap: 6f, SeekGap: 5f, RightGap: 1f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue, PodcastLeftW: 120f),
            // Medium: the 48 cover, the roomy play button, previous / next, elapsed time, lyrics and the volume button.
            new PlayerTier(760f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext,
                LeftW: 220f, ArtSize: 48f, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.VolumeButton
                    | PlayerBarSlots.Lyrics),
            // Comfortable: shuffle / repeat and the remote-device line join.
            new PlayerTier(900f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine,
                LeftW: 230f, ArtSize: 48f, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.Queue | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.RemoteDeviceLine),
            // Wide: the inline volume rail and the video split join; the row breathes (gap 8, pad 12).
            new PlayerTier(1100f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit | PlayerBarSlots.VolumeSlider,
                LeftW: 240f, ArtSize: 48f, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit),
            // Full: the expand button joins; the widest identity block.
            new PlayerTier(1240f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit | PlayerBarSlots.VolumeSlider
                    | PlayerBarSlots.Expand,
                LeftW: 260f, ArtSize: 48f, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VideoSplit | PlayerBarSlots.Expand)),
        Hysteresis: 24f, Height: BarHeight)
    {
        // The levels each window tier may use, from the records above (Compact first).
        Windows = ImmutableArray.Create(
            new PlayerBarWindowRule(CompactBar.MinLevel, CompactBar.MaxLevel), new PlayerBarWindowRule(SmallBar.MinLevel, SmallBar.MaxLevel),
            new PlayerBarWindowRule(StandardBar.MinLevel, StandardBar.MaxLevel), new PlayerBarWindowRule(HugeBar.MinLevel, HugeBar.MaxLevel)),
    };
}
