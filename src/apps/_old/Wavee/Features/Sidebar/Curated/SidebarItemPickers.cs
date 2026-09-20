using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// THE PICKERS (§C4.6 + REVISION 2: "Item picker searches routes, library entities, tracks, actions. Action-shortcut items
// use the registry's picker with target-mode binding UI").
//
// Both are MODAL dialogs (ContentDialog.Show) rather than anchored flyouts, because a pick is invoked from places with no
// stable anchor — a palette row, a menu item, a bottom sheet — and because the primary ("Add") button must react to the
// picker's own state, which a frozen ContentDialog footer cannot. The dialog therefore carries only Cancel and the picker
// content owns its commit button.
//
// SCOPE, honestly: the entity list is the LIBRARY PROJECTION (SidebarPreferences.Entries) — playlists, albums, artists,
// shows and folders the user already has. There is no catalog/track SEARCH here: the sidebar data layer exposes no search
// service (the omnibar's lives in the search page), so a FixedTrack binding is offered as "the track playing now" instead
// of a track search. Both gaps are recorded in the wave's HANDOFF rather than faked.

static class SidebarPickers
{
    /// <summary>Pick a ROUTE or a LIBRARY ENTITY and hand back a ready <see cref="SidebarItemSpec"/> (id minted by the
    /// reducer). <paramref name="entitiesOnly"/> hides the routes tab (an EntityEmbed spotlights an entity, never a
    /// page); <paramref name="kindFilter"/> narrows the entity list (an <c>artistUri</c> config field wants artists).</summary>
    public static void OpenItem(ISidebarEditHost page, Action<SidebarItemSpec> onPick,
                                bool entitiesOnly = false, SidebarEntryKind? kindFilter = null)
    {
        if (page.OverlaySvc is not { } overlay) return;
        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(CzLoc.ItemAdd);
            // "" HIDES the primary button; `null` does NOT — ContentDialog reads `PrimaryText != ""` and then falls back
            // to the localized "OK" (ContentDialog.cs), so the old `null` shipped a stray OK next to Cancel that did
            // nothing (round-2 defect 6d). This picker commits on row click, so Cancel is genuinely the only button.
            d.PrimaryText = "";
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.DialogWidth = DialogW;
            d.Content = Embed.Comp(() => new SidebarItemPickerBody(page, spec =>
            {
                onPick(spec);
                handle?.Close();
            }, entitiesOnly, kindFilter));
        });
    }

    /// <summary>Both pickers' card width. The stock <c>ContentDialog</c> default is the 320-DIP minimum, which is narrower
    /// than the picker bodies ask for — the card then clamped the content instead of the content sizing the card
    /// (round-2 defect 6e). 480 is the width <c>ContentDialog</c> itself picks for a three-button dialog.</summary>
    internal const float DialogW = 480f;

    /// <summary>The body width inside <see cref="DialogW"/>: 480 − 2 × the 24-DIP <c>ContentDialogPadding</c>.</summary>
    internal const float BodyW = DialogW - 48f;

    /// <summary>Pick an ACTION from the registry and bind its target mode (REVISION 2's action picker).
    /// <paramref name="existing"/> pre-selects the current binding when re-binding an existing row.
    /// <para>The dialog owns NO built-in buttons: OK must be accent, must sit right of Cancel, and must DISABLE until an
    /// action is chosen (round-2 defect 6d) — and <c>ContentDialog.IsPrimaryButtonEnabled</c> is read once at card-build
    /// time, so it cannot track the picker's live state. The <c>Footer</c> seam (the <c>PlaybackRuntimeSetupCard</c>
    /// precedent) is the supported way to own that, which is why body and footer now share one
    /// <see cref="SidebarActionPickerModel"/>.</para></summary>
    public static void OpenAction(ISidebarEditHost page, SidebarActionBinding? existing,
                                  Action<SidebarActionBinding> onPick)
    {
        if (page.OverlaySvc is not { } overlay) return;
        var model = new SidebarActionPickerModel(page, existing);
        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(CzLoc.ItemAction);
            d.DialogWidth = DialogW;
            // The footer owns every button, so all three built-ins are suppressed ("" — not null, see OpenItem above).
            d.PrimaryText = "";
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = Embed.Comp(() => new SidebarActionPickerBody(model));
            d.Footer = Embed.Comp(() => new SidebarActionPickerFooter(model));
        });
        model.Commit = binding => { onPick(binding); handle?.Close(); };
        model.Cancel = () => handle?.Close();
    }
}

