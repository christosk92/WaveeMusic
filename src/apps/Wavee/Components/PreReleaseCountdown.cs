using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The countdown card for an announced-but-unreleased album: an eyebrow, a live days/hours/minutes/seconds
/// breakdown, and a spinning ring that says "not yet".
///
/// Shown on the album detail rail (from <c>DetailModel.IsPreRelease</c>/<c>PreReleaseEnd</c>) and on the artist page
/// (from <c>ArtistExtras.PreRelease</c>). Both carry the same thing — an instant to count down to — so this takes the
/// instant and nothing else, and neither surface owns a second copy of the formatting.
///
/// The ring is the stock indeterminate <see cref="ProgressRing"/>, not a determinate sweep: a "fraction of the wait
/// elapsed" arc needs an announcement instant, and the wire never states one — a determinate ring here could only ever
/// draw an empty track. The indeterminate spin is GPU-side (a looping keyframe track), so it costs no per-frame C#.
///
/// The clock is <c>UseInterval</c>, not a <c>System.Threading.Timer</c>: detail pages are parked by
/// <c>Flow.KeepAlive</c> rather than unmounted, and UseInterval auto-pauses while parked or minimized. A raw timer
/// would keep waking the app to re-render a card nobody is looking at.</summary>
sealed class PreReleaseCountdown : Component
{
    /// <summary>When the release unlocks. Frozen at mount — the caller keys this component on it.</summary>
    public required DateTimeOffset ReleaseAt { get; init; }
    /// <summary>Accent for the ring, so the card belongs to the page it sits on. A thunk rather than a value because the
    /// artist page derives its accent from art that lands AFTER the page mounts — reading it inside Render subscribes,
    /// so the ring re-tints when the palette arrives instead of staying frozen at the mount-time default.</summary>
    public required Func<ColorF> Accent { get; init; }

    /// <summary>The tone panels mount this bare — their own plate + eyebrow already say what it is; a second card
    /// ring inside a card is double chrome. When true, Render returns only the tiles / "out now" line: no plate
    /// BoxEl, no Fill/Border/Padding, no ProgressRing, no eyebrow.</summary>
    public bool Bare { get; init; }

    const float RingSize = 34f;

    // One rate, always: the seconds tile changes on every tick, so there is no interval at which a wake redraws an
    // unchanged card. Both mount sites are singular (one masthead, one rail card), so a 1 Hz wake is cheap.
    const float TickMs = 1000f;

    readonly Signal<long> _nowTicks = new(0);

    public override Element Render()
    {
        // Seeded on first render rather than at construction: a component built during a parked-page rebuild could
        // otherwise carry a stale "now" until its first tick landed.
        if (_nowTicks.Peek() == 0) _nowTicks.Value = DateTimeOffset.UtcNow.UtcTicks;

        var now = new DateTimeOffset(_nowTicks.Value, TimeSpan.Zero);   // subscribe → the copy re-renders on each tick
        TimeSpan remaining = ReleaseAt - now;
        bool released = remaining <= TimeSpan.Zero;

        UseInterval(() => _nowTicks.Value = DateTimeOffset.UtcNow.UtcTicks, TickMs, enabled: !released);

        var accent = Accent();   // read inside Render → subscribes, so a late palette re-tints the ring

        if (Bare) return released ? OutNow() : Tiles(remaining);

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                // isActive: !released ⇒ the control's own Inactive state fades it out once the wait is over.
                ProgressRing.Indeterminate(size: RingSize, foreground: accent, isActive: !released),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Grow = 1f,
                    Children =
                    [
                        // AccentDecor — the countdown's accent IS the release's colour. Kept; only case/tracking moved.
                        WaveeType.Eyebrow(Loc.Get(Strings.Detail.PreReleaseEyebrow)) with
                        {
                            Color = accent, MaxLines = 1,
                        },
                        released ? OutNow() : Tiles(remaining),
                    ],
                },
            ],
        };
    }

    static Element OutNow() => new TextEl(Loc.Get(Strings.Detail.PreReleaseOut))
    {
        Size = 15f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The four unit tiles. Wrap-grow (FlexLayout.ArrangeWrap) fills each line edge-to-edge, so they flow 2×2
    /// in a narrow rail column and sit on one row when the card is wide.</summary>
    static Element Tiles(TimeSpan remaining)
    {
        var (d, h, m, s) = Breakdown(remaining);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, Wrap = true, MinWidth = 0f,
            Children =
            [
                UnitTile(d.ToString(), Loc.Get(Strings.Detail.PreReleaseUnitDays)),        // natural width — a wait can be 100+ days
                UnitTile(h.ToString("D2"), Loc.Get(Strings.Detail.PreReleaseUnitHours)),   // clock convention: zero-padded
                UnitTile(m.ToString("D2"), Loc.Get(Strings.Detail.PreReleaseUnitMinutes)),
                UnitTile(s.ToString("D2"), Loc.Get(Strings.Detail.PreReleaseUnitSeconds)),
            ],
        };
    }

    /// <summary>The per-unit remainder, NOT a cumulative total: <c>TimeSpan.Days/.Hours/.Minutes/.Seconds</c> already
    /// decompose that way. Clamped at zero so a tick that lands a hair past the instant (the <c>released</c> gate flips
    /// on the next render, not mid-frame) can never render a negative tile.</summary>
    internal static (int Days, int Hours, int Minutes, int Seconds) Breakdown(TimeSpan left)
        => (Math.Max(0, left.Days), Math.Max(0, left.Hours), Math.Max(0, left.Minutes), Math.Max(0, left.Seconds));

    // The compact stat tile of the album facts bento (DetailTrailing.CompactStatTile), which sits directly below this
    // card on the rail — same 18px value / 11px caption recipe so the two blocks read as one column. Ten lines mirrored
    // rather than shared: that helper is private to its own page and this is styling, not a contract.
    static Element UnitTile(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
        Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(value) { Size = 18f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(label) { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };
}
