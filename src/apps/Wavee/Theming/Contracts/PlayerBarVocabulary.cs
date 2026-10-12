// ── Theming/Contracts/PlayerBarVocabulary.cs — the words the player bar's decisions are stated in (themes plan 6.0, T6a) ────
//
// The core decides what the bar carries (`Shell.PlayerBarRules`: the slots a level has, the "⋯" overflow's rows, the primary's
// verb, the title ink); a theme's view draws what was decided. These four enums were `Shell.RightSlot`, `Shell.OverflowCommand`,
// `Shell.PrimaryVerb` and `Shell.NowPlayingInk` until T6a, and moved here because a theme may not name `Shell` (WVTH002). Their
// members, values and ORDER are unchanged: the order of `OverflowCommand` IS the menu's order, the bit order of `RightSlot` IS the
// right cluster's row order.

namespace Wavee.Theming.Contracts;

/// <summary>The right cluster's SLOTS, in bit order — which IS the row order. A slot's PRESENCE is a function of the level
/// (<c>Shell.PlayerBarRules.RightSlots</c>) with ONE exception — the video split, which exists iff the current track has a video
/// (user decision 2026-09-16: a 52-DIP hole beside lyrics was worse than the row easing once when a video track lands). Playback
/// STATE only lights a slot's FACE (<c>Shell.PlayerBarRules.SlotFaceVisible</c>). That split is still the fix for the bar
/// reflowing on track start: the heart, lyrics and overflow used to ARRIVE with <c>active</c>, each arrival stealing width from the
/// centre while every cluster animated its bounds. A track starting WITHOUT a video therefore still moves nothing; the centre's
/// seek bar has exactly two widths per level.</summary>
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
