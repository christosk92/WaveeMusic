// ── Themes/WinUi/Structure/Views/Detail/WinUiDetailKit.cs — the pieces WinUI's detail views share (themes plan 6.2; P6 W6a) ─────────────────────────────
//
// The hero's action row (boards W-Album, -Compact, -Huge): [Play split] 8 [Shuffle] 8 [Save] 8 [Share] 8 [...] on one wrap, the labelled words from Small up and
// glyphs alone at Compact (the 40 targets); the Play SplitButton (the accent fill, a 1-px divider at 20 %, the "More ways to play" menu); the billed-artist
// attribution (avatars with a ring, the names as links in the accent, the meta); the keyed rows' FLIP; the More button; the save and share verbs.
// Derived from Classic's `ClassicDetailKit` / `ClassicPlaySplit` (a theme may not reference another theme, WVTH001), retuned to the boards: the system
// accent (WinUI paints no artwork accent), 32 controls with an r4 corner, words beside the glyphs.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using RailArtistLine = Wavee.Detail.RailArtistLine;
using RailArtistMode = Wavee.Detail.RailArtistMode;
using Skeleton = Wavee.Detail.Skeleton;

namespace Wavee.Themes.WinUi;

/// <summary>The shared pictures of WinUI's detail views.</summary>
internal static partial class WinUiDetailKit
{
    // ══ 1. THE KEYED ROWS ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A late row's entrance: it fades up when it arrives after the frame.</summary>
    public static readonly EnterExit FadeUp = new(Opacity: 0f, Active: true);

    /// <summary>The position-only FLIP a keyed row takes when a row above it arrives or leaves.</summary>
    public static readonly LayoutTransition Shove = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    /// <summary>A keyed row that exists in both models: it only ever gets pushed.</summary>
    public static BoxEl Row(string key, Element child) => new() { Key = key, Direction = 1, Layout = Shove, Children = [child] };

    /// <summary>A keyed row that can arrive LATE: fades up on insert, FLIPs on later shoves.</summary>
    public static BoxEl LateRow(string key, Element child) => new() { Key = key, Direction = 1, Layout = Shove, Enter = FadeUp, Children = [child] };

    // ══ 2. THE PLAY SPLIT ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The detail header's Play split for <paramref name="subject"/>: <paramref name="play"/> on the left half (Play, or Pause while this subject plays),
    /// the "More ways to play" menu on the chevron half; <paramref name="height"/> is the tier's target (32, 40 at Compact). The accent is the system's. Keyed on
    /// the subject and the height: the props freeze at mount.</summary>
    public static Element PlaySplit(EntityUri subject, Action play, float height)
        => Embed.Comp(() => new PlaySplitHost { Subject = subject, Play = play, Height = height })
            with { Key = "ctxplay:" + subject.Text + ":" + height.ToString(CultureInfo.InvariantCulture) };

    sealed class PlaySplitHost : Component
    {
        public required EntityUri Subject { get; init; }
        public required Action Play { get; init; }
        public required float Height { get; init; }

        readonly Signal<bool> _open = new(false);
        IOverlayService? _overlay;
        NodeHandle _anchor;
        OverlayHandle? _menu;
        Action? _toggle;
        Action<NodeHandle>? _capture;
        Action<KeyEventArgs>? _keys;

