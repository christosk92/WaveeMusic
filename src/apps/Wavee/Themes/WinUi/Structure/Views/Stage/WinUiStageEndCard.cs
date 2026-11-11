// ── Themes/WinUi/Structure/Views/Stage/WinUiStageEndCard.cs — WinUI's `Stage.EndCard` (Video mode, the end of a music video) ─────────────────
//
// The picture's last frame stays where it is, dimmed (the picture chrome's veil); over it a ContentDialog-like smoke plate (r8, the dialog's 24 padding,
// a stroke and the dialog's shadow): "Up next in 5" counting down, a gap above the next track's 16:9 thumbnail (the stage's thumbnail: the video's
// still, or the audio-only cover inset on a plate, tagged) ringed in the accent with a thin progress line at its foot, and beside it the title
// (Subtitle), the artist, "From ..." and the dialog's command row: Play now (accent) and Cancel. CINEMA centres the card between the top bar and the
// shelf's reserved place (the shelf below lists the tracks after this one: "Then"); THEATRE centres it in the picture's box, the thumbnail fitted to
// the box. The countdown is the core's (one timer): the number ticks through a bound text, the progress line is one linear transition from where the
// countdown stands at the card's mount to its end, so nothing renders per frame.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using G = Wavee.Themes.WinUi.WinUiStageEndCardGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's end-of-video card (<see cref="ViewKeys.StageEndCard"/>).</summary>
internal sealed class WinUiStageEndCard : Component
{
    /// <summary>The registered template of <see cref="ViewKeys.StageEndCard"/>.</summary>
    public static Element Template(StageEndCardProps props, in ViewContext ctx)
        => Embed.Comp(new Props(props, WinUiStageTiers.StageOf(ctx.Tier())), static () => new WinUiStageEndCard());

    sealed record Props(StageEndCardProps Card, WinUiStageLayout Tier);

    /// <summary>The countdown left at the card's mount (ms): the progress line's one transition runs that long.</summary>
    int _leftAtMount = -1;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var tier = p.Tier;
        var st = UseContext(Stage.StageContext)!;
        var L = st.Layout.Value;
        bool theatre = st.Theatre.Value;
        var set = st.Accent.Value;
        if (_leftAtMount < 0) _leftAtMount = Math.Max(1, st.EndRemainingMs());
        var fill = new FillProps(_leftAtMount, _leftAtMount / (float)st.EndTotalMs);

