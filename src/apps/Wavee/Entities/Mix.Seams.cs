// ── Entities/Mix.Seams.cs — #48 Mix, each pair's transition inline in the list (owner 2026-11-26, plan decision D18 rev 2) ──────────
//
// A mixed playlist shows its transitions IN the list, as Spotify does: under every row, a lane (the table's `Shape.SeamH`) with a thin
// connector from this row's artwork to the next row's, and on it the pair's chip:
//
//   ▣ Midnight Drive
//   │
//   [✦ Auto]                    Spotify's Auto (a sparkle)
//   │
//   ▣ Dalkom Cafe
//   [∿ Fade]  [∿ Custom]        a pair edited in Spotify or here: the preset by name, or Custom when its styles or curves are its own
//   [No transition]             a pair that goes straight into the next song (Spotify's, or "Don't mix into the next song")
//   [∿ Fade · This PC]          a pair saved on this PC only, or "Not saved" while it waits for Spotify
//   [✎ Auto]                    the pair whose editor is open (the chip's selected face)
//
// The chip opens the pair's editor in the right panel (Entities/Mix.Editor.Session.cs); it is focusable, Enter and Space open it too. The
// lane is laid out only while the list shows the playlist in its own order (a sort or a filter breaks the pairs) and only for a playlist
// this account hears mixed; the last row's lane stays empty, and a pair that cannot mix (a local file, an episode) says "No transition".
// Each theme draws the chip and the connector in its own voice (ViewKeys.MixSeam).

using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The Mix menu's view option, in the settings registry; <see cref="Changed"/> follows it.</summary>
public static class MixPrefs
{
    public static readonly Signal<int> Changed = new(0);

    /// <summary>"Mix when shuffling" (on by default): off = Spotify's behaviour, a shuffled mixed playlist plays unmixed.</summary>
    public static bool WhenShuffling => Platform.Settings.Get(Platform.Keys.MixWhenShuffling);

    public static void SetWhenShuffling(bool on) { Platform.Settings.Set(Platform.Keys.MixWhenShuffling, on); Changed.Value = Changed.Peek() + 1; }
}

public static class MixSeams
{
    /// <summary>Where the lane draws: its height, the connector's x, the chip's lead, the connector's reach past the lane, the touch form.</summary>
    public readonly record struct Geometry(float Lane, float Indent, float Lead, float Reach, bool Touch);

