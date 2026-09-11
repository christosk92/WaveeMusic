using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>
/// The Now Playing hero's header strip: an eyebrow, the two-item <b>Cover | ‹Player›</b> <see cref="SelectorBar"/>,
/// and the player-style gear that anchors <see cref="PlayerStyleFlyout"/>.
///
/// <para><b>Why its own component.</b> The SelectorBar is a CONTROLLED control — it wants a caller-owned
/// <c>Signal&lt;int&gt;</c> whose owner is stable across re-renders — and the flyout's open state is a hook-shaped
/// effect. Hosting both inside <see cref="NowPlayingHeroTile"/> would put two hooks in a component that also swaps
/// its hero child by <c>Key</c>; here they have one owner that renders the same three children forever.</para>
///
/// <para><b>Popup, not Flyout.Attach.</b> The gear needs a lit "expanded" state, and the artwork's context menu opens
/// the SAME popup by writing <see cref="NpvPlayerPrefs.StyleFlyoutOpen"/> — both need a controlled open signal.
/// The popup stays open across selections (every pick just re-renders its body); Escape / light-dismiss closes it and
/// restores focus to the gear.</para>
/// </summary>
sealed class NpvHeaderRow : Component
{
    /// <summary>The strip's fixed height. <see cref="NowPlayingHeroTile.ArtTop"/> is derived from it, so the docked
    /// Art|Video toggle follows the art down automatically — never re-measure this from the outside.</summary>
    public const float Height = 36f;

    // Cover = a framed picture, Player = a record. Both are stock Segoe Fluent names (glyphs.json).
    static readonly string?[] ModeIcons = [Icons.Picture, Icons.Album];

    // WinUI's SelectorBarItem metrics (12,10,12,7 padding + 14pt) build a 47-DIP bar — the rail header is 36. This
    // compresses the ITEM's content box (never the mechanics: the control re-asserts click/roving/focus on the
    // result) to the mockup's 30-DIP pill: 3 + ~18 + 2 = 23 content, + 3 pill, + the bar's own 4/4 padding = 33.
    // Shape-guarded: if SelectorBar's item tree ever changes, the modifier no-ops instead of mangling it.
    static readonly TemplateParts CompactBar = new()
    {
        [SelectorBar.PartItem] = item =>
        {
            if (item.Children is not [BoxEl content, var pill]) return item;
            var kids = new Element[content.Children.Length];
            for (int i = 0; i < kids.Length; i++)
                kids[i] = content.Children[i] is TextEl t && t.FontFamily is null ? t with { Size = 13f } : content.Children[i];
            return item with
            {
                Children = [content with { Padding = new Edges4(12f, 3f, 12f, 2f), Gap = 7f, Children = kids }, pill],
            };
        },
    };

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;

        // Epoch-subscribed reads: Settings › Appearance, the art menu, the command palette and this row all write the
        // same two ints, so every surface re-reads the STORE on a bump rather than trusting a local mirror.
        int epoch = NpvPlayerPrefs.Epoch.Value;
        int stored = NpvPlayerPrefs.Presentation(settings);
        var preset = NpvPlayerCatalog.ById(NpvPlayerPrefs.Style(settings));

        // The SelectorBar's controlled index. It is a MIRROR of the pref, not the truth: the bar writes it on click
        // (before OnChange), so the pill moves in the same frame as the press; the effect below re-syncs it whenever
        // some OTHER surface moved the pref. Never written during Render.
        var presentation = UseSignal(stored);
        UseEffect(() => presentation.SetIfChanged(stored), epoch);

        var styleOpen = NpvPlayerPrefs.StyleFlyoutOpen;
        bool open = styleOpen.Value;   // subscribe: the gear lights while the flyout is up, wherever it was opened from

        string[] items = [Loc.Get(Strings.Player.PresentationCover), Loc.Get(preset.ShortLabelKey)];

        Element gear = RightRail.HeaderButton(
            Icons.Settings,
            Loc.Get(Strings.Player.PlayerStyle),
            () => styleOpen.Value = !styleOpen.Peek(),
            active: open);

        return new BoxEl
        {
            Direction = 0, Height = Height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Children =
            [
                // WaveeType.Eyebrow, unmodified except colour — 12/16/600 + the ONE eyebrow tracking. Deliberately NOT
                // upper-cased (the alias's own doc forbids a call-site .ToUpper(): it mangles Turkish dotted i and
                // expands German ß on a LOCALIZED string).
                WaveeType.Eyebrow(Loc.Get(Strings.Player.NowPlaying)) with
                {
                    Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                // WITHHELD behind WaveeFeatures.NpvPresentationSwitcher (off): the presentation switcher is hidden
                // while the player-style work is in flight. SPREAD rather than a placeholder child — the row applies
                // Gap between children, so an empty box would leave the gap it used to sit in. Everything above this
                // still runs unconditionally (the pref reads, the mirror signal and its re-sync effect): they are
                // also this row's subscription, so dropping them would stop the gear lighting when another surface
                // moves the pref. The gear itself is deliberately NOT gated — see the flag's doc.
                .. WaveeFeatures.NpvPresentationSwitcher
                    ? (Element[])[SelectorBar.Create(items, presentation,
                        onChange: i => NpvPlayerPrefs.SetPresentation(settings, i, NpvDiagnostics.SourceHeader),
                        parts: CompactBar, icons: ModeIcons)]
                    : [],
                // The popup's own anchor wrapper is AlignSelf=Start (it has to be: the overlay reads that node's rect
                // to place the flyout), which would hang the 32-DIP gear off the TOP of a 36-DIP row. This plain
                // wrapper takes the row's Center for it, and the anchor then starts at the wrapper's own origin.
                new BoxEl
                {
                    Direction = 0, Shrink = 0f,
                    Children =
                    [
                        Popup.Create(gear, static () => Embed.Comp(() => new PlayerStyleFlyout()), styleOpen,
                            onOpenChanged: nowOpen => { if (!nowOpen) NpvDiagnostics.FlyoutClosed(); },
                            placement: FlyoutPlacement.BottomEdgeAlignedRight,
                            options: new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)),
                    ],
                },
            ],
        };
    }
}