        public override Element Render()
        {
            _overlay = UseContext(Overlay.Service);
            _toggle ??= ToggleMenu;
            _capture ??= h => _anchor = h;
            _keys ??= OnKey;

            ColorF fill = Tok.AccentDefault;
            ColorF ink = ColorContrast.PickContrast(fill);
            bool pause = PlayRelation.ShowsPause(Subject.Text);
            bool open = _open.Value;
            var palette = Controls.AccentFillPalette(fill);
            float r = Radii.Control, h = Height;
            bool wide = h > Controls.ButtonHeight;
            float pad = wide ? WinUiDetailRules.PlayPadCompact : WinUiDetailRules.PlayPad;

            BoxEl playHalf = Controls.PrimaryButton(Loc.Get(pause ? Strings.Home.Pause : Strings.Detail.Play), Play, fill,
                pause ? WinUiDetailRules.PauseSolid : WinUiDetailRules.PlaySolid) with
            {
                Key = "split:play", MinWidth = 0f, Height = h, Shrink = 0f, Padding = new Edges4(pad, 0f, pad, 0f), Gap = WinUiDetailRules.PlayGap,
                Corners = new CornerRadius4(r, 0f, 0f, r),
                BorderWidth = 0f, BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null,
            };

            BoxEl menuHalf = Button.Create("", _toggle, ButtonAppearance.Accent,
                style: Button.DefaultStyle(ButtonAppearance.Accent, palette: palette) with { MinHeight = h }) with
            {
                Key = "split:menu", Width = h, Height = h, MinWidth = 0f, Shrink = 0f, Padding = default, Gap = 0f,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Corners = new CornerRadius4(0f, r, r, 0f),
                BorderWidth = 0f, BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null,
                Fill = open ? palette.Background.Pressed : fill,
                Children =
                [
                    Icon(Icons.ChevronDown, WinUiDetailRules.SplitChevronGlyph, ink) with { PressedColor = ink with { A = Controls.OnFillSecondaryAlpha(ink) } },
                ],
            };

            // The divider sits ON the accent: a full-height strip of the fill with the hairline centred in it.
            Element divider = new BoxEl
            {
                Width = 1f, Height = h, Shrink = 0f, Fill = fill, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new BoxEl { Width = 1f, Height = h, Fill = ink with { A = WinUiDetailRules.SplitDividerInk } }],
            };

            return new BoxEl
            {
                ZStack = true, Shrink = 0f, Height = h, AlignSelf = FlexAlign.Start,
                OnRealized = _capture, OnKeyDown = _keys,
                Children =
                [
                    new BoxEl
                    {
                        Key = "split:halves", Direction = 0, AlignItems = FlexAlign.Center, Height = h,
                        Corners = Radii.ControlAll, ClipToBounds = true,
                        Children = [playHalf, divider, ToolTip.Wrap(menuHalf, Loc.Get(Strings.Detail.MoreWaysToPlay))],
                    },
                    new BoxEl
                    {
                        Key = "split:border", HitTestVisible = false, Corners = Radii.ControlAll,
                        BorderWidth = 1f, BorderBrush = Tok.AccentControlElevationBorder,
                    },
                ],
            };
        }

        void OnKey(KeyEventArgs e)
        {
            if (e.KeyCode is not (Keys.Down or Keys.F4)) return;
            ToggleMenu();
            e.Handled = true;
        }

        void ToggleMenu()
        {
            if (_menu is { IsOpen: true } shown) { shown.Close(); return; }
            if (Controls.IsNullOverlay(_overlay)) return;
            var items = DetailVerbs.PlayMenu(Subject);
            OverlayHandle? opened = null;
            opened = _overlay.Open(() => _anchor, () => MenuFlyout.Create(items, () => opened?.Close(), size: OverlayFit.MenuSize), FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
            _menu = opened;
            opened.ClosedAction = () =>
            {
                if (!ReferenceEquals(_menu, opened)) return;
                _menu = null;
                _open.Value = false;
            };
            _open.Value = true;
        }
    }