/// <summary>The action picker's public entry point under the name the page calls.</summary>
static class SidebarActionPicker
{
    public static void Open(ISidebarEditHost page, SidebarActionBinding? existing,
                            Action<SidebarActionBinding> onPick)
        => SidebarPickers.OpenAction(page, existing, onPick);
}

/// <summary>Routes ∪ library entities, filtered by one search box. Commits on row click — a single tap, because the
/// dialog's own footer cannot see this component's selection.</summary>
sealed class SidebarItemPickerBody : Component
{
    readonly ISidebarEditHost _page;
    readonly Action<SidebarItemSpec> _pick;
    readonly bool _entitiesOnly;
    readonly SidebarEntryKind? _kindFilter;
    readonly Signal<string> _query = new("");
    readonly Signal<int> _tab = new(0);

    public SidebarItemPickerBody(ISidebarEditHost page, Action<SidebarItemSpec> pick, bool entitiesOnly,
                                 SidebarEntryKind? kindFilter)
    {
        _page = page; _pick = pick; _entitiesOnly = entitiesOnly; _kindFilter = kindFilter;
    }

    public override Element Render()
    {
        string query = _query.Value;
        int tab = _entitiesOnly ? 1 : _tab.Value;
        var prefs = _page.Prefs;
        _ = prefs?.Entries.Version.Value ?? 0;      // the projection's epoch — a library refresh re-lists

        var rows = new List<Element>(24);
        string q = SidebarPalette.NormalizeQuery(query);
        if (tab == 0) AppendRoutes(rows, q);
        else AppendEntities(rows, q, prefs);

        var head = new List<Element>(2);
        if (!_entitiesOnly)
            head.Add(SelectorBar.Create(
                [Loc.Get("sidebar.palette.navigation"), Loc.Get("sidebar.palette.library")], _tab));
        head.Add(TextBox.Create(_query, null, new TextBox.TextBoxOptions
        {
            Placeholder = Loc.Get(CzLoc.SearchPlaceholder), Width = SidebarPickers.BodyW, Height = 32f,
        }));

        return new BoxEl
        {
            // The dialog now DECLARES its width (SidebarPickers.DialogW), so the body sizes to the card instead of the
            // card clamping the body (round-2 defect 6e).
            Direction = 1, Width = SidebarPickers.BodyW, Gap = Spacing.S, MinHeight = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Gap = Spacing.S, Shrink = 0f, Children = [.. head] },
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = 2f, Children = [.. rows],
                }) with { Height = 320f, Shrink = 0f, AutoEdgeFade = true, ScrollKey = "customizer.picker" },
            ],
        };
    }

    void AppendRoutes(List<Element> into, string q)
    {
        var routes = SidebarPinId.PinnableRoutes;
        for (int i = 0; i < routes.Length; i++) AppendRoute(into, routes[i], q);
        // Not pinnable, but legitimate STATIC LINK destinations (§C1.8: hand-picked app routes). The API console is
        // developer surface and AppendRoute drops it while developer mode is off — the same rule the palette's own
        // destination group rides (SidebarPalette.Filter), so the two offers cannot disagree.
        AppendRoute(into, "settings", q);
        AppendRoute(into, DeveloperMode.ApiConsoleRoute, q);
        AppendRoute(into, "concerts", q);
    }

    void AppendRoute(List<Element> into, string routeKey, string q)
    {
        if (!DeveloperMode.ShowsRoute(routeKey, DeveloperMode.Enabled.Peek())) return;
        var (title, glyph) = ShellNav.Dest(routeKey, null);
        if (!SidebarPalette.Matches(q, title, routeKey)) return;
        into.Add(PickerRow(glyph, title, routeKey, () => _pick(new SidebarItemSpec(
            SidebarIds.NewItem(), SidebarItemTarget.Route, routeKey))));
    }

    void AppendEntities(List<Element> into, string q, SidebarPreferences? prefs)
    {
        var entries = prefs?.Entries.Current;
        if (entries is null || entries.Count == 0)
        {
            into.Add(Note(Loc.Get("sidebar.v3.empty.library")));
            return;
        }
        int shown = 0;
        for (int i = 0; i < entries.Count && shown < 200; i++)
        {
            var e = entries[i];
            if (_kindFilter is { } want && e.Kind != want) continue;
            if (e.Kind is SidebarEntryKind.AppRoute or SidebarEntryKind.Track) continue;   // routes have their own tab
            if (e.Kind == SidebarEntryKind.Folder) continue;                               // a folder is not an item target
            if (!SidebarPalette.Matches(q, e.Name, e.Creator)) continue;
            shown++;
            var kind = KindOf(e.Kind);
            string uri = e.Uri;
            string name = e.Name;
            into.Add(PickerRow(SidebarIcons.ForEntityKind(kind), name, e.Creator, () => _pick(new SidebarItemSpec(
                SidebarIds.NewItem(), SidebarItemTarget.Entity, uri, kind, FallbackTitle: name))));
        }
        if (shown == 0) into.Add(Note(Loc.Format("sidebar.v3.empty.search", ("query", q))));
    }

    static SidebarEntityKind KindOf(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => SidebarEntityKind.Playlist,
        SidebarEntryKind.Album => SidebarEntityKind.Album,
        SidebarEntryKind.Artist => SidebarEntityKind.Artist,
        SidebarEntryKind.Show => SidebarEntityKind.Show,
        SidebarEntryKind.Folder => SidebarEntityKind.PlaylistFolder,
        SidebarEntryKind.Track => SidebarEntityKind.Track,
        _ => SidebarEntityKind.None,
    };

    internal static Element PickerRow(string glyph, string title, string? sub, Action onClick) => new BoxEl
    {
        Direction = 0, Height = 40f, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            Icon(glyph, 14f, Tok.TextSecondary),
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children = sub is { Length: > 0 }
                    ?
                    [
                        new TextEl(title) { Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ]
                    : [new TextEl(title) { Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            },
        ],
    }.Interactive(Interaction.ListRow);

    internal static Element Note(string text) => new TextEl(text)
    {
        Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
        Margin = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
    };
}

