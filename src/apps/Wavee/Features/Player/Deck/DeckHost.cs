using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Features.Player.Deck.Model;

namespace Wavee;

/// <summary>
/// The square that IS a deck: a layout firewall holding exactly two children — the face (built once, all binds) and
/// <see cref="DeckClock"/> (a 0x0 box that owns the only timer).
///
/// <para><b>Why it is the firewall.</b> <c>Width</c>+<c>Height</c>+<c>ClipToBounds</c>+<c>IsolateLayout</c> declare
/// that this box's size is parent-determined, so nothing a deck does inside — a re-rendered cover leaf, a 1 Hz
/// clock TextEl, a whole face rebuilt because an option flipped — can escape into the rail, the panel below it, or
/// the page. That guarantee is the entire reason "everything below the hero never moves" is true rather than hoped
/// for.</para>
///
/// <para><b>The model is created ONCE, from the CURRENT transport.</b> Not from a rest pose: switching Cover -&gt;
/// Player mid-song must show a record that is already playing, so <see cref="DeckClock.Seed"/> folds the transport
/// exactly as the ticker would and the model seeds itself from it. The clock then re-folds the same shape 30 times
/// a second — one fold function, so "mounted" and "running" cannot disagree.</para>
///
/// <para><b>What re-renders this component.</b> Only the prefs epoch (an option flip, which must restyle the face
/// in place) and a context change. A PRESET change is a different deck: the shell keys the hero slot on the preset
/// slug, so that is a remount — which is also what discards the old model.</para>
/// </summary>
sealed class DeckHost : Component
{
    /// <summary>320 ms opacity entrance. Reduced motion deliberately KEEPS the fade: this is a cross-fade between
    /// two hero contents, not decoration, and snapping it reads as a glitch. It declares no geometry channel, so
    /// there is nothing here for reduced motion to suppress.</summary>
    static readonly LayoutTransition Entrance = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(320f, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>Which deck. Frozen at mount by design — the shell remounts the hero slot on a preset change.</summary>
    public required NpvPlayerCatalog.Preset Preset;

    /// <summary>The deck's square edge in DIP, handed down by the shell (the rail's content width). Frozen at mount
    /// with <see cref="Preset"/> — see <see cref="NpvDeck.Create(Wavee.Core.Track, NpvPlayerCatalog.Preset, float)"/>.</summary>
    public float Side = NpvDeck.DefaultSide;

    /// <summary>Raised on the needle-drop edge — a face subscribes to fire its one-shot dust puff / body knock.
    /// Forwarded from <see cref="DeckClock"/> so a face never has to know the ticker exists.</summary>
    public Action? ThumpRequested;

    /// <summary>THE scrub gesture for this deck (headshell drag, click wheel, Winamp slider). One per host, so two
    /// grips on the same face cannot fight over <c>ScrubTargetMs</c>.</summary>
    public DeckGesture Gesture { get; } = new();

    readonly DeckSignals _sig = new();
    IDeckModel? _model;
    IAppSettings? _settings;
    float _side = NpvDeck.DefaultSide;

    public override Element Render()
    {
        var slot = UseContext(PlaybackBridge.Slot);
        _settings = UseContext(Services.Slot)?.Settings;
        _ = NpvPlayerPrefs.Epoch.Value;   // an option flip restyles the mounted deck IN PLACE (no remount)

        _side = Side > 0f ? Side : NpvDeck.DefaultSide;
        float side = _side;
        if (slot is null) return new BoxEl { Width = side, Height = side, Shrink = 0f };

        PlaybackBridge bridge = slot;
        Gesture.Attach(bridge);
        if (_model is null) _model = DeckModels.Create(Preset, _settings, bridge, DeckClock.Seed(bridge, CurrentRpm()));
        IDeckModel model = _model;

        var face = DeckFaces.Create(Preset, _settings, side, _sig, bridge, this);
        var signals = _sig;
        return new BoxEl
        {
            Width = side,
            Height = side,
            Shrink = 0f,
            ClipToBounds = true,
            IsolateLayout = true,
            Corners = CornerRadius4.All(Radii.Card),
            Animate = Entrance,
            Children =
            [
                face,
                Embed.Comp(() => new DeckClock
                {
                    Bridge = bridge,
                    Model = model,
                    Out = signals,
                    UsesLevels = Preset.Id is NpvPlayerCatalog.Vu or NpvPlayerCatalog.Winamp or NpvPlayerCatalog.Wmp,
                    Gesture = Gesture,
                    ThumpRequested = RaiseThump,
                    Rpm = CurrentRpm,
                    AngleQuantumDeg = CurrentAngleQuantum,
                }) with { Key = "deck-clock" },
            ],
        };
    }

    /// <summary>The turntable speed the deck is set to. A METHOD, not a captured value: the ticker holds this as a
    /// delegate for the deck's whole life and must see a 33 -&gt; 45 flip the moment it is written.</summary>
    float CurrentRpm() => NpvPlayerPrefs.ChoiceSlug(_settings, Preset, "rpm") == "45" ? 45f : 33.333f;

    /// <summary>One rim pixel of rotation. A disc's rim travels PI*d per turn, so 360/(PI*d) degrees is the smallest
    /// angle that can move a pixel; below it an angle write is invisible and the clock gates it away. 0.70 is the
    /// record family's disc diameter as a fraction of the deck — the widest rotating part, so the quantum is the
    /// finest any part needs.</summary>
    float CurrentAngleQuantum() => 360f / MathF.Max(1f, MathF.PI * _side * 0.70f);

    void RaiseThump() => ThumpRequested?.Invoke();
}
