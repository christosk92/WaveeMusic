// ── Themes/Classic/Structure/Views/Detail/ClassicPlaylistPanels.cs — Classic's playlist access panel and Tune strip (themes plan 6.1, T9b) ──────────────
//
// From `Entities\Playlist.UI.cs` §5 (W17) and §8 (W24):
//
//   · the INVITE & ACCESS panel: the heading and hint, the "Copy invite link" call to action (the THEME accent on purpose, §4: chrome over the page, not the
//     page), and — for an administrator who can edit the metadata — the Collaborative and Public switches. `Playlist.AccessPanel` keeps the optimistic-switch
//     rule (`OptimisticSwitch`: a switch holds the user's INTENT until an authoritative answer agrees with it, and reverts exactly once, on a refusal), the two
//     cells and the verbs (`Spotify.PlaylistEdits`); the picture is these rows;
//   · the TUNE strip, inline under the list bar while Tune is open: its head, the directions as chips, and "Reset tuning" ONLY while a tuning is selected
//     (item 56). `PlaylistTuneMenuModel` decides which choices show and whether Reset stands.

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

    // ══ 2. THE TUNE STRIP (W24; inline since 2026-11-18) ══════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistTune"/>: the strip under the list bar while Tune is open (owner, 2026-11-18: "not a dialog but
    /// a nicer experience"). A plate on the AI wash with the AI stroke: its head (the sparkle tile; "Tune this mix" over the line that says what the mix follows,
    /// that it is rebuilding, or that the last pick failed; Reset while tuned; the close), then the directions as the stock filter chips. A chip applies at once:
    /// the mix's own direction is on; while one applies that one is on, the others wait and the head's ring turns.</summary>
    public static Element TuneTemplate(PlaylistTuneProps p, in ViewContext ctx)
    {
        var choices = PlaylistTuneMenuModel.VisibleChoices(p.Options);
        var reset = PlaylistTuneMenuModel.ResetOption(p.Options, p.Selected);
        bool busy = p.Applying is not null;
        var pick = p.Pick;

        var chips = new Element[choices.Count];
        for (int i = 0; i < choices.Count; i++)
        {
            string id = choices[i].Identifier;
            bool on = busy ? id == p.Applying : id == p.Selected;
            chips[i] = Controls.Chip(choices[i].DisplayName ?? "", on, !busy, busy ? null : () => pick(id)) with { Key = "tune:" + id };
        }

        var head = new List<Element>(5)
        {
            new BoxEl
            {
                Width = ClassicPlaylistLayout.TuneTile, Height = ClassicPlaylistLayout.TuneTile, Shrink = 0f, Corners = CornerRadius4.All(ClassicPlaylistLayout.TuneTileRadius),
                Gradient = ClassicPlaylistLayout.TuneWash, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.RefineSparkle, ClassicPlaylistLayout.TuneTileGlyph, ClassicPlaylistLayout.TuneSparkle)],
            },
            TuneLines(p, choices, busy),
            new BoxEl { Grow = 1f, MinWidth = 0f },
        };
        if (reset is { } r && !busy)
        {
            string resetId = r.Identifier;
            head.Add(HyperlinkButton.Create(Loc.Get(Strings.Detail.Tuning.Reset), () => pick(resetId)) with { Key = "tune:reset", Shrink = 0f });
        }
        head.Add(ToolTip.Wrap(Controls.IconAction(Icons.ChromeClose, p.Close, size: ClassicPlaylistLayout.TuneClose) with { Key = "tune:close" },
            Loc.Get(Strings.Detail.Tuning.Close)));

        return new BoxEl
        {
            Key = "tune-strip", Direction = 1, Gap = ClassicPlaylistLayout.TuneStripGap, Padding = Edges4.All(ClassicPlaylistLayout.TuneStripPad), MinWidth = 0f,
            Corners = Radii.CardAll, Gradient = ClassicPlaylistLayout.TuneWash,
            BorderBrush = ClassicPlaylistLayout.TuneStroke, BorderWidth = ClassicPlaylistLayout.TuneStrokeWidth,
            Children =
            [
                new BoxEl { Key = "tune-strip:head", Direction = 0, Gap = ClassicPlaylistLayout.TuneHeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = head.ToArray() },
                new BoxEl { Key = "tune-strip:chips", Direction = 0, Wrap = true, Gap = ClassicPlaylistLayout.TuneChipGap, MinWidth = 0f, Children = chips },
            ],
        };
    }

    /// <summary>"Tune this mix" over its line: the failure, the rebuild (with the ring), the direction the mix follows, or the invitation.</summary>
    static Element TuneLines(PlaylistTuneProps p, List<TuneOption> choices, bool busy)
    {
        string? name = null;
        if (p.Selected is { } selected) foreach (var c in choices) if (c.Identifier == selected) { name = c.DisplayName; break; }
        string line = p.Failed ? Loc.Get(Strings.Detail.Tuning.ApplyFailed)
            : busy ? Loc.Get(Strings.Detail.Tuning.Applying)
            : name is not null ? Strings.Detail.Tuning.Tuned(direction: name)
            : Loc.Get(Strings.Detail.Tuning.Subtitle);
        var text = new TextEl(line)
        {
            Size = ClassicPlaylistLayout.TuneLineSize, Color = p.Failed ? Tok.SystemFillCritical : Tok.TextSecondary,
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        Element row = busy
            ? new BoxEl
            {
                Direction = 0, Gap = ClassicPlaylistLayout.TuneRingGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [ProgressRing.Create(size: ClassicPlaylistLayout.TuneRing), text],
            }
            : text;
        return new BoxEl
        {
            Direction = 1, Gap = ClassicPlaylistLayout.TuneLineGap, Shrink = 1f, MinWidth = 0f,
            Children =
            [
                new TextEl(Loc.Get(Strings.Detail.Tuning.Title))
                    { Size = ClassicPlaylistLayout.TuneTitleSize, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                row,
            ],
        };
    }
}