/// <summary>The action picker's state, shared by its BODY and its dialog FOOTER (round-2 defect 6d — the accent OK lives in
/// the footer so it can disable itself until an action is chosen, and a footer in a different component tree cannot reach
/// signals that live inside the body). A plain holder: no hooks, no rendering, one instance per opened dialog.</summary>
sealed class SidebarActionPickerModel
{
    public readonly ISidebarEditHost Page;
    public readonly Signal<string?> Key;
    public readonly Signal<int> Mode;
    public readonly Signal<string?> TargetKey;
    public readonly Signal<string?> TargetLabel = new(null);

    /// <summary>The mode picker's controlled index. Owned HERE, not in the row builder, because the mode row only exists
    /// once an action is chosen and a hook may never be called from a conditional branch.</summary>
    public readonly Signal<int> ModeIndex = new(0);

    /// <summary>Set by <c>SidebarPickers.OpenAction</c> after <c>ContentDialog.Show</c> returns the handle it closes.</summary>
    public Action<SidebarActionBinding>? Commit;
    public Action? Cancel;

    public SidebarActionPickerModel(ISidebarEditHost page, SidebarActionBinding? existing)
    {
        Page = page;
        Key = new Signal<string?>(existing is null ? null : WaveeExtensionRegistry.KeyOf(existing));
        Mode = new Signal<int>(existing is null ? (int)SidebarActionTargetMode.None : (int)existing.TargetMode);
        TargetKey = new Signal<string?>(existing?.TargetKey);
    }