    // ══ 3. THE ACTION ROW ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The fixed actions after the split: Shuffle (when the page offers it), Save (when the kind has a heart), Share (when the page has a link) and the
    /// "..." (when the page has a menu), each keyed under <paramref name="keyPrefix"/>. <paramref name="words"/>: Shuffle and Save carry their word (Small and up,
    /// W-Album); false: glyphs alone (W-Album-Compact). <paramref name="target"/> is the tier's 32 / 40.</summary>
    /// <para>A playlist's hero adds the board's pair (W-Playlist: Play · Shuffle · Invite · Tune · Share · ...): <paramref name="invite"/> (the people glyph and
    /// "Invite"; the glyph alone at Compact) and <paramref name="tune"/> (the word alone); null leaves either out. <paramref name="shuffleGlyph"/> false drops the
    /// worded Shuffle's glyph (W-Playlist, W-PlaylistFollowed, W-LikedSongs at Standard and Small; the Huge boards and the glyph-only Compact keep it).</para></summary>
    public static List<Element> FixedActions(string keyPrefix, Detail.Identity id, bool heart, Detail.FrameActions acts, float target, bool words,
        bool shuffleGlyph = true, Action? invite = null, Action? tune = null)
    {
        var actions = new List<Element>(6);
        if (acts.Shuffle is { } shuffle)
            actions.Add(Controls.Named(BorderedAction(words && !shuffleGlyph ? null : Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle, target, words)
                with { Key = keyPrefix + "shuffle:button" }, Loc.Get(Strings.Detail.Shuffle)) with { Key = keyPrefix + "shuffle" });
        if (heart)
        {
            string saveUri = SaveTargetOf(id).Text;
            string saveName = id.Title;
            actions.Add(Embed.Comp(() => new SaveAction { Uri = saveUri, Name = saveName, Target = target, Word = words })
                with { Key = keyPrefix + "save:" + saveUri + ":" + (words ? "w" : "g") });
        }
        if (invite is not null)
            actions.Add(Controls.Named(BorderedAction(Icons.Friends, Loc.Get(Strings.Detail.Edit.Invite), invite, target, words) with { Key = keyPrefix + "invite:button" },
                Loc.Get(Strings.Detail.Edit.InviteCollaborators)) with { Key = keyPrefix + "invite" });
        if (tune is not null)
            actions.Add(Controls.Named(BorderedAction(words ? null : Icons.Equalizer, Loc.Get(Strings.Detail.Tuning.Tune), tune, target, words)
                with { Key = keyPrefix + "tune:button" }, Loc.Get(Strings.Detail.Tuning.Tune)) with { Key = keyPrefix + "tune" });
        if (ShareActionFor(id) is { } share)
            actions.Add(Controls.Named(Controls.IconAction(Icons.Share, share, size: target) with { Key = keyPrefix + "share:button" }, Loc.Get(Strings.Menu.Share))
                with { Key = keyPrefix + "share" });
        if (acts.More is { } more)
            actions.Add(MoreButton(more, target) with { Key = keyPrefix + "more:" + id.Subject.Text });
        return actions;
    }

    /// <summary>How many buttons <see cref="FixedActions"/> lays out for the same inputs (the skeleton reserves exactly these).</summary>
    public static int FixedActionCount(Detail.Identity id, bool heart, Detail.FrameActions acts, bool invite = false, bool tune = false)
        => (acts.Shuffle is not null ? 1 : 0) + (heart ? 1 : 0) + (invite ? 1 : 0) + (tune ? 1 : 0) + (ShareActionFor(id) is not null ? 1 : 0)
         + (acts.More is not null ? 1 : 0);

    /// <summary>The primary and the actions in ONE wrap, 8 apart; <paramref name="center"/> centres the row (the stacked hero).</summary>
    public static BoxEl ActionRow(string key, Element primary, IReadOnlyList<Element> actions, float topMargin, bool center)
    {
        var kids = new Element[1 + actions.Count];
        kids[0] = primary;
        for (int i = 0; i < actions.Count; i++) kids[i + 1] = actions[i];
        return new BoxEl
        {
            Key = key, Layout = Shove,
            Direction = 0, Wrap = true, Gap = WinUiDetailRules.ActionGap, AlignItems = FlexAlign.Center,
            Justify = center ? FlexJustify.Center : FlexJustify.Start,
            Margin = new Edges4(0f, topMargin, 0f, 0f),
            Children = kids,
        };
    }

