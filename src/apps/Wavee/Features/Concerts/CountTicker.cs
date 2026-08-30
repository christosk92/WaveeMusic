using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Animated integer readout (concerts v2 §3.4). The parent hands the debounced target count; while a tween is
/// active a keyed child clock re-renders THIS LEAF every frame (never the page — the virtual-list remount gotcha) and
/// eases the shown value; on settle the clock unmounts and the component costs nothing per frame. ~380ms cubic ease-out.
/// A reserved MinWidth + right-aligned number keeps the caption row from reflowing while the digits spin (no tabular
/// numerals in the text stack).</summary>
sealed class CountTicker : Component
{
    public required IReadSignal<int?> Target;
    /// <summary>Localized noun rendered after the number ("events"). Empty ⇒ number only.</summary>
    public string Suffix = "";

    readonly Signal<double> _shown = new(0);
    int _animatingTo = int.MinValue;
    double _from;
    long _startTicks;

    public override Element Render()
    {
        int target = Target.Value ?? 0;                    // subscribe: a new debounced count re-renders us
        bool animating = (int)Math.Round(_shown.Peek()) != target;
        if (animating && _animatingTo != target) { _animatingTo = target; _from = _shown.Peek(); _startTicks = 0; }

        var text = WaveeType.TrackTitle(((int)Math.Round(_shown.Value)).ToString("N0", CultureInfo.CurrentCulture))
            with { MaxLines = 1 };
        var kids = new List<Element>(3)
        {
            // FIXED width (not Min): the spinning digits change text width every frame mid-tween, and a growing box
            // would re-layout the caption row per frame. 64 DIPs right-aligned holds any plausible five-digit count.
            new BoxEl { Width = 64f, Direction = 0, Justify = FlexJustify.End, Children = [ text ] },
        };
        if (Suffix.Length > 0)
            kids.Add(Body(" " + Suffix) with { Color = Tok.TextSecondary, MaxLines = 1 });
        if (animating)
            kids.Add(Embed.Comp(() => new TickerClock
            {
                OnFrame = now =>
                {
                    if (_startTicks == 0) _startTicks = now;
                    double p = Math.Min(1.0, (now - _startTicks) / (double)TimeSpan.FromMilliseconds(380).Ticks);
                    p = 1 - Math.Pow(1 - p, 3);
                    _shown.Value = _from + (_animatingTo - _from) * p;
                },
            }) with { Key = "count-tick:" + target });     // retarget mid-flight = remount = clean restart
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f, Children = kids.ToArray() };
    }
}

/// <summary>ShyPillLingerClock's shape: an invisible node subscribed to FrameClock.Tick, calling <see cref="OnFrame"/>
/// with the frame timestamp while mounted. One per-frame wake, only while a count tween is in flight.</summary>
sealed class TickerClock : Component
{
    public required Action<long> OnFrame;

    public override Element Render()
    {
        long tick = UseContext(FrameClock.Tick);           // re-render every frame while mounted (during the tween)
        UseEffect(() => OnFrame(DateTime.UtcNow.Ticks), tick);
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
