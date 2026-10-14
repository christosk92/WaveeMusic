// ── Shell/Stage.Frame.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// the core half of the stage's frame views (themes plan T7a1): the layer builders the root places and the transport card's facts
//
// Role: CORE
// Spec: docs/guide/theming.md section 2b (the stage)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// What the structure's `Stage.Root`, `Stage.TopBar` and `Stage.Transport` templates may not name (`Playback`, `Shell`, `Entities`,
// `Queue`'s rows, `Prefs`) arrives here as facts and verbs. Every read the transport card's render made is made INSIDE
// `TransportFacts` (and the three small `Func`s beside it), which the template calls in its own render: a signal read there
// subscribes the card, exactly as the old `TransportCard`, `MuteGlyph`, `VolumeRow` and `DeviceButton` components subscribed on
// theirs.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The layers the root places that are not the layout layers' (those are views since T7a2, `Shell\Stage.Layers.cs`), as cached
    /// builders over their core components (T7b: the lyrics pane inside PaneHost; the queue pane is the `Queue.StagePane` view PaneHost mounts since T7d). The caption host is
    /// T7a1's core half; the lyric facts, the artist-header lookups and the visualizer clock are zero-size machinery.</summary>
    static readonly StageLayers s_layers = new()
    {
        Pane = static () => Embed.Comp(static () => new PaneHost()),
        Caption = static () => Embed.Comp(static () => new CaptionHost()),
        LyricFacts = static () => Embed.Comp(static () => new LyricFacts()),
        HeroFacts = static () => Embed.Comp(static () => new HeroFacts()),
        Clock = static () => Embed.Comp(static () => new Visualizer.Clock()),
    };

    /// <summary>The props of the transport card for one stage context: the facts, the verbs and the bar's own seek and time pieces.</summary>
    static StageTransportProps TransportProps(StageCtx ctx) => new()
    {
        Stage = ctx,
        Facts = () => TransportFacts(ctx),
        Verbs = PlayerVerbs.Default,
        // the bar's own seek; its scrub edge holds the chrome through the onScrubbing seam (§4.12 — a PlayerChromeFeed is inert without a MediaPlayerElement owner)
        SeekBar = static onScrubbing => Shell.SeekBar(onScrubbing: onScrubbing),
        TimeText = static (remaining, ink) => Shell.TimeText(remaining, ink),
        Muted = static () => Playback.Muted.Value,
        ToggleMute = static () => Playback.ToggleMute(),
        Volume = static () => Playback.Volume.Value,
        SetVolume = static v => Playback.SetVolume(v),
        Device = DeviceFacts,
        DeviceItems = DeviceItems,
        OpenDevicePicker = static () => PlayerVerbs.Default.OpenDevicePicker(),
    };

    /// <summary>Everything the transport card draws that is playback state. The reads are the old <c>TransportCard.Render</c>'s, in its order.</summary>
    static StageTransportFacts TransportFacts(StageCtx ctx)
    {
        var track = ctx.RowValue();
        bool playing = Playback.IsPlaying.Value;
        bool loading = Playback.PhaseSignal.Value == Playback.Phase.Loading;
        var tf = PlayerVerbs.TransportFacts();
        bool primary = Transport.PrimaryEnabled(track.IsValid, loading);
        var lib = Controls.Library;
        string uri = track.IsValid ? track.Uri.Text : "";
        string title = track.IsValid ? track.Title : "";
        bool saved = lib is not null && uri.Length > 0 && lib.IsSaved(uri);
        Action? like = lib is { } seam && uri.Length > 0 ? () => seam.ToggleSaved(uri, title) : null;
        var repeat = Playback.Repeat.Value;
        EntityId contextId = Playback.ContextUri.Value;
        Queue.ContextWire wire = Playback.ContextLabel.Value;
        string? source = Queue.ContextName(contextId, in wire);
        bool hasSource = source is { Length: > 0 };
        // a formatted key: the generated method formats; Loc.Get would render "[…]" (V-D15). No source ⇒ no label (never "Playing from search" for nothing).
        string playingFrom = hasSource ? Strings.Stage.PlayingFrom(Queue.ContextKindLabel(contextId)) : "";
        return new StageTransportFacts(playing, primary, tf.CanTransport, tf.PrevEnabled, tf.NextEnabled, Playback.Shuffle.Value,
            repeat != RepeatMode.Off, repeat == RepeatMode.Track, saved, like, playingFrom, source ?? "");
    }

    /// <summary>"This PC ▾": the output device button's glyph and name.</summary>
    static StageDeviceFacts DeviceFacts()
    {
        _ = Playback.Devices.Changed.Value;
        var rows = Playback.Devices.Rows;
        int slot = Shell.DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, rows);
        string name = slot >= 0 ? rows[slot].Name ?? "" : Loc.Get(Strings.Player.SystemDefault);
        string glyph = slot >= 0 ? Icons.Devices : Icons.ThisPc;
        if (slot < 0)
        {
            string? selected = Playback.Audio.SelectedOutputId.Value;
            foreach (var d in Playback.Audio.Devices.Value)
                if (selected is { Length: > 0 } && string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase)) { name = d.Name; break; }
        }
        return new StageDeviceFacts(glyph, name);
    }

    /// <summary>The two-section device picker over the SAME items the player bar builds (Shell.DevicePickerMenuItems), built when it opens.</summary>
    static List<MenuFlyoutItem> DeviceItems()
        => Shell.DevicePickerMenuItems(Playback.OwnerSignal.Peek(), Playback.ActiveDeviceSlot.Peek(), Playback.Devices.Rows,
            Playback.Audio.Devices.Peek(), Playback.Audio.SelectedOutputId.Peek(), Playback.Audio.Supported.Peek(),
            Playback.Audio.PlayingOpened, Shell.DevicePickerFacts.Quality);
}