    /// <summary>A bordered standard action (W-Album: the control fill, the elevation stroke, r4, 32 / 40 tall): the glyph and its word, or the glyph alone.</summary>
    static BoxEl BorderedAction(string? glyph, string label, Action onClick, float target, bool word)
    {
        if (!word)
            return Controls.IconAction(glyph ?? Icons.More, onClick, size: target) with
            {
                Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
            };
        return Button.Create(label, onClick, ButtonAppearance.Standard, glyph: glyph) with { Height = target, MinWidth = 0f, Shrink = 0f };
    }

    /// <summary>The save toggle: the heart and "Save" (W-Album), "Saved" with the filled heart once it is; the glyph alone at Compact. A stock ToggleButton tinted
    /// by the system accent while it is on.</summary>
    sealed class SaveAction : Component
    {
        public required string Uri { get; init; }
        public required string Name { get; init; }
        public required float Target { get; init; }
        public required bool Word { get; init; }

        public override Element Render()
        {
            var lib = Controls.Library;
            if (lib is null) return new BoxEl();
            bool on = lib.IsSaved(Uri);
            string label = Loc.Get(on ? Strings.Menu.Saved : Strings.Menu.Save);
            if (!Word)
            {
                BoxEl box = Controls.IconAction(on ? Icons.HeartFill : Icons.Heart, () => lib.ToggleSaved(Uri, Name), size: Target) with
                {
                    Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                    BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                };
                return Controls.Named(box, label);
            }
            return ToggleButton.Controlled(Loc.Get(Strings.Menu.Save), on, _ => lib.ToggleSaved(Uri, Name),
                glyph: Icons.Heart, checkedGlyph: Icons.HeartFill, checkedLabel: Loc.Get(Strings.Menu.Saved),
                style: NeutralToggle(ControlSize.Medium), parts: s_noShrink);
        }
    }

    static readonly TemplateParts s_noShrink = new() { [ToggleButton.PartRoot] = static e => e with { Shrink = 0f, MinWidth = 0f } };

    /// <summary>WinUI's toggled STANDARD button (W-Album "Save", W-Artist "Follow", W-Album-Huge4K "Following"): the control fill, the elevation stroke and
    /// the primary text in both states; the checked state says itself through its word ("Saved", "Following") and its glyph (the filled heart), never an
    /// accent tint. The shared style's label swap and reflow are kept, so the wider word eases in.</summary>
    internal static ToggleButton.Style NeutralToggle(ControlSize size)
    {
        var s = ButtonRules.FollowStyle(Tok.AccentDefault, R.IsLight);
        s = s with
        {
            OnBackground = s.OffBackground, OnHover = s.OffHover, OnPressed = s.OffPressed, OnDisabledBackground = s.OffDisabledBackground,
            OnForeground = s.OffForeground, OnPressedForeground = s.OffPressedForeground, OnDisabledForeground = s.OffDisabledForeground,
            OnBorder = s.OffBorder, OnHoverBorder = s.OffHoverBorder, OnPressedBorder = s.OffPressedBorder, OnDisabledBorder = s.OffDisabledBorder,
            OnGlyphForeground = null, OffGlyphForeground = null, CheckedBackgroundSizing = s.BackgroundSizing,
        };
        if (size == ControlSize.Medium) return s;
        var m = ControlMetrics.For(size);
        return s with { MinHeight = m.MinHeight, Padding = m.Padding };
    }

    /// <summary>WinUI's Follow: "Follow" / "Following" on the neutral toggle (<see cref="NeutralToggle"/>; W-Artist draws no glyph). Keyed by the caller.
    /// <paramref name="plated"/>: the artist hero's 70 % dark plate and hairline over the photograph (<see cref="WinUiArtistRules.PlateAlpha"/>).</summary>
    public static Element Follow(string uri, string name, ControlSize size = ControlSize.Medium, bool plated = false)
        => uri.Length == 0
            ? ToggleButton.Controlled(Loc.Get(Strings.Artist.Follow), false, static _ => { }, style: FollowStyle(size, plated), parts: s_noShrink)
            : Embed.Comp(() => new FollowAction { Uri = uri, Name = name, Size = size, Plated = plated });

