using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Local equivalents of <c>SettingsPage</c>'s private row helpers (Features/Shell/SettingsPage.cs:183-262 —
/// <c>SettingsRow</c>/<c>SettingsItem</c>/<c>SettingsSectionHeader</c>/<c>SettingsValueTag</c>/
/// <c>SettingsExpanderPanel</c>). Those are <c>static</c> members of <c>sealed partial class SettingsPage</c> and
/// therefore unreachable from a sibling type outside that partial-class family — this file is NOT a partial of
/// <c>SettingsPage</c> (out of scope for this step), so every option below is copied verbatim rather than shared, on
/// purpose: a setup-page row reads pixel-for-pixel like its Settings-tab counterpart.</summary>
static class SetupRows
{
    static readonly Edges4 SectionHeaderMargin = new(0f, Spacing.XXXL, 0f, Spacing.S);

    /// <summary>A group eyebrow: icon + bold title, optionally a one-line caption. Verbatim copy of
    /// <c>SettingsPage.SettingsSectionHeader</c>.</summary>
    public static Element SectionHeader(string title, string? icon = null, string? subtitle = null)
    {
        Element text = subtitle is { Length: > 0 } sub
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    BodyStrong(title),
                    Caption(sub) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2 },
                ],
            }
            : BodyStrong(title);

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            AlignItems = subtitle is { Length: > 0 } ? FlexAlign.Start : FlexAlign.Center,
            Margin = SectionHeaderMargin,
            AlignSelf = FlexAlign.Stretch,
            Children = icon is null
                ? [text]
                : [Icon(icon, 16f, Tok.TextSecondary) with { Margin = new Edges4(0f, 2f, 0f, 0f) }, text],
        };
    }

    /// <summary>A single settings row. Verbatim copy of <c>SettingsPage.SettingsRow</c>.</summary>
    public static Element Row(string label, string? sub, Element? control = null, string? icon = null,
                               SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                               bool isClickEnabled = false, Action? onClick = null, bool isEnabled = true)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = icon,
            Content = control,
            Alignment = align,
            IsClickEnabled = isClickEnabled,
            IsActionIconVisible = isClickEnabled,
            OnClick = onClick,
            IsEnabled = isEnabled,
        });

    /// <summary>A row that lives inside an expander body. Verbatim copy of <c>SettingsPage.SettingsItem</c>.</summary>
    public static Element Item(string label, string? sub, Element? control = null,
                                SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                                bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null,
                                string? icon = null)
        => SettingsExpander.Item(label, sub, control, align, isEnabled, isClickEnabled, onClick, icon);

    /// <summary>What a COLLAPSED group is currently set to. Verbatim copy of <c>SettingsPage.SettingsValueTag</c>.</summary>
    public static Element ValueTag(string value) => new TextEl(value)
    {
        Size = 14f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>Wide content inside a <see cref="SettingsExpander"/>'s <c>ItemsHeader</c>/<c>ItemsFooter</c> slot.
    /// Verbatim copy of <c>SettingsPage.SettingsExpanderPanel</c>.</summary>
    public static Element ExpanderPanel(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Children = [content],
    };

    /// <summary>A vertically-stacked column of rows/headers with the Settings tab's own row spacing. Verbatim copy of
    /// <c>SettingsPage.SettingsTabStack</c>.</summary>
    public static Element Stack(params Element[] children) => new BoxEl
    {
        Direction = 1, Gap = 4f, AlignSelf = FlexAlign.Stretch, Children = children,
    };

    /// <summary>A plain descriptive paragraph (Terms' lead, a page's own lead). Not a Settings-tab shape — the setup
    /// pages are the only place in the app that opens a step with running prose above its rows.</summary>
    public static TextEl Lead(string text) => Body(text) with
    {
        Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
    };
}
