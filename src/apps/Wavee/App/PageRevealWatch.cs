using System;
using System.Globalization;
using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Hosting;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>
/// One observer per retained route. Include is called by the page's actual content builder, not by its query
/// publication: passive effects can publish Ready after the frame has already laid out its skeleton.
/// This measures ready primary content included in a completed UI frame; GPU presentation, image decoding and
/// independently loading secondary widgets are separate observations. It does not claim an on-screen present.
/// </summary>
internal sealed class PageRevealWatch(string route, string? arg)
{
    internal static readonly Context<PageRevealWatch?> Slot = new(null);
    readonly string _route = route;
    readonly string? _arg = arg;
    readonly PageRevealDecision _decision = new(route, arg);
    bool _readyContentIncluded;
    Func<bool>? _currentlyReady;
    string _spec = "";

    // §(startup) — fires exactly once per process, the first time ANY route's primary content is confirmed included
    // in a completed UI frame. This is the "first content painted" signal StartupActivation's deferred Window ladder
    // (OS media surfaces: SMTC, the taskbar thumb bar, the Jump List) waits on before it starts spending UI-thread
    // drains — see WaveeApp's startup-activation effect. Never fires again, and never unfires; a later route's reveal
    // is not this app's FIRST paint and has nothing to gate.
    static bool _firstContentRevealed;
    internal static event Action? FirstContentRevealed;

    public static PageRevealWatch? Use(RenderContext context, Func<bool> currentlyReady, string spec)
    {
        var watch = context.UseContext(Slot);
        var active = context.UseIsActive();
        if (watch is not null)
        {
            // The callback describes the latest rendered page, including any selected master/detail pane.
            watch._currentlyReady = currentlyReady;
            watch._spec = spec;
        }
        context.UseEffect(() =>
        {
            if (watch is null) return (Action?)null;
            void OnFrame(FrameStats frame)
            {
                if (!frame.Rendered || !active.Peek()
                    || !string.Equals(watch._route, NavigationFrameWatch.Route, StringComparison.Ordinal)
                    || !string.Equals(watch._arg ?? "", NavigationFrameWatch.Arg ?? "", StringComparison.Ordinal)) return;
                long navId = NavigationFrameWatch.NavigationId;
                if (!watch._decision.Observe(navId, NavigationFrameWatch.Route, NavigationFrameWatch.Arg,
                    true, true, watch._readyContentIncluded, watch._currentlyReady?.Invoke() == true)) return;
                if (!_firstContentRevealed) { _firstContentRevealed = true; FirstContentRevealed?.Invoke(); }
                double elapsed = NavigationFrameWatch.SinceNavMs;
                WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "page.reveal",
                    "page reveal navId=" + navId + " route=" + watch._route
                    + " arg=" + (string.IsNullOrEmpty(watch._arg) ? "-" : Uri.EscapeDataString(watch._arg))
                    + " spec=" + watch._spec + " boundary=ui-frame revealMs=" + elapsed.ToString("R", CultureInfo.InvariantCulture)
                    + " sinceNavMs=" + elapsed.ToString("R", CultureInfo.InvariantCulture)
                    + " retainedUi=" + (watch._decision.RetainedUi ? "true" : "false")
                    + " readinessAtFirstFrame=" + (watch._decision.ReadyAtFirstFrame ? "ready" : "pending")
                    + " publishSeq=" + frame.PublishSeq);
            }
            FluentApp.FrameCompleted += OnFrame;
            return (Action?)(() => FluentApp.FrameCompleted -= OnFrame);
        }, DepKey.Empty);
        return watch;
    }

    public static Element Include(PageRevealWatch? watch, bool ready, Element content)
    {
        if (watch is not null) watch._readyContentIncluded = ready;
        return content;
    }
}