    static ToggleButton.Style FollowStyle(ControlSize size, bool plated)
    {
        var s = NeutralToggle(size);
        if (!plated) return s;
        var plate = R.Layer.FileArea with { A = WinUiArtistRules.PlateAlpha };
        var stroke = GradientSpec.Solid(WinUiArtistRules.PlateStroke);
        return s with { OffBackground = plate, OnBackground = plate, OffBorder = stroke, OnBorder = stroke, OffHoverBorder = stroke, OnHoverBorder = stroke };
    }

    sealed class FollowAction : Component
    {
        public required string Uri { get; init; }
        public required string Name { get; init; }
        public required ControlSize Size { get; init; }
        public bool Plated { get; init; }

        public override Element Render()
        {
            var lib = Controls.Library;
            if (lib is null) return new BoxEl();
            bool on = lib.IsSaved(Uri);
            return ToggleButton.Controlled(Loc.Get(Strings.Artist.Follow), on, _ => lib.ToggleSaved(Uri, Name),
                checkedLabel: Loc.Get(Strings.Artist.Following), style: FollowStyle(Size, Plated), parts: s_noShrink);
        }
    }

    // ══ 4. THE MORE BUTTON, THE SAVE TARGET AND THE SHARE ACTION ════════════════════════════════════════════════════════════

    /// <summary>The More button: its flyout is built lazily AT OPEN from the newest menu factory (ONE menu host per surface).
    /// <paramref name="look"/>: bare (the album hero, W-Album), the bordered standard button (the artist actions row, W-Artist-Compact's siblings) or the
    /// artist hero's dark plate over the photograph (W-Artist).</summary>
    public static Element MoreButton(Func<ContextMenuModel?> menu, float size, MoreLook look = MoreLook.Bare)
        => Embed.Comp(new MoreProps(size, menu, look), static () => new MoreHost());

    /// <summary>How the More button is dressed.</summary>
    internal enum MoreLook : byte { Bare, Bordered, Plated }

    sealed record MoreProps(float Size, Func<ContextMenuModel?> Menu, MoreLook Look)
    {
        public bool Equals(MoreProps? o) => o is not null && Size == o.Size && Look == o.Look;
        public override int GetHashCode() => HashCode.Combine(Size, Look);
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    sealed class MoreHost : Component, IPropsHost
    {
        MoreProps? _latest;
        readonly Signal<MoreProps?> _props = new(null);
        readonly Func<ContextMenuModel?> _menu;

        public MoreHost() => _menu = () => _latest?.Menu();

        public void ApplyProps(object props)
        {
            _latest = (MoreProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            var overlay = UseContext(Overlay.Service);
            BoxEl button = Controls.IconAction(Icons.More, null, requestsContext: true, size: p.Size);
            if (p.Look != MoreLook.Bare)
                button = button with
                {
                    Fill = p.Look == MoreLook.Plated ? Prop.Of(static () => R.Layer.FileArea with { A = WinUiArtistRules.PlateAlpha }) : Tok.FillControlDefault,
                    HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary, BorderWidth = 1f,
                    BorderBrush = p.Look == MoreLook.Plated ? GradientSpec.Solid(WinUiArtistRules.PlateStroke) : Tok.ControlElevationBorder,
                };
            if (!Controls.IsNullOverlay(overlay)) button = ContextSheets.Attach(button, overlay, _menu, s_moreMenu);
            return ToolTip.Wrap(button, Loc.Get(Strings.Common.More));
        }
    }

    /// <summary>The uri the Save heart saves: the identity's own save target, else its subject.</summary>
    public static EntityUri SaveTargetOf(Detail.Identity id) => id.SaveTarget.IsValid ? id.SaveTarget : id.Subject;

    /// <summary>Copy the page's web link with the confirmation toast, only when a clipboard seam exists.</summary>
    public static Action? ShareActionFor(Detail.Identity id)
    {
        if (id.ShareUrl is not { Length: > 0 } url) return null;
        return () =>
        {
            if (Actions.Services.Clipboard is not { } copy) return;
            copy(url);
            _ = Notify.Say(Loc.Get(Strings.Menu.LinkCopied), InfoBarSeverity.Success);
        };
    }

    // ══ 5. THE ATTRIBUTION ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The billed artists as links in the accent, each its own click, separated by commas.</summary>
    static TextSpan[] ArtistSpans(IReadOnlyList<Controls.Face> artists)
    {
        var spans = new TextSpan[artists.Count * 2 - 1];
        int at = 0;
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) spans[at++] = new TextSpan(", ");
            var face = artists[i];
            spans[at++] = new TextSpan(face.Name, Weight: 400, Color: Tok.AccentTextPrimary, OnClick: face.OnClick);
        }
        return spans;
    }

