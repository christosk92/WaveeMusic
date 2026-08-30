using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The shared page frame every real setup page uses: a [<see cref="SetupStage"/> column] beside [a content
/// column: a pinned eyebrow+title(+lead) header over a body]. The stage column drops out ENTIRELY under width
/// pressure according to <see cref="SetupLayout"/>.
///
/// <para><see cref="Frame"/> defers to a <see cref="Component"/> (<see cref="SetupPageFrame"/>) purely so the
/// stage-drop can read <c>Viewport.Size</c> LIVE — a window resize re-evaluates it without remounting the page body
/// underneath, the same reason <c>ContentHost.PageFor</c> (Features/Shell/ContentHost.cs) wraps every real page in
/// its own <c>Embed.Comp</c> rather than reading context itself: a bare static function has no hook context of its
/// own to read <c>Viewport.Size</c> from.</para></summary>
static class SetupPageHost
{
    /// <param name="pinnedHeader">False omits the eyebrow+title row entirely — the prototype's two Zune bookends
    /// (Welcome, Done) are <c>.col.solo</c>: they carry their own display headline inside the body and must NOT
    /// reserve a second header above it, which would leave a blank band.</param>
    /// <param name="lead">An optional one-line (or <paramref name="leadMaxLines"/>-line) paragraph rendered inside the
    /// pinned header, under the title — every decision-column budget in <see cref="SetupLayout"/> assumes this lives
    /// in the header, not the body, which is why <see cref="SetupLayout.DecisionBodyBudget"/> takes a line count.</param>
    /// <param name="stage">The page's own stage-column content (built with <see cref="SetupStage"/>). Null falls back
    /// to a bare <see cref="SetupStage.Rail"/> — every page gets SOME stage even before its own work package composes
    /// a richer one.</param>
    /// <param name="scrollBody">At Wide only: false swaps the always-<see cref="ScrollEl"/> body for a plain clipped
    /// column, so a page whose content fits its budget (verified against <see cref="SetupLayout.FitsWide"/>) can pin a
    /// footer line to its own floor with a <c>Grow</c> spacer — a <see cref="ScrollEl"/> viewport sizes to its content
    /// and has nothing for that spacer to grow into. Below Wide the body is ALWAYS scrollable regardless (the
    /// safety net for whatever didn't fit once the stage column dropped).</param>
    public static Element Frame(SetupPage page, string eyebrow, string title, Element body, bool pinnedHeader = true,
        string? lead = null, int leadMaxLines = 1, Element? stage = null, bool scrollBody = true)
        => Embed.Comp(new SetupPageFrame.Props(page, eyebrow, title, body, pinnedHeader, lead, leadMaxLines, stage, scrollBody),
            () => new SetupPageFrame()) with { Key = "setup:frame:" + (int)page };

    internal static float Width => SetupLayout.StageWidth;
}

/// <summary>The live-responsive half of <see cref="SetupPageHost.Frame"/>. The frame receives its slots through pushed
/// props: a page identity is stable inside one KeepAlive entry, but its body/stage are rebuilt when page-local signals
/// change (notably when Spotify publishes a pairing challenge, or Appearance rebuilds its live-preview stage on every
/// epoch bump). Passing those through the constructor would freeze the first tree forever, because a reused
/// <see cref="Component"/> never re-runs its factory.</summary>
sealed class SetupPageFrame : Component
{
    internal sealed record Props(SetupPage Page, string Eyebrow, string Title, Element Body, bool PinnedHeader,
        string? Lead, int LeadMaxLines, Element? Stage, bool ScrollBody);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        var tier = tierSig.Value;
        bool showStage = SetupLayout.ShowsHero(tier) && HeroView.Exists(p.Page);

        Element? leadEl = p.Lead is { Length: > 0 } leadText
            ? Body(leadText).Secondary() with
            {
                MinWidth = 0f, MaxLines = p.LeadMaxLines, Trim = TextTrim.WordEllipsis,
                Wrap = p.LeadMaxLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap,
            }
            : null;

        var headerChildren = new List<Element>(3)
        {
            WaveeType.Eyebrow(p.Eyebrow) with
                { Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            WaveeType.PageHero(p.Title) with
                { FontFamily = "Segoe UI Variable Display", MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis },
        };
        if (leadEl is not null) headerChildren.Add(leadEl);

        Element header = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f, MinWidth = 0f,
            Children = headerChildren.ToArray(),
        };

        // Below Wide (no stage column) the body is ALWAYS scrollable — the safety net for whatever didn't fit once
        // the stage dropped. At Wide, honor the page's own `scrollBody` (a page that fits its budget can pin a
        // footer line to the floor with a Grow spacer instead, which a ScrollEl viewport has nothing to grow into).
        bool scroll = !showStage || p.ScrollBody;
        Element bodyEl = scroll
            ? ScrollView(p.Body) with { Shrink = 1f, MinWidth = 0f, MinHeight = 0f }
            : new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true, Children = [p.Body] };

        Element content = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.M,
            // A headerless page is a Zune bookend, and those CENTER their block vertically (the prototype's
            // `.col.solo` + `justify-content:center`). A ScrollEl viewport sizes itself to its content, so a child
            // asking for Grow=1f/Justify=Center inside one has nothing to grow into and silently pins to the top —
            // which is exactly what it did. Bookends are authored to fit the plate, so they take the column directly.
            Children = p.PinnedHeader ? [header, bodyEl] : [p.Body],
        };

        bool clearBack = !showStage && p.Page is >= SetupPage.SignIn and <= SetupPage.Notifications;
        var padding = new Edges4(
            Spacing.XXL,
            clearBack ? Spacing.XXXL + Spacing.XXL : Spacing.XXL,
            Spacing.XXL,
            Spacing.M);

        if (!showStage)
            return new BoxEl
            {
                Key = "setup:layout:" + (int)tier,
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Padding = padding, Children = [content],
            };

        return new BoxEl
        {
            Key = "setup:layout:" + (int)tier,
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Gap = Spacing.XXL, Padding = padding,
            Children =
            [
                p.Stage ?? SetupStage.Rail(p.Page),
                content,
            ],
        };
    }
}
