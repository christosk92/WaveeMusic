// ── Entities/Mix.Seams.cs — #48 Mix, the quiet list (plan §3.10, UX direction rev 2 "Track list") ───────────────────────
//
// The seam under a row: a strip inside row A's own bounds, along its bottom edge, one layer of the row's core wrapper (so all
// three themes get it and no row's height changes: the virtualized list's layout is untouched when the lens read lands).
//
//   plain pair      nothing at rest; hovering the strip fades in a hairline and the "⋯ Transition" handle
//   edited pair     a 6 DIP accent dot, always (a Spotify recipe, or a record on this PC)
//   No mix          the hairline with a 24 DIP break at its middle, always (a local NoMix record, a local or not-mixable side)
//   chips (option)  "Show transitions: Always" puts Spotify's compact chip in every seam ("Auto", "Rise · 3 s", "No mix")
//
// A seam shows only between two rows that are neighbours in the PLAYLIST (display i and i+1 map to playlist i and i+1), so a sort
// or a filter hides them by itself. Clicking a seam opens the Transitions list. Not interactive at Compact (the row menu's
// "Don't mix into the next song" and the Transitions list are the touch and keyboard paths).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The Mix menu's view options, in the settings registry; <see cref="Changed"/> follows them.</summary>
public static class MixPrefs
{
    public static readonly Signal<int> Changed = new(0);

    /// <summary>"Show transitions: Always" (Spotify's chips in every seam); off = on hover.</summary>
    public static bool ShowAlways => Platform.Settings.Get(Platform.Keys.MixShowTransitions);

    /// <summary>"Mix when shuffling" (on by default): off = Spotify's behaviour, a shuffled mixed playlist plays unmixed.</summary>
    public static bool WhenShuffling => Platform.Settings.Get(Platform.Keys.MixWhenShuffling);

    public static void SetShowAlways(bool on) { Platform.Settings.Set(Platform.Keys.MixShowTransitions, on); Changed.Value = Changed.Peek() + 1; }

    public static void SetWhenShuffling(bool on) { Platform.Settings.Set(Platform.Keys.MixWhenShuffling, on); Changed.Value = Changed.Peek() + 1; }
}

public static class MixSeams
{
    /// <summary>0 = no seam · 1 plain · 2 edited · 3 no mix.</summary>
    public readonly record struct Seam(byte Mark, string Chip);

    /// <summary>The seam under playlist row <paramref name="index"/> (the pair index → index + 1). UI thread.</summary>
    public static Seam For(int playlistSlot, int index)
    {
        var p = new Playlist(playlistSlot);
        if (!p.IsValid || !p.MixOn || MixListenWithout.Is(p)) return default;
        var edges = Entities.Current.Edges.PlaylistTracks;
        var targets = edges.Targets(playlistSlot);
        var payload = edges.Payload(playlistSlot);
        if (index < 0 || index + 1 >= targets.Length) return default;
        var t = Playback.Mix.PlanListPair(playlistSlot, index, index + 1);
        bool always = MixPrefs.ShowAlways;
        if (!t.Mix)
            return t.WhyNot is MixWhyNot.PairOptOut or MixWhyNot.LocalFile or MixWhyNot.NotMixable
                ? new Seam(3, always ? Loc.Get(Strings.Mix.NoMix) : "") : default;
        ulong from = MixRecipe.Hex64(Entities.Strings.Resolve(payload[index].ItemId));
        ulong to = MixRecipe.Hex64(Entities.Strings.Resolve(payload[index + 1].ItemId));
        bool edited = t.Source == MixSource.Saved || MixOverrides.TryGet(p.Id.Text, from, to, out _);
        return new Seam(edited ? (byte)2 : (byte)1, always ? ChipText(in t) : "");
    }

    /// <summary>"Auto", "Fade · 4 s", "Rise · 2.9 s", or "Edited in Spotify" for a preset Wavee does not know.</summary>
    public static string ChipText(in MixTiming t)
    {
        if (!t.Mix) return Loc.Get(Strings.Mix.NoMix);
        if (t.Source == MixSource.Auto) return Loc.Get(Strings.Mix.Auto);
        string seconds = (t.OutDurationMs / 1000.0).ToString(t.OutDurationMs % 1000 < 50 ? "0" : "0.#", CultureInfo.InvariantCulture);
        return t.Preset switch
        {
            (byte)MixPreset.Fade => Strings.Mix.FadeChip(seconds),
            (byte)MixPreset.Rise => Strings.Mix.RiseChip(seconds),
            _ => Loc.Get(Strings.Mix.EditedInSpotify),
        };
    }

