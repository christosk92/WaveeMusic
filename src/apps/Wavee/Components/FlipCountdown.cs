using System;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Zune-style daylist countdown: HH:MM:SS in fixed digit cells that TICK — the old numeral slides up and
/// out while the new one rises in (a keyed remount per cell; the reconciler keeps the exiting orphan painted under the
/// entering digit inside the clipped cell), beside a "Next update at {time}" caption naming the local rollover clock.
///
/// Pure view of wall time — no fetch, no revalidate poke. The existing 60s home revalidate loop +
/// <c>HomeDaylistHydrator</c> swap the feed when the daylist rolls; on the detail page the live store re-map does the
/// same. Once the window closes this parks at a dimmed 00:00:00 and waits for the keyed remount carrying the next
/// window — every mount site keys this component on <c>ExpiresAtMs</c>, because props freeze at mount.
///
/// The clock is a 1s <c>UseInterval</c> (the seconds cell changes on every tick, so no slower rate exists at which a
/// wake redraws an unchanged strip); it auto-pauses while the page is parked/minimized. No tabular figures exist in
/// the text seam, so every numeral sits centered in a fixed-width cell (the CountTicker / HomeCards `.count`
/// discipline) — nothing reflows as the digits spin. Reduced motion degrades the slide to a crossfade
/// (<c>MotionTok</c>'s KeepFade policy), never a hook branch.</summary>
sealed class FlipCountdown : Component
{
    /// <summary>Unix ms when the current daylist window rolls over. ≤ 0 → renders nothing.</summary>
    public required long ExpiresAtMs { get; init; }
    /// <summary>Chrome accent the digits take their HUE from. A thunk (PreReleaseCountdown's pattern): detail pages
    /// derive their accent from art that lands AFTER mount, and reading it inside Render subscribes so the digits
    /// re-tint. The fill is contrast-graded to <see cref="WaveePalette.TextInk"/> — never painted raw on a wash.</summary>
    public required Func<ColorF> Accent { get; init; }
    /// <summary>Rail preset: Body-scale digits for the narrow detail rail. Default = hero scale.</summary>
    public bool Compact;
    /// <summary>Bottom margin the mount site owns (the Home hero reserves its pulse row with one).</summary>
    public float BottomMargin;

    /// <summary>Hero digit-cell height — the row the layout estimators reserve. <c>HomeHeroLayout.PulseBlock</c> and
    /// <c>DetailVerticalLayout.PulseRowHeight</c> restate this number as a literal: both are engine-free test-included
    /// sources that cannot reference this engine-bound component. Changing one means changing all three.</summary>
    public const float HeroRowHeight = 28f;
    /// <summary>Compact (rail) digit-cell height.</summary>
    public const float CompactRowHeight = 20f;

    const float TickMs = 1000f;
    // Interned numerals: a cell's key AND its text — a keyed remount per tick must not also mint strings per second.
    static readonly string[] Numerals = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

    readonly Signal<long> _nowMs = new(0);

    public override Element Render()
    {
        // Seeded on first render rather than at construction: a component built during a parked-page rebuild could
        // otherwise carry a stale "now" until its first tick landed (PreReleaseCountdown's pattern).
        if (_nowMs.Peek() == 0) _nowMs.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        long now = _nowMs.Value;                        // subscribe → re-render on each tick
        long left = Math.Max(0L, ExpiresAtMs - now);
        bool expired = ExpiresAtMs <= 0 || left == 0;

        UseInterval(() => _nowMs.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TickMs, enabled: !expired);

        if (ExpiresAtMs <= 0) return new BoxEl();       // the hooks above always ran — stable order

        // Zune-thin numerals in the page accent's TEXT grade while the window is live. Callers pass the chrome fill
        // (Home's extracted wash / the detail hero Play); painting that fill as ink on a wash mixed from the same hue
        // is unreadable — TextInk solves AA against the card surface and keeps the hue. The strip dims to tertiary
        // once the window closes ("regenerating" — the feed swap re-keys a fresh window in).
        ColorF ink = expired ? Tok.TextTertiary : WaveePalette.TextInk(Accent());
        float cellH = Compact ? CompactRowHeight : HeroRowHeight;
        float cellW = Compact ? 10f : 13f;              // fixed cells in lieu of tabular figures — see the class doc
        float colonW = Compact ? 6f : 8f;
        float size = Compact ? 14f : 20f;

        var t = TimeSpan.FromMilliseconds(left);
        int hours = Math.Min(99, (int)t.TotalHours);    // two fixed cells; a >4-day window clamps rather than reflows

        string timeText = DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMs)
            .ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Margin = new Edges4(0f, 0f, 0f, BottomMargin),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        Digit(hours / 10, cellW, cellH, size, ink),
                        Digit(hours % 10, cellW, cellH, size, ink),
                        Colon(colonW, cellH, size, ink),
                        Digit(t.Minutes / 10, cellW, cellH, size, ink),
                        Digit(t.Minutes % 10, cellW, cellH, size, ink),
                        Colon(colonW, cellH, size, ink),
                        Digit(t.Seconds / 10, cellW, cellH, size, ink),
                        Digit(t.Seconds % 10, cellW, cellH, size, ink),
                    ],
                },
                Caption(Strings.Home.NextUpdateAt(timeText)) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
    }

    /// <summary>One flip cell: a clipped fixed-size window over a SINGLE keyed child. When the numeral changes the key
    /// mismatch remounts it — the old digit exits upward (an orphan drawn under the newcomer, still inside this cell's
    /// clip) while the new one rises from below. Declarative Enter/Exit bake on BoxEl only, hence the wrap.</summary>
    static Element Digit(int value, float w, float h, float size, ColorF ink)
    {
        float rise = MathF.Round(h * 0.35f);
        return new BoxEl
        {
            Width = w, Height = h, ClipToBounds = true, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Key = Numerals[value],
                    Width = w, Height = h,
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Enter = new EnterExit(Dy: rise, Opacity: 0f, Active: true),
                    Exit = new EnterExit(Dy: -rise, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlFast,
                    Children = [Numeral(Numerals[value], size, h, ink)],
                },
            ],
        };
    }

    static Element Colon(float w, float h, float size, ColorF ink) => new BoxEl
    {
        Width = w, Height = h, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Numeral(":", size, h, ink)],
    };

    // Segoe UI Variable Display at a LIGHT weight — the Zune read; the display face is the one the vertical hero
    // title already speaks, so the strip belongs to the page it decorates.
    static TextEl Numeral(string s, float size, float lineH, ColorF ink) => new(s)
    {
        FontFamily = "Segoe UI Variable Display",
        Size = size, Weight = 300, LineHeight = lineH,
        Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
    };
}