    public WaveeActionDescriptor? Descriptor(string? key)
        => key is not null && Page.Registry is { } reg && reg.TryGetAction(key, out var d) ? d : null;

    public bool NeedsTarget()
        => (SidebarActionTargetMode)Mode.Value is SidebarActionTargetMode.FixedEntity
                                               or SidebarActionTargetMode.FixedTrack;

    /// <summary>True when the pick is committable: an action is selected AND, if its mode needs one, a target is set.</summary>
    public bool Ready() => Descriptor(Key.Value) is not null && (!NeedsTarget() || TargetKey.Value is { Length: > 0 });

    public void Choose(string actionKey)
    {
        Key.Value = actionKey;
        // Reset the mode to the FIRST mode this descriptor accepts, so a leftover mode from another action can never be
        // committed as ModeNotSupported.
        if (Page.Registry is { } reg && reg.TryGetAction(actionKey, out var d))
        {
            var modes = AcceptedModes(d);
            Mode.Value = modes.Count > 0 ? (int)modes[0] : (int)SidebarActionTargetMode.None;
        }
        TargetKey.Value = null;
        TargetLabel.Value = null;
    }

    /// <summary>Exactly the modes the descriptor declares — the customizer offers no others (a stored binding naming
    /// anything else resolves <c>ModeNotSupported</c>, so offering it would be a lie).</summary>
    public static List<SidebarActionTargetMode> AcceptedModes(WaveeActionDescriptor d)
    {
        var list = new List<SidebarActionTargetMode>(5);
        if ((d.AcceptedTargets & WaveeActionTargetModes.None) != 0) list.Add(SidebarActionTargetMode.None);
        if ((d.AcceptedTargets & WaveeActionTargetModes.FixedEntity) != 0) list.Add(SidebarActionTargetMode.FixedEntity);
        if ((d.AcceptedTargets & WaveeActionTargetModes.FixedTrack) != 0) list.Add(SidebarActionTargetMode.FixedTrack);
        if ((d.AcceptedTargets & WaveeActionTargetModes.NowPlaying) != 0) list.Add(SidebarActionTargetMode.NowPlaying);
        if ((d.AcceptedTargets & WaveeActionTargetModes.ActiveRoute) != 0) list.Add(SidebarActionTargetMode.ActiveRoute);
        return list;
    }

    public static string ModeLocKey(SidebarActionTargetMode mode) => mode switch
    {
        SidebarActionTargetMode.None => CzLoc.TargetNone,
        SidebarActionTargetMode.FixedEntity => CzLoc.TargetEntity,
        SidebarActionTargetMode.FixedTrack => CzLoc.TargetTrack,
        SidebarActionTargetMode.NowPlaying => CzLoc.TargetNowPlaying,
        _ => CzLoc.TargetRoute,
    };

    public static readonly List<SidebarActionTargetMode> EmptyModes = new();

    public static int IndexOfMode(List<SidebarActionTargetMode> modes, SidebarActionTargetMode mode)
    {
        for (int i = 0; i < modes.Count; i++)
            if (modes[i] == mode) return i;
        return 0;
    }

    /// <summary>The row SUBTITLE: what this action can be pointed at (round-2 defect 6c). It is the one thing that
    /// distinguishes two otherwise-identical rows — e.g. the two library "Save" verbs, one for tracks and one for
    /// entities — so it is a caption, not decoration.</summary>
    public static string TargetSummary(WaveeActionDescriptor d)
    {
        var modes = AcceptedModes(d);
        if (modes.Count == 0) return "";
        var sb = new System.Text.StringBuilder(48);
        for (int i = 0; i < modes.Count; i++)
        {
            if (i > 0) sb.Append(" · ");
            sb.Append(Loc.Get(ModeLocKey(modes[i])));
        }
        return sb.ToString();
    }

