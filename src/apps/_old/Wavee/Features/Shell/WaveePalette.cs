using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Command palette (Ctrl+K). Port of the gallery CommandPalette: Popup.Create(FocusTrap:true, Chrome:Popup) over a
// TextBox + ranked result list. Named WaveeCommandPalette because Design/WaveePalette.cs already owns the type name
// WaveePalette (cover-colour mapper). OverlayHost's FocusTrap focuses FirstFocusableIn(wrapper) — the chromeless
// EditableText, never a PartRoot chrome node (see .claude/skills/wavee/focus-pitfalls.md).

/// <summary>Zero-height top-centre anchor + light-dismiss popup. Mount inside OverlayHost (a ZStack lane over the shell).</summary>
sealed class WaveeCommandPalette : Component
{
    public required Signal<bool> IsOpen;
    public required Action<string, string?> Go;
    public required ActionServices Actions;
    public required IAppSettings Settings;
    public required Action ToggleTheme;

    // Gallery command-palette panel (not a page breakpoint). Matches the WindowsApp CommandPalette exemplar.
    const float PanelW = 560f;

    public override Element Render()
    {
        var isOpen = IsOpen;
        var host = new WaveeCommands.Host
        {
            Go = Go,
            Actions = Actions,
            Settings = Settings,
            ToggleTheme = ToggleTheme,
        };

        var anchor = new BoxEl { Width = PanelW, Height = 0f };

        return Popup.Create(
            anchor,
            content: () => Embed.Comp(() => new WaveePaletteContent
            {
                Host = host,
                Close = () => isOpen.Value = false,
            }),
            isOpen: isOpen,
            placement: FlyoutPlacement.BottomEdgeAlignedLeft,
            options: new PopupOptions(FocusTrap: true, Chrome: PopupChrome.Popup));
    }

    /// <summary>Mount the palette centred near the top of the shell overlay. Hit-test transparent so the lane never
    /// steals page input; the popup surface is hosted by OverlayHost.</summary>
    public static Element Overlay(Signal<bool> isOpen, Action<string, string?> go, ActionServices actions,
        IAppSettings settings, Action toggleTheme)
        => new BoxEl
        {
            Grow = 1, Direction = 1, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Padding = new Edges4(0, Spacing.XXXL * 2, 0, 0),
            Children =
            [
                Embed.Comp(() => new WaveeCommandPalette
                {
                    IsOpen = isOpen, Go = go, Actions = actions, Settings = settings, ToggleTheme = toggleTheme,
                }),
            ],
        };
}

/// <summary>Palette body. Mounted fresh on each open (Popup rebuilds content), so UseSignal query/selection reset for free.</summary>
sealed class WaveePaletteContent : Component
{
    public required WaveeCommands.Host Host;
    public required Action Close;

    const float PanelW = 560f;
    const float PanelMaxH = 460f;

    public override Element Render()
    {
        var query = UseSignal("");
        var sel = UseSignal(0);
        var index = UseRef<WaveeCommands.Entry[]?>(null);
        var dest = UseRef<WaveeCommands.Entry[]?>(null);
        var catalog = UseRef<WaveeCommands.Entry?>(null);
        var opened = UseRef(false);

        var registry = UseContext(WaveeExtensionRegistry.Slot) ?? Host.Actions.Extensions;
        index.Value ??= WaveeCommands.BuildIndex(registry);
        dest.Value ??= new WaveeCommands.Entry[WaveeCommands.MaxResults];
        catalog.Value ??= new WaveeCommands.Entry
        {
            Id = "search.query", Label = "", LabelLower = "", Glyph = Icons.Search,
            Kind = WaveeCommands.Kind.CatalogSearch,
        };

        var hits = dest.Value;
        int count = WaveeCommands.Filter(index.Value, query.Value, hits, catalog.Value);
        int selClamped = count == 0 ? 0 : Math.Clamp(sel.Value, 0, count - 1);

        UseEffect(() =>
        {
            _ = query.Value;
            if (!Announcer.IsAvailable) return;
            if (!opened.Value)
            {
                opened.Value = true;
                Announcer.Say("Command palette");
            }
            Announcer.SayThrottled(count == 0 ? "No matching commands" : count.ToString() + " matching commands");
        });

        var host = Host;
        var close = Close;

        void Commit(int i)
        {
            if ((uint)i >= (uint)count) return;
            WaveeCommands.Invoke(hits[i], in host);
            close();
        }

        void OnKey(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    if (count > 0) sel.Value = (selClamped + 1) % count;
                    e.Handled = true; break;
                case Keys.Up:
                    if (count > 0) sel.Value = (selClamped - 1 + count) % count;
                    e.Handled = true; break;
                case Keys.Enter:
                    Commit(selClamped); e.Handled = true; break;
                case Keys.Escape:
                    close(); e.Handled = true; break;
            }
        }

        var rows = new Element[count == 0 ? 1 : count];
        if (count == 0)
        {
            rows[0] = new BoxEl
            {
                Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                Children = [new TextEl("No matching commands") { Size = 13f, Color = Tok.TextTertiary }],
            };
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                bool active = idx == selClamped;
                var entry = hits[idx];
                rows[i] = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    MinHeight = WaveeSize.ControlH, MinWidth = 0f,
                    Padding = new Edges4(Spacing.L, Spacing.XS, Spacing.L, Spacing.XS),
                    Corners = Radii.ControlAll,
                    Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
                    HoverFill = Tok.FillSubtleSecondary,
                    OnClick = () => Commit(idx),
                    Children =
                    [
                        new TextEl(entry.Glyph) { Size = 14f, FontFamily = Theme.IconFont, Color = active ? Tok.AccentDefault : Tok.TextTertiary },
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f,
                            Children = [new TextEl(entry.Label) { Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                        },
                        active
                            ? new TextEl("Enter") { Size = 11f, Color = Tok.TextTertiary }
                            : new BoxEl(),
                    ],
                };
            }
        }

        return new BoxEl
        {
            Direction = 1, Width = PanelW, MaxHeight = PanelMaxH, Gap = Spacing.XXS,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
            OnKeyDown = OnKey,
            Children =
            [
                TextBox.Create(query, options: new TextBox.TextBoxOptions
                {
                    Placeholder = "Search commands — type > for commands only",
                    Width = PanelW - Spacing.S * 2,
                }),
                new BoxEl { Height = Spacing.XXS },
                new BoxEl { Direction = 1, Gap = 1f, Children = rows },
            ],
        };
    }
}
