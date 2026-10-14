// ── Themes/Classic/Structure/Views/Detail/ClassicPlaylistPanels.cs — Classic's playlist access panel and Tune panel (themes plan 6.1, T9b) ──────────────
//
// From `Entities\Playlist.UI.cs` §5 (W17) and §8 (W24):
//
//   · the INVITE & ACCESS panel: the heading and hint, the "Copy invite link" call to action (the THEME accent on purpose, §4: chrome over the page, not the
//     page), and — for an administrator who can edit the metadata — the Collaborative and Public switches. `Playlist.AccessPanel` keeps the optimistic-switch
//     rule (`OptimisticSwitch`: a switch holds the user's INTENT until an authoritative answer agrees with it, and reverts exactly once, on a refusal), the two
//     cells and the verbs (`Spotify.PlaylistEdits`); the picture is these rows;
//   · the TUNE panel: the accent tile header, the choices (the current one disabled with "Current" trailing) and "Reset tuning" behind a separator ONLY while a
//     tuning is selected (item 56). `PlaylistTuneMenuModel` decides which choices show and whether the reset row stands.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicPlaylist
{
    // ══ 1. THE ACCESS PANEL (W17) ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistAccess"/>.</summary>
    public static Element AccessTemplate(PlaylistAccessProps p, in ViewContext ctx)
    {
        var kids = new List<Element>(6)
        {
            new TextEl(Loc.Get(Strings.Detail.Edit.InviteCollaborators)) { Size = ClassicPlaylistLayout.AccessTitleSize, Weight = 700, Color = Tok.TextPrimary },
            new TextEl(Loc.Get(Strings.Detail.Edit.InviteHint))
                { Size = ClassicPlaylistLayout.AccessHintSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3, MinWidth = 0f },
        };
        if (p.Admin) kids.Add(InviteCta(p));
        if (p.Admin && p.EditableMetadata)
        {
            kids.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, ClassicPlaylistLayout.AccessDividerGap, 0f, ClassicPlaylistLayout.AccessDividerGap) });
            kids.Add(SwitchRow(Loc.Get(Strings.Detail.Edit.Collaborative), Loc.Get(Strings.Detail.Edit.CollaborativeHint),
                p.Collaborative, p.SavingCollab, p.OnCollaborative));
            kids.Add(SwitchRow(Loc.Get(Strings.Detail.Edit.PublicPlaylist),
                Loc.Get(p.IsPublic ? Strings.Detail.Access.PublicCaption : Strings.Detail.Access.PrivateCaption),
                p.Public, p.SavingPublic, p.OnPublic));
        }
        return new BoxEl { Direction = 1, Width = p.Width, Padding = Edges4.All(Spacing.L), Gap = Spacing.M, Children = kids.ToArray() };
    }

    /// <summary>The "Copy invite link" CTA — the THEME accent on purpose (§4: chrome over the page, not the page).</summary>
    static Element InviteCta(PlaylistAccessProps p)
    {
        int state = p.InviteState;
        ColorF fill = Tok.AccentDefault;
        ColorF ink = ColorContrast.PickContrast(fill);
        Element glyph = state == 1
            ? ProgressRing.Indeterminate(ClassicPlaylistLayout.CtaGlyph, foreground: ink)
            : Icon(state == 2 ? Icons.Accept : Icons.Link, ClassicPlaylistLayout.CtaGlyph, ink);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Height = ClassicPlaylistLayout.CtaH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(ClassicPlaylistLayout.CtaRadius), Fill = fill, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, IsEnabled = state != 1, OnClick = p.CopyInvite,
            Children =
            [
                new BoxEl { Key = "cta-ico:" + state, Animate = MotionRecipes.IconSwap, Children = [glyph] },
                new BoxEl
                {
                    Key = "cta-txt:" + (state == 2 ? 1 : 0), Animate = MotionRecipes.TextSwap,
                    Children = [R.Type.DenseTitle(Loc.Get(state == 2 ? Strings.Auth.Copied : Strings.Detail.Edit.CopyInviteLink)) with { Color = ink }],
                },
            ],
        };
    }

    static Element SwitchRow(string label, string caption, Func<FluentGpu.Signals.Signal<bool>> value, bool saving, Action<bool> onChange) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicPlaylistLayout.SwitchLabelGap,
                Children =
                [
                    R.Type.DenseTitle(label) with { Color = Tok.TextPrimary, MaxLines = 1 },
                    R.Type.MicroMeta(caption) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, MinWidth = 0f },
                ],
            },
            saving ? StatusChip(saved: false) : new BoxEl(),
            ToggleSwitch.Create(value(), onChange, isEnabled: !saving),
        ],
    };

    // ══ 2. THE TUNE PANEL (W24) ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistTune"/>. <c>TableProfile.Tune</c> hands no anchor, so the panel opens as a centred
    /// dialog (the picker's reason): the header tile, the choices and, while a tuning is selected, the reset row behind a separator.</summary>
    public static Element TuneTemplate(PlaylistTuneProps p, in ViewContext ctx)
    {
        var options = p.Options;
        var kids = new List<Element>(options.Count + 4)
        {
            new BoxEl
            {
                Direction = 0, Gap = ClassicPlaylistLayout.TuneHeaderGap, AlignItems = FlexAlign.Center,
                Padding = new Edges4(ClassicPlaylistLayout.TuneHeaderPadX, ClassicPlaylistLayout.TuneHeaderPadTop, ClassicPlaylistLayout.TuneHeaderPadX,
                    ClassicPlaylistLayout.TuneHeaderPadBottom),
                Children =
                [
                    new BoxEl
                    {
                        Width = ClassicPlaylistLayout.TuneTile, Height = ClassicPlaylistLayout.TuneTile, Corners = CornerRadius4.All(ClassicPlaylistLayout.TuneTileRadius),
                        Fill = Tok.AccentTextPrimary with { A = ClassicPlaylistLayout.TuneTileWash },
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Icon(Icons.RefineSparkle, ClassicPlaylistLayout.TuneTileGlyph, Tok.AccentTextPrimary)],
                    },
                    new TextEl(Loc.Get(Strings.Detail.Tuning.FlyoutSubtitle))
                        { Size = ClassicPlaylistLayout.TuneSubtitleSize, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3 },
                ],
            },
            TuneDivider(),
        };
        foreach (var option in PlaylistTuneMenuModel.VisibleChoices(options))
            kids.Add(TuneRow(option.DisplayName ?? "", string.Equals(option.Identifier, p.Selected, StringComparison.Ordinal), option.Identifier, p.Pick));
        if (PlaylistTuneMenuModel.ResetOption(options, p.Selected) is { } reset)
        {
            kids.Add(TuneDivider());
            kids.Add(TuneRow(Loc.Get(Strings.Detail.Tuning.Reset), false, reset.Identifier, p.Pick, radio: false));
        }
        return new BoxEl
        {
            Direction = 1, Width = p.Width,
            Padding = new Edges4(0f, ClassicPlaylistLayout.TunePadTop, 0f, ClassicPlaylistLayout.TunePadBottom), Children = kids.ToArray(),
        };
    }

    static Element TuneDivider() => new BoxEl
    {
        Height = 1f, Fill = Tok.StrokeDividerDefault,
        Margin = new Edges4(ClassicPlaylistLayout.TuneDividerX, ClassicPlaylistLayout.TuneDividerTop, ClassicPlaylistLayout.TuneDividerX, ClassicPlaylistLayout.TuneDividerBottom),
    };

    static Element TuneRow(string label, bool current, string identifier, Action<string> pick, bool radio = true) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, Height = ClassicPlaylistLayout.TuneRowH, AlignItems = FlexAlign.Center,
        Padding = new Edges4(ClassicPlaylistLayout.TuneRowPadL, 0f, ClassicPlaylistLayout.TuneRowPadR, 0f),
        Corners = Radii.ControlAll, HoverFill = current ? ColorF.Transparent : Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = radio ? AutomationRole.RadioButton : AutomationRole.MenuItem, Cursor = current ? CursorId.Arrow : CursorId.Hand,
        IsEnabled = !current, OnClick = current ? null : () => pick(identifier),
        Children =
        [
            radio
                ? new BoxEl
                {
                    Width = ClassicPlaylistLayout.RadioEdge, Height = ClassicPlaylistLayout.RadioEdge, Corners = CornerRadius4.All(ClassicPlaylistLayout.RadioEdge / 2f),
                    BorderWidth = 1f, Shrink = 0f,
                    BorderColor = current ? Tok.AccentTextPrimary : Tok.TextSecondary, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = current
                        ? [new BoxEl
                        {
                            Width = ClassicPlaylistLayout.RadioDot, Height = ClassicPlaylistLayout.RadioDot,
                            Corners = CornerRadius4.All(ClassicPlaylistLayout.RadioDot / 2f), Fill = Tok.AccentTextPrimary,
                        }]
                        : [],
                }
                : new BoxEl { Width = ClassicPlaylistLayout.RadioEdge, Shrink = 0f },
            new TextEl(label)
            {
                Size = ClassicPlaylistLayout.TuneRowText, Color = current ? Tok.TextSecondary : Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            },
            current ? new TextEl(Loc.Get(Strings.Detail.Tuning.Current)) { Size = ClassicPlaylistLayout.TuneCurrentText, Color = Tok.TextTertiary } : new BoxEl(),
        ],
    };
}
