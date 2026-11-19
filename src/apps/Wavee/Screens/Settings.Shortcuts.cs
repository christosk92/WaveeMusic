// ── Screens/Settings.Shortcuts.cs — the keyboard shortcuts reference, as data ─────────────────────────────────────────────────────────
//
// Role: CORE. Help & about › Keyboard shortcuts lists the chords the shell answers to (`SettingsSpec.Help.cs` makes one fact dial per entry,
// `help.shortcuts.<id>`). The app has no rebinding, so the reference offers none.

namespace Wavee;

public static partial class Settings
{
    /// <summary>The keyboard section's reference: the chords the shell defines (`Shell.UI.cs`' chords, `OnShellKey`, and the player's own keys, `Shell.PlayerKey`). It is a READ of
    /// what the app answers to: the app has no rebinding, so the section offers none.</summary>
    public static class Shortcuts
    {
        /// <summary>One shortcut: the row id, its words, the chord as it is spoken ("Ctrl+F"), and whether it exists only under a theme with tabs.</summary>
        public sealed record Entry(string Id, string TitleKey, string? CaptionKey, string Chord, bool NeedsTabs = false);

        /// <summary>"Ctrl+F" as the boards' small cap reads it: "Ctrl F". The plus sign itself ("Ctrl++") is the one chord whose separator is also its key.</summary>
        public static string Words(string chord) => chord == "Ctrl++" ? "Ctrl +" : chord.Replace('+', ' ');

        public static readonly Entry[] All =
        [
            new("playPause", Strings.Settings.Keyboard.PlayPause, Strings.Settings.Keyboard.PlayPauseSub, "Space"),
            new("seekBack", Strings.Settings.Keyboard.SeekBack, Strings.Settings.Keyboard.SeekBackSub, "←"),
            new("seekForward", Strings.Settings.Keyboard.SeekForward, Strings.Settings.Keyboard.SeekForwardSub, "→"),
            new("seekBackFine", Strings.Settings.Keyboard.SeekBackFine, null, "Shift+←"),
            new("seekForwardFine", Strings.Settings.Keyboard.SeekForwardFine, null, "Shift+→"),
            new("volumeUp", Strings.Settings.Keyboard.VolumeUp, Strings.Settings.Keyboard.VolumeUpSub, "↑"),
            new("volumeDown", Strings.Settings.Keyboard.VolumeDown, Strings.Settings.Keyboard.VolumeDownSub, "↓"),
            new("search", Strings.Settings.Keyboard.Search, null, "Ctrl+F"),
            new("palette", Strings.Settings.Keyboard.Palette, null, "Ctrl+K"),
            new("settings", Strings.Settings.Keyboard.Settings, null, "Ctrl+,"),
            new("newTab", Strings.Settings.Keyboard.NewTab, null, "Ctrl+T", NeedsTabs: true),
            new("back", Strings.Settings.Keyboard.Back, null, "Alt+←"),
            new("forward", Strings.Settings.Keyboard.Forward, null, "Alt+→"),
            new("fullscreen", Strings.Settings.Keyboard.Fullscreen, null, "F11"),
            new("zoomIn", Strings.Settings.Keyboard.ZoomIn, null, "Ctrl++"),
            new("zoomOut", Strings.Settings.Keyboard.ZoomOut, null, "Ctrl+−"),
            new("zoomReset", Strings.Settings.Keyboard.ZoomReset, null, "Ctrl+0"),
            new("escape", Strings.Settings.Keyboard.Escape, null, "Esc"),
        ];
    }
}