    public SidebarActionBinding Build(WaveeActionDescriptor descriptor)
    {
        var mode = (SidebarActionTargetMode)Mode.Value;
        // The descriptor's key is publisher + '.' + contribution; a BINDING stores the two halves separately (so a
        // currently-missing extension still round-trips), and WaveeExtensionKey.Compose is the exact inverse.
        string provider = WaveeExtensionKey.PublisherOf(descriptor.Key);
        string action = provider.Length > 0 && descriptor.Key.Length > provider.Length + 1
            ? descriptor.Key.Substring(provider.Length + 1)
            : descriptor.Key;
        return new SidebarActionBinding(provider, action, mode,
            mode is SidebarActionTargetMode.FixedEntity or SidebarActionTargetMode.FixedTrack ? TargetKey.Value : null,
            null);
    }
}

/// <summary>The action picker + its target-mode binding UI. Rows come from <c>WaveeExtensionRegistry.Actions</c> in
/// REGISTRATION ORDER (first-party first) — never from <c>AppActions.All</c> (REVISION 2's guardrail). Each row shows the
/// descriptor's icon, its label and a caption naming the targets it accepts; the selected row wears the app's standard
/// selection treatment (3-DIP accent bar + subtle plate), and a binding that cannot resolve says why.</summary>
sealed class SidebarActionPickerBody : Component
{
    readonly SidebarActionPickerModel _m;

    public SidebarActionPickerBody(SidebarActionPickerModel model) => _m = model;

    public override Element Render()
    {
        var registry = _m.Page.Registry;
        string? key = _m.Key.Value;
        var descriptor = _m.Descriptor(key);

        // HOOKS FIRST, unconditionally: the mode row below only exists once an action is chosen, so its controlled-index
        // mirror may not live inside it (a hook in a conditional branch breaks the stable call order).
        var modes = descriptor is null ? SidebarActionPickerModel.EmptyModes
                                      : SidebarActionPickerModel.AcceptedModes(descriptor);
        int activeMode = SidebarActionPickerModel.IndexOfMode(modes, (SidebarActionTargetMode)_m.Mode.Value);
        UseLayoutEffect(() => _m.ModeIndex.SetIfChanged(activeMode), DepKey.From(activeMode));

        var rows = new List<Element>(16);
        var actions = registry?.Actions;
        if (actions is null || actions.Count == 0)
            rows.Add(SidebarItemPickerBody.Note(Loc.Get(CzLoc.ExtensionManage)));
        else
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                rows.Add(ActionRow(a, string.Equals(a.Key, key, StringComparison.Ordinal)));
            }

        var tail = new List<Element>(4);
        if (descriptor is not null)
        {
            tail.Add(Divider());
            tail.Add(ModeRow(modes));
            if (_m.NeedsTarget()) tail.Add(TargetRow());
            tail.Add(ReasonRow(descriptor));
        }

