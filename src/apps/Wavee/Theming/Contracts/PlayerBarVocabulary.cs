// ── Theming/Contracts/PlayerBarVocabulary.cs — the words the player bar's decisions are stated in (themes plan 6.0, T6a) ────
//
// The core decides what the bar carries (`Shell.PlayerBarRules`: the slots a level has, the "⋯" overflow's rows, the primary's
// verb, the title ink); a theme's view draws what was decided. These four enums were `Shell.RightSlot`, `Shell.OverflowCommand`,
// `Shell.PrimaryVerb` and `Shell.NowPlayingInk` until T6a, and moved here because a theme may not name `Shell` (WVTH002). Their
// members, values and ORDER are unchanged: the order of `OverflowCommand` IS the menu's order, the bit order of `RightSlot` IS the
// right cluster's row order.

namespace Wavee.Theming.Contracts;

/// <summary>The right cluster's SLOTS, in bit order — which IS the row order. A slot's PRESENCE is a function of the level alone
/// (<c>Shell.PlayerBarRules.RightSlots</c>), the video split's included: a track without a video shows the split disabled in its
/// slot (2026-11-07; from 2026-09-16 the split existed only with a video, and every skip between a video and a plain track reflowed
/// the row). Playback STATE only lights a slot's FACE (<c>Shell.PlayerBarRules.SlotFaceVisible</c>). That split is the fix for the bar
/// reflowing on a skip: the heart, lyrics and overflow used to ARRIVE with <c>active</c>, each arrival stealing width from the
/// centre while every cluster animated its bounds. The centre's seek bar has one width per level.</summary>
[Flags]
public enum RightSlot : ushort
{
    None = 0,
    Shuffle = 1 << 0,
    Repeat = 1 << 1,
    Volume = 1 << 2,
    VolumeSlider = 1 << 3,
    Lyrics = 1 << 4,
    Video = 1 << 5,
    Queue = 1 << 6,
    Devices = 1 << 7,
    Expand = 1 << 8,
    More = 1 << 9,
}

/// <summary>One row of the "⋯" overflow, in BUILD order — which IS the menu order (ch 20 W17).</summary>
public enum OverflowCommand : byte { Previous, Next, Shuffle, Repeat, Lyrics, Queue, NowPlaying, FullScreen, Video, Mute }

/// <summary>What the primary transport button does when clicked.</summary>
public enum PrimaryVerb : byte { None, TogglePlay, Retry }

/// <summary>The title line's ink rung.</summary>
public enum NowPlayingInk : byte { Primary, Secondary, Critical }