    /// <summary>The strip's height inside the row: room for the chip when chips show, else the hairline's hover band.</summary>
    public static float StripHeight(bool chips) => chips ? Spacing.L : Spacing.M;
}

/// <summary>The seam layer of one table row (the row's core wrapper stacks it over the theme's grid). Props: the row's index read and
/// the host's reads; renders nothing for a plain row of a list that is not mixed.</summary>
internal sealed class MixSeamLayer(Func<int> display, Func<int, int> originalAt, Func<int> playlistSlot, Func<ColorF> accent,
                                   bool interactive, Action<int> open) : Component
{
    readonly Func<int> _display = display;
    readonly Func<int, int> _originalAt = originalAt;
    readonly Func<int> _playlist = playlistSlot;
    readonly Func<ColorF> _accent = accent;
    readonly bool _interactive = interactive;
    readonly Action<int> _open = open;
    int _index = -1;

    public override Element Render()
    {
        var seam = UseComputed(() =>
        {
            _ = Entities.Current.Playlists.Changed.Value;          // the mix flags, a local record (its write bumps the row)
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            _ = Entities.Current.Edges.TrackBeats.Changed.Value;   // the timing behind a chip
            _ = MixPrefs.Changed.Value;
            int pl = _playlist();
            if (pl <= 0) return default(MixSeams.Seam);
            int d = _display();
            int a = _originalAt(d), b = _originalAt(d + 1);
            if (a < 0 || b != a + 1) return default;                // not neighbours in the playlist (sorted, filtered, the last row)
            _index = a;
            return MixSeams.For(pl, a);
        }).Value;
        if (seam.Mark == 0) return new BoxEl { Key = "mix:seam:none", Visible = false };

        bool chips = seam.Chip.Length > 0;
        float h = MixSeams.StripHeight(chips);
        ColorF line = Tok.StrokeDividerDefault;
        var layers = new List<Element>(3);
        if (seam.Mark == 3)
            // The break: the hairline with a gap at its middle, always.
            layers.Add(new BoxEl
            {
                Key = "mix:seam:break", Direction = 0, Grow = 1f, AlignItems = FlexAlign.End,
                Children =
                [
                    new BoxEl { Key = "mix:seam:l", Grow = 1f, Height = 1f, Fill = line },
                    new BoxEl { Key = "mix:seam:gap", Width = Spacing.XL, Height = 1f },
                    new BoxEl { Key = "mix:seam:r", Grow = 1f, Height = 1f, Fill = line },
                ],
            });
        else if (!chips)
            // The hairline across the row and the "⋯ Transition" handle: the plain pair's hover state.
            layers.Add(new BoxEl
            {
                Key = "mix:seam:line", ZStack = true, Grow = 1f,
                Children =
                [
                    new BoxEl { Height = 1f, AlignSelf = FlexAlign.End, Fill = line },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(Spacing.XXL, 0f, 0f, 0f),
                        Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Fill = Tok.WindowBackground, Corners = Radii.ControlAll,
                        Children = [Ui.Caption("⋯ " + Loc.Get(Strings.Mix.Transition)) with { MaxLines = 1 }],
                    },
                ],
            });
        if (seam.Mark == 2 && !chips)
            layers.Add(new BoxEl
            {
                // The edited pair's dot: centred in the strip, so it never crosses the row's edge.
                Key = "mix:seam:dot", Width = Spacing.S - 2f, Height = Spacing.S - 2f, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                Corners = Radii.Circle(Spacing.S - 2f), Fill = _accent(),
            });
        if (chips)
            layers.Add(new BoxEl
            {
                Key = "mix:seam:chip", AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
                Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeDividerDefault, BorderWidth = 1f,
                Children = [Ui.Caption(seam.Chip) with { MaxLines = 1, Color = seam.Mark == 2 ? _accent() : Tok.TextSecondary }],
            });
        // A plain pair paints nothing at rest: its strip fades in on hover (opacity only, no layout). The marks are always on.
        bool hoverOnly = seam.Mark == 1 && !chips;
        return new BoxEl
        {
            Key = "mix:seam", ZStack = true, Height = h, AlignSelf = FlexAlign.End,
            HitTestVisible = _interactive, Cursor = _interactive ? CursorId.Hand : null,
            OnClick = _interactive ? () => _open(_index) : null,
            Opacity = hoverOnly ? 0f : 1f,
            WhileHover = hoverOnly && _interactive ? new MotionTarget { Opacity = 1f } : null,
            Children = layers.ToArray(),
        };
    }
}
