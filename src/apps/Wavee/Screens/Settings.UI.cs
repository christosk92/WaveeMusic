// ── Screens/Settings.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the page shell (masthead, the 7-tab SelectorBar, the per-tab scroll lane), the shared row grammar every tab builds
// with, the glyph resolver, General (incl. the tray's Notification-area group), Notifications, the Logs mount, and
// the Screens-side install call
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 1200 lines
// Spec: DERIVED (ch 27 §9.3's 3,400, across its own four named partials); ch 27 §0 N1-N8/N11, §1, W1, W13, W14, W21,
// W28, W29, §6, §9; tray plan §8 (+ §13 decision 2: the icon default is "Only while hidden")
//
// THE SHAPE (ch 27 §1.2, adapted to a static partial class): ONE page component (`SettingsPageView`) whose render
// captures every context UNCONDITIONALLY — hooks may not sit behind the tab switch — into the page statics below, and
// then calls exactly one tab body. The tab bodies are static functions over the settings store, re-run on the page's
// own epoch (`Bump`) plus the foreign epochs each tab reads; any tab that needs a hook embeds a child component. The
// settings page is a single keep-alive route, so page-lifetime statics are the 0.2.9 instance fields, verbatim.
//
// MOUNT POINTS / SEAMS: `Settings.InstallScreens()` (the orchestrator's one call), `Settings.Page()`,
// `Settings.Open(Tab)`; `Settings.LogsPanelBody` (owner S assigns `Diagnostics.LogsPanel`), `Settings.SendTestEvent`
// (owner S assigns the notification simulator, G-218). The Appearance, Playback, Storage and About bodies live in
// their named partials.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Notifications;
using FluentGpu.WindowsApi.Packaging;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. INSTALL, THE PAGE, THE SEAMS ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The Screens-side install: registers the Settings page, then the What's-new page, the report dialog and
    /// the setup wizard (each owner file's own install). Called ONCE by the composition root after
    /// <see cref="Shell.InstallUi"/>.</summary>
    public static void InstallScreens()
    {
        Shell.SetPage(Shell.RouteKind.Settings, static (in Shell.Route route) => Page());
        ReleaseNotes.InstallUi();
        Feedback.InstallUi();
        Setup.InstallWizard();
    }

    // MOUNT POINT (stage B contract)
    public static Element Page() => Embed.Comp(static () => new SettingsPageView());

    /// <summary>Navigate to Settings on <paramref name="tab"/> (the palette, a toast's "Manage", a deep link).</summary>
    public static void Open(Tab tab)
    {
        s_tab.Value = (int)tab;
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings));
    }

    /// <summary>Owner S's log viewer (`Diagnostics.LogsPanel`). Null → the Logs tab says the viewer is unavailable.</summary>
    public static Func<Element>? LogsPanelBody;

    /// <summary>Owner S's notification simulator (G-218): push a real event of the topic through the pipeline and report
    /// what consumed it. Null → "Send event" answers <see cref="TestEventOutcome.Unavailable"/>.</summary>
    public static Func<NotifyTopic, TestEventResult>? SendTestEvent;

    // ══ 2. PAGE STATE (0.2.9's SettingsPage instance fields; page-lifetime) ════════════════════════════════════════

    const float ContentMaxWidth = 1000f;
    const float CardSpacing = 4f;
    static readonly Edges4 SectionHeaderMargin = new(0f, Spacing.XXXL, 0f, Spacing.S);

    static readonly Signal<int> s_tab = new(0);
    static readonly Signal<int> s_epoch = new(0);

    // Captured unconditionally by the page render, read by the tab bodies (never null after the first render).
    static IOverlayService? s_overlay;
    static InputHooks? s_hooks;
    static Action<Action> s_post = static a => a();
    static Action<float>? s_requestTheme;
    static float s_zoomLive = 1f;
    static Size2 s_viewportLive;

    static readonly Signal<int> s_language = new(0);
    static readonly Signal<int> s_trayMode = new((int)Tray.DefaultIconMode);

    /// <summary>Re-read every tab body: the settings store is not observable, so a writer bumps.</summary>
    static void Bump() => s_epoch.Value = s_epoch.Peek() + 1;

    // The named partials' hooks into the page lifecycle. Seed* run once per page mount (stable control signals are
    // re-read from the store); Enter* run on every tab change with whether their tab is now the visible one.
    static partial void SeedAppearance();
    static partial void SeedPlayback();
    static partial void SeedStorage();
    static partial void SeedAbout();
    static partial void EnterPlayback(bool active);
    static partial void EnterStorage(bool active);

    private static partial Element AppearanceTab();
    private static partial Element PlaybackTab();
    private static partial Element StorageTab();
    private static partial Element AboutTab();

    sealed class SettingsPageView : Component
    {
        public override Element Render()
        {
            // UNCONDITIONAL — above the tab switch (a conditional hook breaks hook ORDER).
            s_overlay = UseContext(Overlay.Service);
            s_hooks = UseContext(InputHooks.Current);
            s_requestTheme = UseContext(ThemeControl.Request);
            s_zoomLive = UseContext(Viewport.Zoom);          // display-only live zoom for the Appearance › Zoom row
            s_viewportLive = UseContext(Viewport.Size);
            var post = UsePost();
            s_post = post;

            UseEffect(() =>
            {
                s_language.Value = Language.IndexOf(Platform.Settings.Get(Platform.Keys.UiCulture));
                s_trayMode.Value = (int)Tray.ModeFrom(Platform.Settings.Get(Platform.Keys.TrayIconMode));
                SeedAppearance();
                SeedPlayback();
                SeedStorage();
                SeedAbout();
                // Leaving Settings (unmount) tears down what only the visible Playback tab may own: the video-override
                // flyout and its anchor (0.2.9 `UnmountVideoOverrides`).
                return (Action?)(static () => EnterPlayback(false));
            }, DepKey.Empty);
            // The route is keep-alive: a PARKED page is not unmounted, so the same teardown rides the deactivation edge.
            UseActivation(onDeactivated: static () => EnterPlayback(false));

            _ = s_epoch.Value;
            _ = Prefs.PlayerBar.Epoch.Value;
            int tab = Math.Clamp(s_tab.Value, 0, TabSlugs.Length - 1);

            UseEffect(() =>
            {
                EnterPlayback(tab == (int)Tab.Playback);
                EnterStorage(tab == (int)Tab.Storage);
            }, DepKey.From(tab));

            Element body = (Tab)tab switch
            {
                Tab.Appearance => AppearanceTab(),
                Tab.Playback => PlaybackTab(),
                Tab.Notifications => NotificationsTab(),
                Tab.Storage => StorageTab(),
                Tab.Logs => LogsTab(),
                Tab.About => AboutTab(),
                _ => GeneralTab(),
            };

            string slug = TabSlugs[tab];
            // N7/N8: the body swaps with no transition; only the scroller is keyed, per tab, so each tab keeps its own
            // offset. Logs is the one unscrolled lane: the panel owns the full remaining height.
            Element content = (Tab)tab == Tab.Logs
                ? new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
                    Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.L),
                    Children = [body],
                }
                : ScrollView(new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.PageWide),
                    Children = [ContentColumn(body)],
                }) with { Grow = 1f, ScrollKey = "settings:" + slug, Key = "settings:scroll:" + slug };

            // W28: the masthead and the strip are SIBLINGS of the scroller — nothing compacts, sticks or gains a shadow.
            return new BoxEl
            {
                Grow = 1f, Direction = 1,
                Children =
                [
                    Header(),
                    new BoxEl
                    {
                        Direction = 1, Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, 0f),
                        Children = [SelectorBar.Create(TabLabels(), s_tab), Divider()],
                    },
                    content,
                ],
            };
        }
    }

    static string[] TabLabels() =>
    [
        Loc.Get(Strings.Settings.Tabs.General),
        Loc.Get(Strings.Settings.Tabs.Appearance),
        Loc.Get(Strings.Settings.Tabs.Playback),
        Loc.Get(Strings.Settings.Notify.Title),
        Loc.Get(Strings.Settings.Tabs.Storage),
        Loc.Get(Strings.Settings.Tabs.Logs),
        Loc.Get(Strings.Settings.Tabs.About),
    ];

    // ══ 3. THE SHARED ROW GRAMMAR (0.2.9 `SettingsPage.cs:192-290`, `SettingsShared.cs`) ══════════════════════════

    /// <summary>W1's masthead: a 24-DIP gear + the 28/36/600 title, padded (36, 16, 36, 12).</summary>
    static Element Header() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.M),
        Children =
        [
            Icon(Icons.Settings, 24f, Tok.TextPrimary),
            Design.Type.PageHero(Loc.Get(Strings.Settings.Title)) with { Grow = 1f },
        ],
    };

    /// <summary>N1: a LEFT-aligned 1000-DIP column — never centred, never full-bleed.</summary>
    static Element ContentColumn(Element body) => new BoxEl
    {
        Direction = 1, MaxWidth = ContentMaxWidth, AlignSelf = FlexAlign.Stretch, Children = [body],
    };

    /// <summary>N2: cards 4 DIP apart.</summary>
    static Element TabStack(params Element[] children) => new BoxEl
    {
        Direction = 1, Gap = CardSpacing, AlignSelf = FlexAlign.Stretch, Children = children,
    };

    /// <summary>A group eyebrow: 16-DIP glyph + BodyStrong title, optionally a ≤ 2-line caption in the SAME column as
    /// the title; margin (0, 32, 0, 8). One line centres on the glyph; two lines hang from the top.</summary>
    static Element SectionHeader(string title, string? icon = null, string? subtitle = null)
    {
        bool hasSub = subtitle is { Length: > 0 };
        Element text = hasSub
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    BodyStrong(title),
                    Caption(subtitle!) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2 },
                ],
            }
            : BodyStrong(title);

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            AlignItems = hasSub ? FlexAlign.Start : FlexAlign.Center,
            Margin = SectionHeaderMargin,
            AlignSelf = FlexAlign.Stretch,
            Children = icon is null ? [text] : [Icon(icon, 16f, Tok.TextSecondary) with { Margin = new Edges4(0f, 2f, 0f, 0f) }, text],
        };
    }

    /// <summary>One settings row (a <see cref="SettingsCard"/>). Inert under the pointer unless click-enabled (W29 ①).</summary>
    static Element Row(string label, string? sub, Element? control = null, string? icon = null,
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

    /// <summary>One expander item (thresholds 0: never wraps, never drops its glyph).</summary>
    static Element Item(string label, string? sub, Element? control = null,
                        SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                        bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null, string? icon = null)
        => SettingsExpander.Item(label, sub, control, align, isEnabled, isClickEnabled, onClick, icon);

    /// <summary>N4: a collapsed picker group's ANSWER in its header — 14 DIP secondary, one line, char-ellipsis.</summary>
    static Element ValueTag(string value) => new TextEl(value)
    {
        Size = 14f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>Wide content (a card strip) for an expander's <c>ItemsHeader</c>: padding (16, 12, 16, 12), deliberately
    /// no fill/border — the expander body already paints the group chrome.</summary>
    static Element ExpanderPanel(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Children = [content],
    };

    /// <summary>A compact toggle over one bool key: a FRESH signal seeded from the store (the store is the truth, never
    /// a mirror), the write, the optional after-write hop, then the page bump.</summary>
    static Element Toggle(SettingKey<bool> key, Action<bool>? afterWrite = null, bool isEnabled = true)
        => ToggleSwitch.Create(new Signal<bool>(Platform.Settings.Get(key)), onChange: on =>
        {
            Platform.Settings.Set(key, on);
            afterWrite?.Invoke(on);
            Bump();
        }, isEnabled: isEnabled, style: SettingsCard.CompactToggleStyle());

    /// <summary>N11: every destructive action confirms through the ONE dialog shape, Cancel the default button.</summary>
    static void ConfirmThen(string title, string body, string primaryText, Action onConfirm)
        => Controls.Confirm(s_overlay, title, body, primaryText, onConfirm);

    /// <summary>The shared "still reading" block (ch 27 §3 ProgressRing sizes: 28 page, 20 census, 18 roster).</summary>
    static Element Loading(float size = 28f) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [ProgressRing.Create(size: size)],
    };

    /// <summary>Open a URI through the host (ms-settings:, https:), fail-soft.</summary>
    static void OpenUri(string uri)
    {
        try { (s_hooks?.OpenUri ?? InputHooks.Current.Default.OpenUri)?.Invoke(uri); }
        catch (Exception ex) { Log.Warn("settings", "could not open " + uri, ex); }
    }

    // ══ 4. GLYPHS (0.2.9 `SettingsGlyphs.cs`) ══════════════════════════════════════════════════════════════════════

    static readonly HashSet<string> s_unmappedGlyphs = new(StringComparer.Ordinal);

    /// <summary>A catalog glyph NAME → its <see cref="Icons"/> constant. An unmapped name fails loudly in Debug at the
    /// offending row, and in Release logs <c>settings.glyph.unmapped</c> once per name and paints the gear.</summary>
    static string Glyph(string name) => name switch
    {
        "Globe" => Icons.Globe, "Link" => Icons.Link, "Devices" => Icons.Devices, "Device" => Icons.Device,
        "Code" => Icons.Code, "LocaleLanguage" => Icons.LocaleLanguage, "OpenInNewWindow" => Icons.OpenInNewWindow,
        "Settings" => Icons.Settings, "Clock" => Icons.Clock, "Document" => Icons.Document, "Refresh" => Icons.Refresh,
        "Brush" => Icons.Brush, "Sun" => Icons.Sun, "Zoom" => Icons.Zoom, "Font" => Icons.Font, "Design" => Icons.Design,
        "List" => Icons.List, "RowSize" => Icons.RowSize, "Picture" => Icons.Picture, "ViewList" => Icons.ViewList,
        "DockLeft" => Icons.DockLeft, "SplitView" => Icons.SplitView, "Edit" => Icons.Edit, "Microphone" => Icons.Microphone,
        "RefineSparkle" => Icons.RefineSparkle, "Filter" => Icons.Filter, "MusicNote" => Icons.MusicNote,
        "Headphones" => Icons.Headphones, "RadioTower" => Icons.RadioTower, "Volume" => Icons.Volume, "Play" => Icons.Play,
        "Speakers" => Icons.Speakers, "Equalizer" => Icons.Equalizer, "Audio" => Icons.Audio, "Movie" => Icons.Movie,
        "TvMonitor" => Icons.TvMonitor, "Pin" => Icons.Pin, "ThisPc" => Icons.ThisPc, "Album" => Icons.Album,
        "Folder" => Icons.Folder, "FolderOpen" => Icons.FolderOpen, "Tag" => Icons.Tag, "Delete" => Icons.Delete,
        "Attention" => Icons.Attention, "Download" => Icons.Download, "ChromeClose" => Icons.ChromeClose,
        "ChromeMinimize" => Icons.ChromeMinimize, "RevealPassword" => Icons.RevealPassword, "Contact" => Icons.Contact,
        _ => UnmappedGlyph(name),
    };

    static string UnmappedGlyph(string name)
    {
        System.Diagnostics.Debug.Fail("Settings glyph name '" + name + "' has no Icons mapping.");
        if (s_unmappedGlyphs.Add(name))
            Log.Warn("settings", "settings.glyph.unmapped: Settings glyph name has no Icons mapping; using the default glyph (name=" + name + ")");
        return Icons.Settings;
    }

    static string SectionGlyph(Tab tab, string title) => Glyph(Catalog.SectionGlyph(tab, title));

    static string RowGlyph(Tab tab, string rowId) => Glyph(Catalog.RowGlyph(tab, rowId));

    // ══ 5. GENERAL (W1; tray plan §8) ══════════════════════════════════════════════════════════════════════════════

    static Element GeneralTab()
    {
        var kids = new List<Element>(20)
        {
            SectionHeader(Loc.Get(Strings.Settings.Language.Title), SectionGlyph(Tab.General, "Language & region"),
                Loc.Get(Strings.Settings.Language.Subtitle)),
            Row(Loc.Get(Strings.Settings.Language.Label), Loc.Get(Strings.Settings.Language.RestartSub),
                ComboBox.Create(LanguageLabels(), s_language, width: 260f, onChange: SetLanguage, itemEnabled: Language.Enabled),
                RowGlyph(Tab.General, "language")),

            SectionHeader(Loc.Get(Strings.Settings.Links.Title), SectionGlyph(Tab.General, "Links"),
                Loc.Get(Strings.Settings.Links.Subtitle)),
            SpotifyLinksRow(),
        };

        kids.Add(SectionHeader(Loc.Get(Strings.Settings.General.Tray.Title), SectionGlyph(Tab.General, "Notification area"),
            Loc.Get(Strings.Settings.General.Tray.Subtitle)));
        AddTrayRows(kids);

        kids.Add(SectionHeader(Loc.Get(Strings.Settings.Gpu.Title), SectionGlyph(Tab.General, "Graphics"),
            Loc.Get(Strings.Settings.Gpu.Subtitle)));
        kids.Add(Embed.Comp(static () => new GpuPickerCard()));

        kids.Add(SectionHeader(Loc.Get(Strings.Settings.Diag.Title), SectionGlyph(Tab.General, "Developer"),
            Loc.Get(Strings.Settings.Diag.Subtitle)));
        AddDeveloperRows(kids);

        return TabStack(kids.ToArray());
    }

    static string[] LanguageLabels() =>
    [
        Loc.Get(Strings.Settings.Language.System),
        Loc.Get(Strings.Settings.Language.EnglishUs),
        Loc.Get(Strings.Settings.Language.Dutch),
        Loc.Get(Strings.Settings.Language.Korean),
    ];

    static void SetLanguage(int index)
    {
        if (!Language.CanPick(index)) return;   // belt-and-braces behind the combo's own disabled items
        Platform.Settings.Set(Platform.Keys.UiCulture, Language.Codes[index]);
        s_language.Value = index;
        Bump();
    }

    /// <summary>The opt-in <c>spotify:</c> handler, applied AT THE TOGGLE (the next link opens here / the scheme goes
    /// straight back). A packaged build's manifest owns its protocols, so the row says Windows manages it.</summary>
    static Element SpotifyLinksRow()
    {
        bool packaged = IsPackaged();
        return Row(Loc.Get(Strings.Settings.Links.Spotify),
            Loc.Get(packaged ? Strings.Settings.General.SpotifyLinksPackagedSub : Strings.Settings.Links.SpotifySub),
            Toggle(Platform.Keys.HandleSpotifyLinks, afterWrite: SyncSpotifyScheme, isEnabled: !packaged),
            RowGlyph(Tab.General, "spotifyLinks"), isEnabled: !packaged);
    }

    /// <summary>The tray plan §8 group. Every writer calls <c>Tray.Host.OnSettingsChanged()</c> (icon presence, the
    /// foreground hook and — through it — <c>Shell.SyncStartupRegistration()</c> follow).</summary>
    static void AddTrayRows(List<Element> kids)
    {
        var mode = Tray.ModeFrom(Platform.Settings.Get(Platform.Keys.TrayIconMode));
        bool hideAllowed = Tray.HideModesAllowed(mode);
        bool startOnLogin = Platform.Settings.Get(Platform.Keys.StartOnLogin);
        bool startHiddenAvailable = Tray.StartHiddenAvailable(startOnLogin, mode);
        string needsIcon = Loc.Get(Strings.Settings.General.Tray.NeedsIcon);

        kids.Add(Row(Loc.Get(Strings.Settings.General.Tray.IconMode),
            Loc.Get(mode == Tray.IconMode.Never ? Strings.Settings.General.Tray.IconNeverSub : Strings.Settings.General.Tray.IconModeSub),
            ComboBox.Create(
                [
                    Loc.Get(Strings.Settings.General.Tray.IconAlways),
                    Loc.Get(Strings.Settings.General.Tray.IconWhileHidden),
                    Loc.Get(Strings.Settings.General.Tray.IconNever),
                ],
                s_trayMode, width: 260f, onChange: static i =>
                {
                    if (i is < 0 or > 2) return;
                    Platform.Settings.Set(Platform.Keys.TrayIconMode, i);
                    s_trayMode.Value = i;
                    Tray.Host.OnSettingsChanged();
                    Bump();
                }),
            RowGlyph(Tab.General, "trayIcon")));

        kids.Add(Row(Loc.Get(Strings.Settings.General.Tray.CloseToTray),
            hideAllowed ? Loc.Get(Strings.Settings.General.Tray.CloseToTraySub) : needsIcon,
            Toggle(Platform.Keys.TrayCloseToTray, afterWrite: static _ => Tray.Host.OnSettingsChanged(), isEnabled: hideAllowed),
            RowGlyph(Tab.General, "closeToTray"), isEnabled: hideAllowed));

        kids.Add(Row(Loc.Get(Strings.Settings.General.Tray.MinimizeToTray),
            hideAllowed ? Loc.Get(Strings.Settings.General.Tray.MinimizeToTraySub) : needsIcon,
            Toggle(Platform.Keys.TrayMinimizeToTray, afterWrite: static _ => Tray.Host.OnSettingsChanged(), isEnabled: hideAllowed),
            RowGlyph(Tab.General, "minimizeToTray"), isEnabled: hideAllowed));

        kids.Add(Row(Loc.Get(Strings.Settings.General.Tray.StartHidden),
            startHiddenAvailable ? Loc.Get(Strings.Settings.General.Tray.StartHiddenSub)
                : !hideAllowed ? needsIcon : Loc.Get(Strings.Settings.General.Tray.NeedsStartOnLogin),
            Toggle(Platform.Keys.TrayStartHidden, afterWrite: static _ => Tray.Host.OnSettingsChanged(), isEnabled: startHiddenAvailable),
            RowGlyph(Tab.General, "startHidden"), isEnabled: startHiddenAvailable));

        kids.Add(Row(Loc.Get(Strings.Settings.General.StartOnLogin), Loc.Get(Strings.Settings.General.StartOnLoginSub),
            Toggle(Platform.Keys.StartOnLogin, afterWrite: static _ =>
            {
                // Tray.Host.OnSettingsChanged calls Shell.SyncStartupRegistration first; call it directly too so the
                // Run value follows even before the tray host has booted.
                Shell.SyncStartupRegistration();
                Tray.Host.OnSettingsChanged();
            }),
            RowGlyph(Tab.General, "startOnLogin")));
    }

    /// <summary>Developer: the app's ONE developer switch, the FPS overlay (present but greyed while developer mode is
    /// off — never hidden), the dealer archive, and "Simulate an update" (composed away while developer mode is off).</summary>
    static void AddDeveloperRows(List<Element> kids)
    {
        bool dev = Platform.Settings.Get(Platform.Keys.DeveloperMode);
        kids.Add(Row(Loc.Get(Strings.Settings.Diag.DeveloperMode), Loc.Get(Strings.Settings.Diag.DeveloperModeSub),
            Toggle(Platform.Keys.DeveloperMode), RowGlyph(Tab.General, "developerMode")));
        kids.Add(Row(Loc.Get(Strings.Settings.Diag.FpsOverlay), Loc.Get(Strings.Settings.Diag.FpsOverlaySub),
            Toggle(Platform.Keys.FpsOverlay, isEnabled: dev), RowGlyph(Tab.General, "fpsOverlay"), isEnabled: dev));
        kids.Add(Row(Loc.Get(Strings.Settings.Diag.DealerArchive), Loc.Get(Strings.Settings.Diag.DealerArchiveSub),
            Toggle(Platform.Keys.DealerArchiveEnabled), RowGlyph(Tab.General, "dealerArchive")));
        if (dev)
            kids.Add(Row(Loc.Get(Strings.Settings.Diag.SimulateUpdate), Loc.Get(Strings.Settings.Diag.SimulateUpdateSub),
                Button.Standard(Loc.Get(Strings.Settings.Diag.SimulateUpdateButton), static () => Update.Host.SimulateUpdate()),
                RowGlyph(Tab.General, "simulateUpdate")));
    }

    /// <summary>Settings › General › Graphics — the render-GPU picker. Its own component so the seeding effect lives on
    /// a child whose lifetime IS the tab. The adapter list is enumerated ONCE at mount (a cold DXGI walk) and frozen;
    /// picking persists LUID + name and live-applies through the engine's device-reset path.</summary>
    sealed class GpuPickerCard : Component
    {
        readonly Signal<int> _selected = new(0);
        readonly IReadOnlyList<GpuAdapterDesc> _adapters = SafeAdapters();

        static IReadOnlyList<GpuAdapterDesc> SafeAdapters()
        {
            try { return GpuAdapterInfo.EnumerateAdapters(); }
            catch (Exception ex) { Log.Warn("settings", "GPU adapter enumeration failed", ex); return []; }
        }

        public override Element Render()
        {
            UseEffect(() =>
            {
                long luid = Platform.Settings.Get(Platform.Keys.PreferredGpuLuid);
                string name = Platform.Settings.Get(Platform.Keys.PreferredGpuName);
                int idx = 0;
                if (luid != 0L || name.Length > 0)
                    for (int k = 0; k < _adapters.Count; k++)
                    {
                        if (luid != 0L && _adapters[k].Luid == luid) { idx = k + 1; break; }
                        if (idx == 0 && name.Length > 0 && string.Equals(_adapters[k].Name, name, StringComparison.Ordinal)) idx = k + 1;
                    }
                _selected.Value = idx;
            }, DepKey.Empty);

            var labels = new string[_adapters.Count + 1];
            labels[0] = Loc.Get(Strings.Settings.Gpu.Automatic);
            for (int k = 0; k < _adapters.Count; k++)
            {
                var a = _adapters[k];
                labels[k + 1] = a.IsCurrent ? Strings.Settings.Gpu.InUse(a.Name) : a.Name;
            }

            return Row(Loc.Get(Strings.Settings.Gpu.Label), Loc.Get(Strings.Settings.Gpu.RestartSub),
                ComboBox.Create(labels, _selected, width: 300f, onChange: Pick), RowGlyph(Tab.General, "preferredGpu"));
        }

        void Pick(int i)
        {
            if (i <= 0 || i > _adapters.Count)
            {
                Platform.Settings.Set(Platform.Keys.PreferredGpuLuid, 0L);
                Platform.Settings.Set(Platform.Keys.PreferredGpuName, "");
                _selected.Value = 0;
                GpuAdapterInfo.RequestAdapterSwitch(0L);
                return;
            }
            var a = _adapters[i - 1];
            Platform.Settings.Set(Platform.Keys.PreferredGpuLuid, a.Luid);
            Platform.Settings.Set(Platform.Keys.PreferredGpuName, a.Name);
            _selected.Value = i;
            GpuAdapterInfo.RequestAdapterSwitch(a.Luid);
        }
    }

    // ══ 6. NOTIFICATIONS (W13, W14) ════════════════════════════════════════════════════════════════════════════════
    //
    // Two global gates, then ONE LADDER PER TOPIC (Off → In Wavee → Windows), in `Notify.Prefs.AllTopics` order — read,
    // never re-listed (A9). Not catalog-driven: the Bell/gear glyphs repeat on purpose (ch 27 parity 75a).

    static Element NotificationsTab()
    {
        _ = Notify.Prefs.Epoch.Value;          // any dial write, from here or the centre, re-reads the tab
        bool dev = Platform.Settings.Get(Platform.Keys.DeveloperMode);
        var policy = Notify.Prefs.Policy();
        var children = new List<Element>(16);

        // The honesty layer FIRST: when Windows suppresses Wavee every dial below is theatre until that is fixed.
        if (BlockedBar(NotifyRules.Blocked(ToastDelivery())) is { } blocked) children.Add(blocked);

        children.Add(SectionHeader(Loc.Get(Strings.Settings.Notify.DeliveryTitle), Icons.Bell));
        children.Add(Row(Loc.Get(Strings.Settings.Notify.Windows), Loc.Get(Strings.Settings.Notify.WindowsSub),
            NotifyToggle(Platform.Keys.NotifyWindows, reconcile: true), Icons.Bell));
        children.Add(Row(Loc.Get(Strings.Settings.Notify.Sound), Loc.Get(Strings.Settings.Notify.SoundSub),
            NotifyToggle(Platform.Keys.NotifySound, reconcile: false), Icons.Bell, isEnabled: policy.WindowsEnabled));
        children.Add(Row(Loc.Get(Strings.Settings.Notify.Quiet), Loc.Get(Strings.Settings.Notify.QuietSub),
            NotifyToggle(Platform.Keys.NotifyQuietEnabled, reconcile: true), Icons.Moon, isEnabled: policy.WindowsEnabled));
        if (policy.WindowsEnabled && policy.Quiet.Enabled)
            children.Add(QuietRange(policy));

        children.Add(SectionHeader(Loc.Get(Strings.Settings.Notify.TopicsTitle), Icons.Settings));
        children.Add(new BoxEl
        {
            MinWidth = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
            Children = [Body(Loc.Get(Strings.Settings.Notify.TopicsHint)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3 }],
        });
        if (!policy.WindowsEnabled)
            children.Add(InfoBar.Create(InfoBarSeverity.Informational, title: "",
                message: Loc.Get(Strings.Settings.Notify.WindowsOffHint), isClosable: false));
        foreach (var topic in Notify.Prefs.AllTopics)
            children.Add(TopicRow(topic, dev));

        return TabStack(children.ToArray());
    }

    static Element TopicRow(NotifyTopic topic, bool dev)
    {
        var level = Notify.Prefs.Level(topic);
        // A topic that cannot reach Windows renders TWO segments, never three-with-one-dead.
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];

        var dial = SelectorBar.Create(labels, new Signal<int>((int)level), onChange: i =>
        {
            Notify.Prefs.SetLevel(topic, (NotifyLevel)i);
            // A SCHEDULED topic dialled down must give back the toast the OS already holds.
            if (NotificationPolicy.IsScheduled(topic)) Notify.RequestReconcile();
            Bump();
        });

        if (!dev) return Row(TopicLabel(topic), TopicSub(topic), dial, TopicGlyph(topic));

        // Developer surface: the dial stays where it is; the chevron reveals "Send a test event".
        string sendSub = Loc.Get(NotificationPolicy.IsScheduled(topic) ? Strings.Settings.Notify.SendScheduledSub : Strings.Settings.Notify.SendSub);
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = TopicLabel(topic),
            Description = TopicSub(topic),
            HeaderIcon = TopicGlyph(topic),
            Content = dial,
            Items =
            [
                Item(Loc.Get(Strings.Settings.Notify.Send), sendSub,
                    Button.Standard(Loc.Get(Strings.Settings.Notify.SendButton), () => RunTestEvent(topic))),
            ],
        });
    }

    /// <summary>Report what the PIPELINE decided — the report is the feature; the banner is one of its outcomes. Enabled
    /// at every level including Off: "nothing happens, as configured" is otherwise unverifiable.</summary>
    static void RunTestEvent(NotifyTopic topic)
    {
        var result = SendTestEvent is { } send ? send(topic) : new TestEventResult(TestEventOutcome.Unavailable, null);
        string key = NotifyRules.OutcomeKey(result.Outcome);
        string message = NotifyRules.OutcomeHasClock(result.Outcome)
            ? Loc.Format(key, ("0", NotifyRules.Clock(result.At, CultureInfo.CurrentCulture)))
            : Loc.Get(key);
        var severity = NotifyRules.OutcomeSeverity(result.Outcome) switch
        {
            1 => InfoBarSeverity.Success,
            2 => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        Notify.Say(message, severity, durationMs: 6000f);
        Bump();   // a new bell row / a changed scheduled count is visible on this page
    }

    static string TopicLabel(NotifyTopic topic) => Loc.Get(topic switch
    {
        NotifyTopic.NewAlbums => Strings.Settings.Notify.NewAlbums,
        NotifyTopic.NewEpisodes => Strings.Settings.Notify.NewEpisodes,
        NotifyTopic.ReleaseDrops => Strings.Settings.Notify.ReleaseDrops,
        NotifyTopic.Concerts => Strings.Settings.Notify.Concerts,
        NotifyTopic.Followers => Strings.Settings.Notify.Followers,
        NotifyTopic.DaylistRefresh => Strings.Settings.Notify.Daylist,
        NotifyTopic.AppUpdates => Strings.Settings.Notify.AppUpdates,
        _ => Strings.Settings.Notify.LibraryActivity,
    });

    static string TopicSub(NotifyTopic topic)
    {
        string sub = Loc.Get(topic switch
        {
            NotifyTopic.NewAlbums => Strings.Settings.Notify.NewAlbumsSub,
            NotifyTopic.NewEpisodes => Strings.Settings.Notify.NewEpisodesSub,
            NotifyTopic.ReleaseDrops => Strings.Settings.Notify.ReleaseDropsSub,
            NotifyTopic.Concerts => Strings.Settings.Notify.ConcertsSub,
            NotifyTopic.Followers => Strings.Settings.Notify.FollowersSub,
            NotifyTopic.DaylistRefresh => Strings.Settings.Notify.DaylistSub,
            NotifyTopic.AppUpdates => Strings.Settings.Notify.AppUpdatesSub,
            _ => Strings.Settings.Notify.LibraryActivitySub,
        });
        return NotificationPolicy.IsScheduled(topic) ? sub + "  ·  " + Loc.Get(Strings.Settings.Notify.ClosedBadge) : sub;
    }

    static string TopicGlyph(NotifyTopic topic) => topic switch
    {
        NotifyTopic.NewAlbums => Icons.Album,
        NotifyTopic.NewEpisodes => Icons.Microphone,
        NotifyTopic.ReleaseDrops => Icons.Heart,
        NotifyTopic.Concerts => Icons.Calendar,
        NotifyTopic.Followers => Icons.Bell,
        NotifyTopic.DaylistRefresh => Icons.Sun,
        NotifyTopic.AppUpdates => Icons.Download,
        _ => Icons.Clock,
    };

    static Element NotifyToggle(SettingKey<bool> key, bool reconcile)
        => Toggle(key, afterWrite: _ =>
        {
            Notify.Prefs.Bump();
            if (reconcile) Notify.RequestReconcile();
        });

    static Element QuietRange(in NotificationPolicy policy)
    {
        string[] hours = NotifyRules.HourLabels();
        return Row(Loc.Get(Strings.Settings.Notify.QuietFrom) + " / " + Loc.Get(Strings.Settings.Notify.QuietTo), null,
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
                Children =
                [
                    HourCombo(hours, Platform.Keys.NotifyQuietFromHour, policy.Quiet.FromHour),
                    HourCombo(hours, Platform.Keys.NotifyQuietToHour, policy.Quiet.ToHour),
                ],
            },
            Icons.Clock);
    }

    static Element HourCombo(string[] hours, SettingKey<int> key, int value)
        => ComboBox.Create(hours, new Signal<int>(Math.Clamp(value, 0, 23)), width: 120f, onChange: h =>
        {
            Platform.Settings.Set(key, Math.Clamp(h, 0, 23));
            Notify.Prefs.Bump();
            Notify.RequestReconcile();
            Bump();
        });

    static ToastDeliverySetting ToastDelivery()
    {
        try { return ToastNotifier.Default.Setting; }
        catch (Exception) { return ToastDeliverySetting.Unknown; }
    }

    static Element? BlockedBar(BlockedBanner banner)
    {
        (string Title, string Body)? text = banner switch
        {
            BlockedBanner.App => (Loc.Get(Strings.Settings.Notify.BlockedApp), Loc.Get(Strings.Settings.Notify.BlockedAppSub)),
            BlockedBanner.User => (Loc.Get(Strings.Settings.Notify.BlockedUser), Loc.Get(Strings.Settings.Notify.BlockedUserSub)),
            BlockedBanner.Policy => (Loc.Get(Strings.Settings.Notify.BlockedPolicy), Loc.Get(Strings.Settings.Notify.BlockedPolicySub)),
            _ => null,
        };
        if (text is not { } t) return null;
        Element? action = NotifyRules.OffersOpenSettings(banner)
            ? Button.Standard(Loc.Get(Strings.Settings.Notify.OpenWindows), static () => OpenUri("ms-settings:notifications"))
            : null;
        return InfoBar.Create(InfoBarSeverity.Warning, t.Title, t.Body, isClosable: false, actionButton: action);
    }

    // ══ 7. LOGS (N8, W21 — the panel is owner S's) ═════════════════════════════════════════════════════════════════

    static Element LogsTab()
    {
        if (LogsPanelBody is { } body)
            return new BoxEl { Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Children = [body()] };
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl(Loc.Get("settings.logs.unavailable")) { Size = 13f, Color = Tok.TextSecondary }],
        };
    }

    static bool IsPackaged()
    {
        try { return PackageIdentity.IsPackaged; }
        catch (Exception) { return false; }
    }
}