        return new BoxEl
        {
            Direction = 1, Width = SidebarPickers.BodyW, Gap = Spacing.S, MinHeight = 0f,
            Children =
            [
                ScrollView(new BoxEl { Direction = 1, Gap = 2f, Children = [.. rows] }) with
                {
                    Height = 260f, Shrink = 0f, AutoEdgeFade = true, ScrollKey = "customizer.actionpicker",
                },
                new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 0f, Children = [.. tail] },
            ],
        };
    }

    Element ActionRow(WaveeActionDescriptor a, bool selected)
    {
        string akey = a.Key;
        string sub = SidebarActionPickerModel.TargetSummary(a);
        var icon = a.Icon();

        var lines = new List<Element>(2)
        {
            new TextEl(a.Label())
            {
                Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (sub.Length > 0)
            lines.Add(new TextEl(sub)
            {
                Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });

        return new BoxEl
        {
            Direction = 0, Height = 48f, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(2f, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            // The selection-aware 4-state ramp, set EXPLICITLY: `.Interactive(...)` would overwrite all three fills with
            // its recipe and erase the selected state.
            Fill = selected ? WaveeColors.SelectedRest : ColorF.Transparent,
            HoverFill = selected ? WaveeColors.SelectedHover : Tok.FillSubtleSecondary,
            PressedFill = selected ? WaveeColors.SelectedPressed : Tok.FillSubtleTertiary,
            BrushTransitionMs = Motion.ControlFaster,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.RadioButton,
            OnClick = () => _m.Choose(akey),
            Children =
            [
                // The app's standard selection mark. The row used to REPLACE the action's icon with a radio bullet when
                // selected, which hid the one thing identifying the row (round-2 defect 6c).
                new BoxEl
                {
                    Width = 3f, Height = 20f, Shrink = 0f, Corners = CornerRadius4.All(1.5f),
                    Fill = Tok.AccentDefault, Opacity = selected ? 1f : 0f, HitTestVisible = false,
                },
                new BoxEl
                {
                    Width = 28f, Height = 28f, Shrink = 0f, Corners = Radii.ControlAll,
                    Fill = selected ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    // ROUND-2 DEFECT 6a — THE TOFU. `a.Icon()` is an IconRef whose Font may be the APP-LOCAL WaveeIcons
                    // face: wavee.playNext (U+E900) and wavee.addToQueue (U+E901) live there, not in Segoe Fluent. The old
                    // row read only `.Glyph` and dropped `.Font`, so those two codepoints resolved against Segoe Fluent
                    // and rendered as □. Passing the ref's own family through `Ui.Icon(…, family:)` fixes both rows —
                    // no glyph-table change needed, because the glyphs were never missing, only mis-fonted.
                    Children =
                    [
                        Icon(icon.Glyph ?? Icons.More, 14f,
                             selected ? Tok.AccentTextPrimary
                                      : a.Destructive ? Tok.SystemFillCritical : Tok.TextSecondary,
                             icon.Font),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = 1f,
                    Justify = FlexJustify.Center,
                    Children = [.. lines],
                },
                selected
                    ? (Element)Icon(Icons.Accept, 14f, Tok.AccentTextPrimary)
                    : new BoxEl { Width = 14f, Shrink = 0f },
            ],
        };
    }

    Element ModeRow(List<SidebarActionTargetMode> modes)
    {
        if (modes.Count <= 1) return new BoxEl { Height = 0f };
        var labels = new string[modes.Count];
        for (int i = 0; i < modes.Count; i++) labels[i] = Loc.Get(SidebarActionPickerModel.ModeLocKey(modes[i]));
        // CzRow.Choice, never SelectorBar (round-2 defect 2): the mode labels are sentences ("Nothing (a global action)"),
        // so this reliably resolves to the dropdown rather than a tab strip clipping mid-word.
        return CzRow.Wide(Loc.Get(CzLoc.TargetLabel), null,
            CzRow.Choice(labels, _m.ModeIndex, i =>
            {
                if ((uint)i < (uint)modes.Count) _m.Mode.Value = (int)modes[i];
                _m.TargetKey.Value = null;
                _m.TargetLabel.Value = null;
            }));
    }

    Element TargetRow()
    {
        var mode = (SidebarActionTargetMode)_m.Mode.Value;
        string? label = _m.TargetLabel.Value ?? _m.TargetKey.Value;
        if (mode == SidebarActionTargetMode.FixedTrack)
        {
            // No track SEARCH exists in the sidebar data layer (see the file header) — the honest offer is the track
            // playing right now, captured as a fixed uri.
            string? nowPlaying = _m.Page.Acts?.Playback?.CurrentTrack.Value?.Uri;
            return CzRow.Prop(Loc.Get(CzLoc.TargetTrack), label ?? nowPlaying,
                Button.Create(Loc.Get(CzLoc.ItemAdd), () =>
                {
                    if (nowPlaying is { Length: > 0 })
                    {
                        _m.TargetKey.Value = nowPlaying;
                        _m.TargetLabel.Value = nowPlaying;
                    }
                }, ButtonAppearance.Standard, ControlSize.Small, isEnabled: nowPlaying is { Length: > 0 }));
        }
        return CzRow.Prop(Loc.Get(CzLoc.TargetEntity), label,
            Button.Create(Loc.Get(CzLoc.ItemAdd),
                () => SidebarPickers.OpenItem(_m.Page, spec =>
                {
                    _m.TargetKey.Value = spec.Key;
                    _m.TargetLabel.Value = spec.FallbackTitle ?? spec.Key;
                }, entitiesOnly: true),
                ButtonAppearance.Standard, ControlSize.Small));
    }

    /// <summary>Why the chosen binding would be inert (the platform's visible-but-disabled rule) — an unavailable target is
    /// still a LEGAL binding, so this explains rather than blocks. The commit button itself now lives in the footer.</summary>
    Element ReasonRow(WaveeActionDescriptor descriptor)
    {
        if (!_m.Ready()) return new BoxEl { Height = 0f };
        var resolution = _m.Page.Acts is { } acts && _m.Page.Registry is { } reg
            ? reg.Resolve(acts, _m.Build(descriptor))
            : default;
        if (resolution.Available || resolution.ReasonLocKey is not { } rk) return new BoxEl { Height = 0f };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Children =
            [
                Icon(Icons.StatusWarning, 12f, Tok.TextTertiary),
                new TextEl(Loc.Get(rk))
                {
                    Size = 11f, Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    MaxLines = 2, Wrap = TextWrap.Wrap,
                },
            ],
        };
    }
}

/// <summary>The action picker's dialog FOOTER: Cancel then the accent OK, with OK disabled until an action (and, when the
/// mode needs one, a target) is chosen — round-2 defect 6d, where the emphasis was inverted and a dead "OK" sat beside an
/// accent Cancel. It lives in <c>ContentDialog.Footer</c> because <c>IsPrimaryButtonEnabled</c> is read once at card-build
/// time and so cannot follow the picker's live state.
/// <para>Cancel is <c>Standard</c>, not <c>Subtle</c>: that is what <c>ContentDialog</c> itself gives a non-default command
/// button, and a third button treatment inside one dialog is the inconsistency this pass is removing.</para></summary>
sealed class SidebarActionPickerFooter : Component
{
    const float BtnMinW = 96f, BtnH = 32f;

    readonly SidebarActionPickerModel _m;

    public SidebarActionPickerFooter(SidebarActionPickerModel model) => _m = model;

    public override Element Render()
    {
        // Read BOTH signals so the footer re-renders (and re-evaluates `Ready`) on every pick and every target change.
        _ = _m.Key.Value;
        _ = _m.TargetKey.Value;
        _ = _m.Mode.Value;
        bool ready = _m.Ready();

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Shrink = 0f, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Children =
            [
                Button.Standard(Loc.Get(Strings.Auth.Cancel), () => _m.Cancel?.Invoke()) with
                {
                    MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center,
                },
                // The commit VERB, not a generic "OK": `CzLoc.ItemAdd` ("Add") is what this dialog actually does, and it is
                // a key this app's catalog owns (the engine's `dialog.ok` lives in FluentGpu's neutral floor, and the
                // unqualified `Strings` here is the app's generated class, not the engine's).
                Button.Accent(Loc.Get(CzLoc.ItemAdd), Commit, isEnabled: ready) with
                {
                    MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center, TabIndex = 1,
                },
            ],
        };
    }

    void Commit()
    {
        if (_m.Descriptor(_m.Key.Peek()) is not { } d || !_m.Ready()) return;
        _m.Commit?.Invoke(_m.Build(d));
    }
}
