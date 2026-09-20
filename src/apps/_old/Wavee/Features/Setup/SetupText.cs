using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The wizard page body vocabulary — Rise's own <c>StackPanel Spacing="20"</c> of
/// <c>BaseTextBlockStyle</c>/<c>BodyTextBlockStyle</c> text and <c>labs:SettingsCard</c>s. Replaces
/// <c>SetupStage</c>/<c>SetupDecision</c>/<c>SetupCompact</c>/<c>SetupRows</c>/<c>SetupType</c> wholesale — there is
/// no stage/decision split, no hero rail, no chips/rows any more; every page is one content column built from these
/// five primitives.</summary>
static class SetupText
{
    /// <summary>The page body's own top-level rhythm — Rise's <c>StackPanel Spacing="20"</c>.</summary>
    public static Element Stack(params Element[] kids) =>
        new BoxEl { Direction = 1, Gap = SetupLayout.BodySpacing, MinWidth = 0f, Children = kids };

    /// <summary>A tighter inner group (a card's own label + value, a lead + its detail row) — Rise's inner
    /// <c>Spacing="12"</c>.</summary>
    public static Element Group(params Element[] kids) =>
        new BoxEl { Direction = 1, Gap = SetupLayout.BodyInnerSpacing, MinWidth = 0f, Children = kids };

    /// <summary>A page's lead paragraph — <c>BaseTextBlockStyle</c> (14/600), the one-notch-up rung a Rise page opens
    /// its body with, above the plain 14/400 text below it.</summary>
    public static Element Lead(string s) => BodyStrong(s) with { Wrap = TextWrap.Wrap, MinWidth = 0f };

    /// <summary>Plain body copy — <c>BodyTextBlockStyle</c> (14/400).</summary>
    public static Element Body(string s) => Ui.Body(s) with { Wrap = TextWrap.Wrap, MinWidth = 0f };

    /// <summary>Fine print / captions — secondary ink at the same 14-px body size (WinUI's
    /// <c>ApplicationSecondaryForeground</c> override, not a smaller caption rung).</summary>
    public static Element Secondary(string s) => Ui.Body(s).Secondary() with { Wrap = TextWrap.Wrap, MinWidth = 0f };

    /// <summary>The one card shape every page reaches for — a thin wrapper over the engine's <c>SettingsCard</c> so a
    /// page never hand-rolls its own card chrome.</summary>
    public static Element Card(string header, string? description, string? glyph = null, Element? content = null, Action? onClick = null) =>
        SettingsCard.Create(new SettingsCard.Options
        {
            Header = header,
            Description = description,
            HeaderIcon = glyph,
            Content = content,
            IsClickEnabled = onClick is not null,
            OnClick = onClick,
        });
}