        var region = Region(L, tier, theatre);
        float pad = theatre ? G.TheatrePlatePad : G.PlatePad;
        float thumbW = G.ThumbW(tier.EndThumbW, region.W, pad, theatre);
        float thumbH = WinUiStageShelfGeometry.ThumbH(thumbW);
        float textW = G.TextW(thumbW, region.W, pad);
        var lane = p.Card.Lane;
        var rows = p.Card.Queue.Rows;
        int at = 0;
        while (at < rows.Length && lane.TextOf(rows[at].Ref).Unavailable) at++;
        Element body = at >= rows.Length ? new BoxEl() : Card(st, lane, rows[at], p.Card.From, tier, thumbW, thumbH, textW, pad, set, fill);
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(region.X, region.Y, 0f, 0f),
                    Width = region.W, Height = region.H, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestPassThrough = true,
                    Children = [body],
                },
            ],
        };
    }

    /// <summary>Where the card is centred: Cinema between the top bar and the lower block, Theatre inside the picture's box above its command bar.</summary>
    static RectF Region(Stage.Layout L, WinUiStageLayout tier, bool theatre)
    {
        if (theatre)
        {
            var box = WinUiStageTheatre.PictureRect(L, tier, true);
            float bar = WinUiStageVideoBarGeometry.BarH(WinUiStageVideoBarGeometry.Inset(tier.BarInset, true));
            return new RectF(box.X + G.TheatrePad, box.Y + G.TheatrePad, MathF.Max(0f, box.W - 2f * G.TheatrePad), MathF.Max(0f, box.H - bar - 2f * G.TheatrePad));
        }
        float top = tier.BarH;
        float bottom = WinUiStagePictureChrome.LowerBlockTop(L, tier, shelf: true);
        float gutter = WinUiStagePictureChrome.Left(tier);
        return new RectF(gutter, top, MathF.Max(0f, L.W - 2f * gutter), MathF.Max(0f, bottom - top));
    }

    static Element Card(Stage.StageCtx st, IQueueLane lane, QueueRowProps row, string from, WinUiStageLayout tier, float thumbW, float thumbH,
        float textW, float pad, AccentSet set, FillProps fill)
    {
        var text = lane.TextOf(row.Ref);
        var video = lane.VideoOf(row.Ref);
        var lines = new List<Element>(4)
        {
            new TextEl(text.Title)
            {
                FontFamily = WinUiStageLayerGeometry.DisplayFace, Size = G.TitleSize, LineHeight = G.TitleLine, Weight = G.TitleWeight, Color = Ink.Ink,
                Wrap = TextWrap.Wrap, MaxLines = G.TitleMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
            new TextEl(text.Artists)
            {
                Size = G.ArtistSize, LineHeight = G.ArtistLine, Color = Ink.InkSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        };
        if (from.Length > 0)
            lines.Add(new TextEl(Strings.Stage.EndCard.From(from))
            {
                Size = G.FromSize, LineHeight = G.FromLine, Color = Ink.InkTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            });
        lines.Add(new BoxEl
        {
            Direction = 0, Gap = G.ButtonGap, Margin = new Edges4(0f, G.ButtonsAbove, 0f, 0f), AlignItems = FlexAlign.Center,
            Children =
            [
                Button.Create(Loc.Get(Strings.Stage.EndCard.PlayNow), st.EndPlayNow, ButtonAppearance.Accent, ControlSize.Medium,
                    palette: Button.ButtonPalette.ForAccent(set.Fill)) with { Key = "stage:end:play", Height = tier.EndButtonH, Shrink = 0f, AllowFocusOnInteraction = false },
                Button.Create(Loc.Get(Strings.Stage.EndCard.Cancel), st.EndCancel, ButtonAppearance.Standard, ControlSize.Medium)
                    with { Key = "stage:end:cancel", Height = tier.EndButtonH, Shrink = 0f, AllowFocusOnInteraction = false },
            ],
        });
        return new BoxEl
        {
            Key = "stage:endcard", Direction = 1, Gap = G.CaptionGap, Shrink = 0f, HitTestPassThrough = true,
            Padding = Edges4.All(pad), Corners = Radii.CardAll, Fill = WinUiStageVideoBar.Plate(), BorderWidth = 1f, BorderColor = Ink.Stroke, Shadow = Elevation.Dialog,
            Children =
            [
                Caption(st),
                new BoxEl
                {
                    Direction = 0, Gap = G.RowGap, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = thumbW, Height = thumbH, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = Radii.CardAll, HitTestVisible = false,
                            Children =
                            [
                                WinUiStageThumb.Of(text, video, thumbW, thumbH, ringW: G.Ring, ring: set.Fill),
                                Progress(thumbW, set.Fill, fill),
                            ],
                        },
                        new BoxEl { Direction = 1, Gap = G.TextGap, Width = textW, MinWidth = 0f, Shrink = 0f, Children = lines.ToArray() },
                    ],
                },
            ],
        };
    }

    /// <summary>"Up next in 5": the localized sentence around its number, the number in the ink and semibold, ticking through a bound text.</summary>
    static Element Caption(Stage.StageCtx st)
    {
        string template = Loc.Get(Strings.Stage.EndCard.UpNextInKey);
        const string slot = "{seconds}";
        int cut = template.IndexOf(slot, StringComparison.Ordinal);
        string head = cut < 0 ? template : template[..cut], tail = cut < 0 ? "" : template[(cut + slot.Length)..];
        var seconds = st.EndSeconds;
        var kids = new List<Element>(3);
        if (head.Length > 0) kids.Add(new TextEl(head) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap });
        kids.Add(new TextEl("")
        {
            Text = Prop.Of(() => seconds.Value.ToString(System.Globalization.CultureInfo.CurrentCulture)),
            Size = G.CaptionSize, LineHeight = G.CaptionLine, Weight = G.NumberWeight, Color = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
        });
        if (tail.Length > 0) kids.Add(new TextEl(tail) { Size = G.CaptionSize, LineHeight = G.CaptionLine, Color = Ink.InkSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap });
        return new BoxEl { Key = "stage:end:caption", Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false, Children = kids.ToArray() };
    }

    /// <summary>The thin progress line at the thumbnail's foot: the track, and the accent fill sliding in from the left over what is left.</summary>
    static Element Progress(float w, ColorF accent, FillProps fill) => new BoxEl
    {
        Key = "stage:end:progress", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Width = w, Height = G.ProgressH, ClipToBounds = true,
        Fill = Ink.Ink with { A = G.ProgressTrackA }, HitTestVisible = false,
        Children = [Embed.Comp(fill with { W = w, Accent = accent }, static () => new ProgressFill())],
    };

    sealed record FillProps(int LeftMs, float LeftShare)
    {
        public float W { get; init; }

        public ColorF Accent { get; init; }
    }

    /// <summary>The accent fill: its root slides from where the countdown stood at the mount to the end, once, linear, on the render thread.</summary>
    sealed class ProgressFill : Component
    {
        public override Element Render()
        {
            var f = UseProps<FillProps>();
            UseTransition(AnimChannel.TranslateX, -f.LeftShare * f.W, 0f, f.LeftMs, Easing.Linear, DepKey.Empty);
            return new BoxEl { Key = "stage:end:fill", Width = f.W, Height = G.ProgressH, Fill = f.Accent, HitTestVisible = false };
        }
    }
}