    /// <summary>The attribution row of a horizontal hero (W-Album): the avatars (ringed, overlapping) then one line, "Artist, Artist · 15 songs · 57 min", the names
    /// links in the accent. <paramref name="avatar"/> 0 draws no portraits (the short hero).</summary>
    public static Element AttributionLine(IReadOnlyList<Controls.Face>? artists, string? meta, float avatar, float measure)
    {
        var kids = new List<Element>(2);
        if (avatar > 0f && artists is { Count: > 0 }) kids.Add(AvatarPile(artists, avatar));
        var spans = new List<TextSpan>(artists is { Count: > 0 } ? artists.Count * 2 + 1 : 1);
        if (artists is { Count: > 0 }) spans.AddRange(ArtistSpans(artists));
        if (meta is { Length: > 0 })
            spans.Add(new TextSpan((spans.Count > 0 ? WinUiDetailRules.MetaLead : "") + meta, Weight: 400, Color: Tok.TextSecondary));
        if (spans.Count > 0)
            kids.Add(new SpanTextEl(spans.ToArray())
            {
                Size = WinUiDetailRules.AttributionSize, LineHeight = WinUiDetailRules.AttributionLine, Color = Tok.TextSecondary,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiDetailRules.AvatarGap, MinWidth = 0f, MaxWidth = measure, Children = kids.ToArray(),
        };
    }

    /// <summary>The stacked hero's attribution: the names alone, centred (the meta is its own line).</summary>
    public static Element NamesLine(IReadOnlyList<Controls.Face> artists, float measure)
        => new SpanTextEl(ArtistSpans(artists))
        {
            Size = WinUiDetailRules.AttributionSize, LineHeight = WinUiDetailRules.AttributionLine, Color = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = measure, MinWidth = 0f,
            AlignSelf = FlexAlign.Center,
        };

    static Element AvatarPile(IReadOnlyList<Controls.Face> artists, float avatar)
    {
        int shown = Math.Min(artists.Count, WinUiDetailRules.AvatarMax);
        var kids = new Element[shown];
        float outer = avatar + 2f * WinUiDetailRules.AvatarRing;
        for (int i = 0; i < shown; i++)
        {
            var face = artists[i];
            kids[i] = new BoxEl
            {
                Key = "avatar:" + i.ToString(CultureInfo.InvariantCulture),
                Width = outer, Height = outer, Shrink = 0f, Corners = CornerRadius4.All(outer / 2f),
                Fill = Prop.Of(static () => R.Layer.FileArea), Padding = Edges4.All(WinUiDetailRules.AvatarRing),
                Margin = new Edges4(i == 0 ? 0f : -WinUiDetailRules.AvatarOverlap, 0f, 0f, 0f),
                Children = [PersonPicture.Create("", avatar, displayName: face.Name, imageSourcePath: face.ImageUrl)],
            };
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids };
    }

    /// <summary>The eyebrow row of a list that can carry the "Saved" chip (W-Playlist: "Playlist · public  [✓ Saved]"): the words, then the chip 8 after them while
    /// <paramref name="saveUri"/> is in the library. The row holds the chip's height whether or not it shows, so saving or removing the list moves nothing.</summary>
    public static Element EyebrowWithChip(string text, WinUiHeroForm form, string saveUri, bool center) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiDetailRules.ChipAfterEyebrow, Height = form.ChipHeight, MinWidth = 0f,
        AlignSelf = center ? FlexAlign.Center : FlexAlign.Auto,
        Children = [EyebrowRun(text, form) with { Shrink = 1f }, Embed.Comp(() => new SavedChip { Uri = saveUri, Height = form.ChipHeight }) with { Key = "hero-chip:" + saveUri }],
    };

