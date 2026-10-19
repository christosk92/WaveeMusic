// ── Theming/Contracts/PanelBitsProps.cs — the props of the panel's small views: the lyrics header, "Next up" and the lyrics peek ──
//
// Three pictures the core drew itself until the leftovers lane (P3): the panel's lyrics header (`Panel.LyricsHeader`: the title, the
// second-line toggle, the AI sparkle, expand, the developer inspector, close), the now-playing body's "Next up" rows (`Panel.NextUp`,
// shared with the video body) and the now-playing body's two-row lyrics peek (`Player.LyricsPeek`).
//
// WHAT STAYS CORE. The header's facts (which word titles it, whether the document carries a second layer and which one is on, the tooltips)
// are `RailWiring`'s reads; the sparkle's verdict and its click are `AiLyrics.HeaderFacts` / `HeaderClick` over `AiLyrics.Rules.Header`;
// the developer gate and the inspector's open are `Diagnostics`'; the expand is `LyricsVerbs` (with the shared transition's begin); close
// is the panel's state machine. The "Next up" window, the queue reads, the row data (`Track.RowData`), each row's play and click verbs and
// its key are `Rail.NextUpRows`. The peek's document, its clock (the 100 ms interval while it shows) and the active / peek pair are the
// core hosts `Lyrics.PeekHost` / `PeekReel` ([CoreHost]), which mount `Player.LyricsPeek` with the decided face.
//
// A read is a `Func` the template calls INSIDE its render, so the subscription lands on the view's slot (as it landed on the core
// component that drew it before). WVTH006: sealed records with init-only members; every delegate is a cached static.

using FluentGpu.Controls;

namespace Wavee.Theming.Contracts;

/// <summary>What one render of the lyrics header shows.</summary>
/// <param name="Title">"Lyrics", or "Transcript" while an episode plays.</param>
/// <param name="HasSecondLine">The document on screen carries a second layer: the globe toggle is composed.</param>
/// <param name="SecondLineActive">The chosen second layer is the one shown (the toggle's accent arm).</param>
/// <param name="SecondLineTip">The toggle's tooltip ("" without a second layer).</param>
/// <param name="ExpandTip">The expand button's tooltip.</param>
/// <param name="TrackTitle">P6 fix (optional): the playing track's or episode's title ("" when nothing plays). A header that names the song
/// (WinUI's W-PaneLyrics) reads it; the others keep <paramref name="Title"/>.</param>
/// <param name="SyncLine">P6 fix (optional): how the document on screen is timed ("Synced · word by word", "Synced · line by line", "Not
/// synced"; "" while there is no document).</param>
public readonly record struct LyricsHeaderFacts(string Title, bool HasSecondLine, bool SecondLineActive, string SecondLineTip, string ExpandTip,
    string TrackTitle = "", string SyncLine = "");

/// <summary>The AI sparkle's verdict for one render (<c>AiLyrics.Rules.Header</c> over the status, the track and the preference).</summary>
/// <param name="Visible">The feature can run here: the sparkle shows (hidden, it is a zero-extent box).</param>
/// <param name="Active">The accent arm (the song is timed by AI).</param>
/// <param name="Working">The playing track is being timed (a picture may breathe).</param>
/// <param name="Tip">The tooltip, and what a click reads aloud when it can do nothing else.</param>
/// <param name="TrackId">The track the verdict is about.</param>
/// <param name="PrefersAi">The track's stored preference.</param>
/// <param name="TogglesPreference">A click switches the song between AI timing and the provider's.</param>
/// <param name="OpensSettings">A click opens Settings › Appearance (Settings can fix what the tooltip says).</param>
public readonly record struct LyricsSparkleFacts(bool Visible, bool Active, bool Working, string Tip, string TrackId, bool PrefersAi,
    bool TogglesPreference, bool OpensSettings);

/// <summary>The props of <see cref="ViewKeys.PanelLyricsHeader"/>.</summary>
public sealed record PanelLyricsHeaderProps
{
    /// <summary>The header's facts; call it inside the render (it subscribes the lyrics preferences and the playing kind).</summary>
    public required Func<LyricsHeaderFacts> Read { get; init; }

    /// <summary>Cycle the second line.</summary>
    public required Action ToggleSecondLine { get; init; }

    /// <summary>Open the fullscreen stage on lyrics; the argument is the shared transition's begin (the art flight), or null.</summary>
    public required Action<Action<string>?> Expand { get; init; }

    /// <summary>The sparkle's verdict; call it inside the sparkle's own render (only it subscribes the AI status).</summary>
    public required Func<LyricsSparkleFacts> Sparkle { get; init; }

    /// <summary>What a sparkle click does with the verdict it was drawn from.</summary>
    public required Action<LyricsSparkleFacts> SparkleClick { get; init; }

    /// <summary>The developer inspector shows (developer mode, not presenting); call it inside the button's own render.</summary>
    public required Func<bool> InspectorShown { get; init; }

    /// <summary>Open the lyrics inspector for the playing track over this overlay (a null overlay does nothing).</summary>
    public required Action<IOverlayService> OpenInspector { get; init; }

    /// <summary>Close the panel.</summary>
    public required Action Close { get; init; }
}

/// <summary>One "Next up" row: its node key (the queue item, or its index, and the artwork flag), whether the art shows, and the shared media
/// surface's data (the title, the artists, the play and click verbs, the drag and the menu: <c>Track.RowData</c>).</summary>
public readonly record struct NextUpRow(string Key, bool Art, Controls.CardData Data);

/// <summary>The props of <see cref="ViewKeys.PanelNextUp"/>.</summary>
public sealed record NextUpProps
{
    /// <summary>The rows; call it inside the render (it subscribes the scope, the queue edge, the track table and the artwork setting).</summary>
    public required Func<NextUpRow[]> Read { get; init; }
}

/// <summary>Which face the lyrics peek shows.</summary>
public enum LyricsPeekFace : byte
{
    /// <summary>Nothing: no track, or no timed document (a bare zero-size box).</summary>
    None,
    /// <summary>A video plays: the one-row "no sync during video" note.</summary>
    Note,
    /// <summary>The two-row reel: the sung line over a faded peek at the next.</summary>
    Reel,
}

/// <summary>The props of <see cref="ViewKeys.LyricsPeek"/>: plain data, so an unchanged pair re-renders nothing.</summary>
public sealed record LyricsPeekProps
{
    public required LyricsPeekFace Face { get; init; }

    /// <summary>The sung line's index and text (null text: no such line, an empty row).</summary>
    public int Active { get; init; } = -1;
    public string? ActiveText { get; init; }

    /// <summary>The next line's index and text (null text: no such line).</summary>
    public int Peek { get; init; } = -1;
    public string? PeekText { get; init; }

    /// <summary>Open the panel on lyrics (the reel and the note are one button).</summary>
    public required Action OpenLyrics { get; init; }
}