    /// <summary>A lane leaving (Mix off, a sort): it fades out while the rows close over it.</summary>
    public static readonly LayoutTransition LaneExit = new(TransitionChannels.Opacity, TransitionDynamics.Tween(Design.Motion.Faster, Easing.FluentAccelerate),
        Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>What one pair's chip says.</summary>
    public readonly record struct Face(bool Shown, MixSeamKind Kind, string Label, string Note);

    /// <summary>The transition lane under a row: the chip's line and its air above and below (the connector runs through them). At Compact the
    /// chip is the 40-DIP touch target.</summary>
    public static float LaneHeight(bool touch) => touch ? Theming.TouchTargetPolicy.CompactFloor + 2f * Spacing.XS : Ui.Caption("").LineHeight + 4f * Spacing.XS;

    /// <summary>The chip of the pair playlist row <paramref name="index"/> → <paramref name="index"/> + 1. UI thread.</summary>
    public static Face For(int playlistSlot, int index)
    {
        var p = new Playlist(playlistSlot);
        if (!p.IsValid || !p.MixOn || MixOptOut.Is(p)) return default;
        var edges = Entities.Current.Edges.PlaylistTracks;
        var targets = edges.Targets(playlistSlot);
        var payload = edges.Payload(playlistSlot);
        if (index < 0 || index + 1 >= targets.Length) return default;
        var t = Playback.Mix.PlanListPair(playlistSlot, index, index + 1);
        ulong from = MixRecipe.Hex64(Entities.Strings.Resolve(payload[index].ItemId));
        ulong to = MixRecipe.Hex64(Entities.Strings.Resolve(payload[index + 1].ItemId));
        // A record on this PC: an edit waiting for Spotify ("Not saved"), or one this PC keeps for itself ("This PC").
        bool local = MixOverrides.TryGet(p.Id.Text, from, to, out var record) && record.Kind != MixOverrideKind.Auto;
        string note = !local ? "" : Loc.Get(record.Pending ? Strings.Mix.StateNotSaved : Strings.Mix.StateThisPc);
        if (!t.Mix) return new Face(true, MixSeamKind.None, Loc.Get(Strings.Mix.Editor.NoTransition), note);
        if (t.Source == MixSource.Auto) return new Face(true, MixSeamKind.Auto, Loc.Get(Strings.Mix.Auto), note);
        if (IsCustom(in t)) return new Face(true, MixSeamKind.Custom, Loc.Get(Strings.Mix.Editor.Custom), note);
        return new Face(true, MixSeamKind.Preset, MixEdit.PresetName(t.Preset), note);
    }

    /// <summary>A saved transition with styles or curves of its own (Spotify's "Custom"): a drawn curve, or a style that is not its preset's.</summary>
    static bool IsCustom(in MixTiming t) => t.Curves is not null || t.Styles != MixStyles.Resolve(t.Preset, default);

    /// <summary>"Auto", or the saved preset by Spotify's name and the overlap: "Fade · 4 s", "Wave · 7.4 s", "Mix · 3 s" for a
    /// preset Spotify does not offer by name (the queue's Mixing line).</summary>
    public static string ChipText(in MixTiming t)
    {
        if (!t.Mix) return Loc.Get(Strings.Mix.NoMix);
        if (t.Source == MixSource.Auto) return Loc.Get(Strings.Mix.Auto);
        return StyleText(in t);
    }

    /// <summary>"Fade · 4 s": the plan's preset by Spotify's name (<see cref="PresetName"/>) and its overlap.</summary>
    public static string StyleText(in MixTiming t)
    {
        string seconds = (t.OutDurationMs / 1000.0).ToString(t.OutDurationMs % 1000 < 50 ? "0" : "0.#", CultureInfo.InvariantCulture);
        return Strings.Mix.PresetChip(PresetName(t.Preset), seconds);
    }

    /// <summary>Spotify's UI name of a preset id (recipe 2.1) for the nine it offers (Fade, Blend, Rise, Melt, Slam, Wave, Dissolve,
    /// Surge, Afterglow); "Mix" for an internal id or one the table does not know.</summary>
    public static string PresetName(int presetId) => Loc.Get((MixPresetId)presetId switch
    {
        MixPresetId.Fade => Strings.Mix.Preset.Fade,
        MixPresetId.Blend => Strings.Mix.Preset.Blend,
        MixPresetId.Rise => Strings.Mix.Preset.Rise,
        MixPresetId.Melt => Strings.Mix.Preset.Melt,
        MixPresetId.Slam => Strings.Mix.Preset.Slam,
        MixPresetId.Wave => Strings.Mix.Preset.Wave,
        MixPresetId.Dissolve => Strings.Mix.Preset.Dissolve,
        MixPresetId.Surge => Strings.Mix.Preset.Surge,
        MixPresetId.Afterglow => Strings.Mix.Preset.Afterglow,
        _ => Strings.Mix.Preset.Mix,
    });
}

/// <summary>The transition lane of one table row (the row's core wrapper puts it under the row's grid, Shape.SeamH tall): the pair's chip on its
/// connector, drawn by the theme (ViewKeys.MixSeam). Renders an empty lane for the last row and for rows that are not neighbours in the playlist.</summary>
internal sealed class MixSeamLane(Func<int> display, Func<int, int> originalAt, Func<int> playlistSlot, Func<MixSeams.Geometry> geometry,
    Func<int, float> revealDelay) : Component
{
    readonly Func<int, float> _revealDelay = revealDelay;
    /// <summary>Decided once, at the lane's first render: −1 born at rest, else the delay its timeline draws in after.</summary>
    float _reveal = float.NaN;
    readonly Func<int> _display = display;
    readonly Func<int, int> _originalAt = originalAt;
    readonly Func<int> _playlist = playlistSlot;
    readonly Func<MixSeams.Geometry> _geometry = geometry;
    Action? _open;
    int _pl, _index = -1;

    readonly record struct State(MixSeams.Face Face, int Playlist, int Index, bool Selected);

    public override Element Render()
    {
        _open ??= () => MixEditSession.Edit(_pl, _index);
        var g = UseComputed(_geometry).Value;
        var s = UseComputed(() =>
        {
            _ = Entities.Current.Playlists.Changed.Value;          // the mix flags, a local record (its write bumps the row)
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            _ = Entities.Current.Edges.TrackBeats.Changed.Value;   // the timing behind Auto's pick
            _ = Entities.Current.Tracks.Changed.Value;
            int pl = _playlist();
            if (pl <= 0) return default(State);
            int d = _display();
            int a = _originalAt(d), b = _originalAt(d + 1);
            if (a < 0 || b != a + 1) return default;                // not neighbours in the playlist (the last row)
            return new State(MixSeams.For(pl, a), pl, a, MixEditSession.Shows(pl, a));
        }).Value;
        _pl = s.Playlist;
        _index = s.Index;
        if (float.IsNaN(_reveal)) _reveal = _revealDelay(_display());
        if (!s.Face.Shown || g.Lane <= 0f) return new BoxEl { Key = "mix:seam:none", Height = g.Lane, Shrink = 0f };
        var props = new MixSeamProps
        {
            Label = s.Face.Label, Kind = s.Face.Kind, Note = s.Face.Note, Selected = s.Selected, Open = _open,
            Name = Strings.Mix.Seam.Name(s.Face.Note.Length > 0 ? s.Face.Label + " · " + s.Face.Note : s.Face.Label),
            LaneHeight = g.Lane, Indent = g.Indent, Lead = g.Lead, Reach = g.Reach, Touch = g.Touch, Reveal = _reveal,
        };
        return new BoxEl
        {
            Key = "mix:seam:" + s.Index.ToString(CultureInfo.InvariantCulture), Direction = 1, Height = g.Lane, Shrink = 0f, MinWidth = 0f,
            Children = [Slots.View(ViewKeys.MixSeam, props)],
        };
    }
}