    /// <summary>The "Saved" chip: a 10 check and the word (12) on the control fill, 0 6 padding, r4; nothing while the list is not saved.</summary>
    sealed class SavedChip : Component
    {
        public required string Uri { get; init; }
        public required float Height { get; init; }

        public override Element Render()
        {
            var lib = Controls.Library;
            if (lib is null || !lib.IsSaved(Uri)) return new BoxEl();
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiDetailRules.ChipGap, Height = Height, Shrink = 0f,
                Padding = new Edges4(WinUiDetailRules.ChipPad, 0f, WinUiDetailRules.ChipPad, 0f), Corners = CornerRadius4.All(WinUiDetailRules.ChipRadius),
                Fill = Tok.FillControlDefault,
                Children =
                [
                    Icon(Icons.Check, WinUiDetailRules.ChipCheck) with { Color = Tok.TextPrimary },
                    new TextEl(Loc.Get(Strings.Menu.Saved)) { Size = WinUiDetailRules.MetaSize, LineHeight = WinUiDetailRules.MetaLine, Color = Tok.TextPrimary, MaxLines = 1 },
                ],
            };
        }
    }

    /// <summary>The description line (W-Playlist, W-PlaylistFollowed): Body 14/20 in Text 2, one line, ellipsized.</summary>
    public static TextEl DescriptionRun(string text, bool center) => new(text)
    {
        Size = WinUiDetailRules.DescriptionSize, LineHeight = WinUiDetailRules.DescriptionLine, Color = Tok.TextSecondary,
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, AlignSelf = center ? FlexAlign.Center : FlexAlign.Auto,
    };

    /// <summary>The eyebrow: Caption in Text 2 (the size and line are the tier's), one line.</summary>
    public static TextEl EyebrowRun(string text, WinUiHeroForm form) => new(text)
    {
        Size = form.EyebrowSize, LineHeight = form.EyebrowLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // ══ 6. THE BILLED-ARTIST LINE BOX (the album's AlbumArtistLine key) ═════════════════════════════════════════════════════

    const string LocRailMoreArtists = "detail.row.moreArtists";

    /// <summary>The line box's height ease: real layout, so the rows under it reflow with it.</summary>
    static readonly LayoutTransition ArtistLineReflow = new(TransitionChannels.Size, MotionTok.ContentResize.ToDynamics(),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);
    static readonly EnterExit ArtistFaceFade = new(Opacity: 0f, Active: true);

    /// <summary>The billed-artist line in one mode: the ONE look both the default line and the album's own (<c>Album.FacePileHost</c>) paint. The NAME is
    /// what survives: <see cref="RailArtistLine"/> drops the faces first (Wide: pile + names, Medium: the lead face + names on two lines + "+N", Narrow:
    /// names only), and the chevron follows the text in every mode. Nothing under the pointer moves on hover (the plate is a fill); a mode change never
    /// snaps: the line box eases its height, the face slots are keyed per mode and cross-fade in place, and the names glide to their new position
    /// (<see cref="Shove"/>).</summary>
    public static Element ArtistLineBox(RailArtistMode mode, ArtistLineSpec s)
    {
        var kids = new List<Element>(4);
        if (s.Faces.Count > 0)
        {
            if (mode == RailArtistMode.Wide)
                kids.Add(FaceSlot("faces:pile", Controls.FacePile(s.Faces, RailArtistLine.WideFaces, s.Overflow)));
            else if (mode == RailArtistMode.Medium)
            {
                var lead = s.Faces[0];
                kids.Add(FaceSlot("faces:lead", PersonPicture.Create("", Skeleton.OwnerAvatar, displayName: lead.Name, imageSourcePath: lead.ImageUrl)));
            }
        }

        bool wide = mode == RailArtistMode.Wide;
        kids.Add(new BoxEl
        {
            Key = "names", Direction = 0, Shrink = 1f, MinWidth = 0f, Layout = Shove,
            Children =
            [
                new TextEl(s.Names)
                {
                    Size = WinUiDetailRules.ArtistNamesSize, LineHeight = RailArtistLine.TextLine, Weight = 600, Color = Tok.AccentTextPrimary,
                    MinWidth = 0f, Shrink = 1f,
                    MaxLines = wide ? 1 : 2, Wrap = wide ? TextWrap.NoWrap : TextWrap.WrapWholeWords,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        });
        int plus = mode == RailArtistMode.Medium ? RailArtistLine.PlusN(s.Count, 1) : 0;
        if (plus > 0)
            kids.Add(new TextEl(Loc.Format(LocRailMoreArtists, ("count", plus)))
            {
                Key = "plus", Size = WinUiDetailRules.ArtistPlusSize, LineHeight = WinUiDetailRules.ArtistPlusLine, Weight = 600,
                Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
            });
        if (s.Menu) kids.Add(new BoxEl
        {
            Key = "chevron", Shrink = 0f, AlignItems = FlexAlign.Center, Layout = Shove,
            Children = [Icon(Icons.ChevronDownSmall, 8f, Tok.TextTertiary)],
        });

        bool live = s.Click is not null;
        Element trigger = new BoxEl
        {
            Key = "trigger",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 1f, MinWidth = 0f, MaxWidth = s.Width,
            Padding = new Edges4(WinUiDetailRules.ArtistTriggerPadX, RailArtistLine.PadY, WinUiDetailRules.ArtistTriggerPadX, RailArtistLine.PadY),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = ColorF.Transparent,
            HoverFill = live ? Tok.FillCardDefault : ColorF.Transparent, PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
            Role = !live ? AutomationRole.None : s.Menu ? AutomationRole.Button : AutomationRole.Hyperlink,
            Focusable = live, Cursor = live ? CursorId.Hand : (CursorId?)null,
            FocusVisualMargin = Design.FocusInsetRow,
            OnClick = s.Click, OnKeyDown = s.OnKey, OnRealized = s.OnRealized,
            Children = kids.ToArray(),
        };
        // The tooltip wrapper shrink-wraps to the trigger, whose MaxWidth is the line's measure: it can never outgrow the row.
        if (s.Menu) trigger = ToolTip.Wrap(trigger, s.Tip);

        return new BoxEl
        {
            Key = "artist-line", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MaxWidth = s.Width,
            MinHeight = RailArtistLine.HeightOf(mode), ClipToBounds = true, Animate = ArtistLineReflow,
            Children = [trigger],
        };
    }

    /// <summary>One mode's face slot: keyed per look, so a width step cross-fades the faces in place.</summary>
    static Element FaceSlot(string key, Element faces) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [faces],
        Enter = ArtistFaceFade, Exit = ArtistFaceFade, Transition = MotionTok.ControlFaster,   // Design.Motion.Faster (83 ms)
    };
}
