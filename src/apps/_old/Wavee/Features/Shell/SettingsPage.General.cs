using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The General tab: Language & region · Links · Graphics · Developer. Four short, single-purpose groups — the opposite
// of the old "General" catch-all this file used to hold (Theme, Palette, Mica Alt, Zoom, Visual effects, Row density,
// Track list style, Track page layout, Sidebar, Lyrics, Language, Links, Run setup again all in one tab). Appearance,
// Playback, Sound and Lyrics moved to their own tabs (SettingsPage.Appearance.cs); the palette picker, Mica Alt and the
// two Track-page-layout rows are gone outright; "Run setup again" is gone with the settings-tour onboarding pages.
//
// Developer is new here: the three switches and "Simulate an update" used to live on the old Diagnostics tab
// (DiagnosticsPanel.DiagnosticSwitches) above a log viewer that had nothing to do with them. Diagnostics is now Logs —
// the full-height log viewer alone (Workstream C) — and these move to the tab that already holds every other
// infrequently-touched app-behavior toggle.
sealed partial class SettingsPage
{
    readonly Signal<int> _language = new(0);

    // Enabled is a parallel mask: nl / ko-KR are shown but DISABLED (greyed, unselectable) — their tables aren't
    // complete enough to ship as a pick yet. Keep them visible so the row advertises what's coming; flip to true
    // per locale as each table lands. System + en-US stay enabled.
    static (string[] Codes, string[] Labels, bool[] Enabled) LanguageOptions()
    {
        return (
            ["system", "en-US", "nl", "ko-KR"],
            [
                Loc.Get(Strings.Settings.Language.System),
                Loc.Get(Strings.Settings.Language.EnglishUs),
                Loc.Get(Strings.Settings.Language.Dutch),
                Loc.Get(Strings.Settings.Language.Korean),
            ],
            [true, true, false, false]);
    }

    static int LanguageIndex(string culture)
    {
        var (codes, _, _) = LanguageOptions();
        for (int i = 0; i < codes.Length; i++)
            if (string.Equals(codes[i], culture, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    // The scheme association is applied AT THE TOGGLE, not at next launch: a user who turns this on expects the very
    // next spotify: link to open here, and one who turns it off expects the scheme handed straight back.
    Element SpotifyLinksToggle(IAppSettings? settings)
        => ToggleSwitch.Create(new Signal<bool>(settings?.Get(WaveeSettings.HandleSpotifyLinks) ?? false), onChange: _ =>
        {
            if (settings is null) return;
            bool next = !settings.Get(WaveeSettings.HandleSpotifyLinks);
            settings.Set(WaveeSettings.HandleSpotifyLinks, next);
            DeepLink.SyncSpotifySchemeRegistration(next);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

    Element GeneralTab(Services? svc, NotificationCenterBridge? nc)
    {
        var settings = svc?.Settings;
        var languageOptions = LanguageOptions();

        void SetLanguage(int i)
        {
            if (settings is null || (uint)i >= (uint)languageOptions.Codes.Length) return;
            if (!languageOptions.Enabled[i]) return;   // belt-and-suspenders: the ComboBox already rejects a disabled pick
            settings.Set(WaveeSettings.UiCulture, languageOptions.Codes[i]);
            _language.Value = i;
            Bump();
        }

        var kids = new List<Element>
        {
            SettingsSectionHeader(Loc.Get(Strings.Settings.Language.Title),
                SettingsGlyphs.Section(SettingsTab.General, "Language & region"),
                Loc.Get(Strings.Settings.Language.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Language.Label), Loc.Get(Strings.Settings.Language.RestartSub),
                ComboBox.Create(languageOptions.Labels, _language, width: 260f, isEnabled: settings is not null,
                    onChange: SetLanguage, itemEnabled: languageOptions.Enabled),
                SettingsGlyphs.Row(SettingsTab.General, "language")),

            SettingsSectionHeader(Loc.Get(Strings.Settings.Links.Title),
                SettingsGlyphs.Section(SettingsTab.General, "Links"),
                Loc.Get(Strings.Settings.Links.Subtitle)),
            SettingsRow(Loc.Get(Strings.Settings.Links.Spotify), Loc.Get(Strings.Settings.Links.SpotifySub),
                SpotifyLinksToggle(settings), SettingsGlyphs.Row(SettingsTab.General, "spotifyLinks")),

            SettingsSectionHeader(Loc.Get(Strings.Settings.Gpu.Title),
                SettingsGlyphs.Section(SettingsTab.General, "Graphics"),
                Loc.Get(Strings.Settings.Gpu.Subtitle)),
            Embed.Comp(() => new GpuPickerCard()),
        };
        kids.Add(SettingsSectionHeader(Loc.Get(Strings.Settings.Diag.Title),
            SettingsGlyphs.Section(SettingsTab.General, "Developer"),
            Loc.Get(Strings.Settings.Diag.Subtitle)));
        kids.AddRange(DeveloperSwitches(settings, nc));

        return SettingsTabStack(kids.ToArray());
    }

    // ── Developer (moved from the old Diagnostics tab's DiagnosticSwitches) ─────────────────────────────────────────
    //
    // Developer mode is the app's ONE switch for developer surface (App/DeveloperMode.cs): the sidebar's API console,
    // Library V3's overflow entry, the lyrics inspector, the per-topic "Send event" rows and the home image tracer all
    // read it. FPS overlay is meaningless without the developer surface it belongs to, so it is disabled (not hidden)
    // while developer mode is off. Dealer archive and Simulate update are independent, deliberately-heavy dev tools —
    // Simulate update is composed away entirely (not merely disabled) while developer mode is off.
    Element[] DeveloperSwitches(IAppSettings? settings, NotificationCenterBridge? nc)
    {
        bool dev = DeveloperMode.Enabled.Value;

        var rows = new List<Element>(4)
        {
            SwitchRow(Strings.Settings.Diag.DeveloperMode, Strings.Settings.Diag.DeveloperModeSub,
                SettingsGlyphs.Row(SettingsTab.General, "developerMode"), dev, isEnabled: true,
                on => DeveloperMode.Set(settings, on)),

            SwitchRow(Strings.Settings.Diag.FpsOverlay, Strings.Settings.Diag.FpsOverlaySub,
                SettingsGlyphs.Row(SettingsTab.General, "fpsOverlay"), DeveloperMode.FpsOverlay.Value, isEnabled: dev,
                on => DeveloperMode.SetFpsOverlay(settings, on)),

            SwitchRow(Strings.Settings.Diag.DealerArchive, Strings.Settings.Diag.DealerArchiveSub,
                SettingsGlyphs.Row(SettingsTab.General, "dealerArchive"),
                settings?.Get(WaveeSettings.DealerArchiveEnabled) ?? false, isEnabled: true,
                on =>
                {
                    settings?.Set(WaveeSettings.DealerArchiveEnabled, on);
                    // The archive keeps the directory Program.cs gave it, so the toggle only has to say on/off —
                    // and it applies to the LIVE dealer connection, which is the whole point of it being a setting.
                    DealerArchive.Instance.SetEnabled(on);
                }),
        };

        // Developer surface only: walk the whole update state machine (available → downloading → installing →
        // completed → failed) with no network and no package deployment, so every toast, notification-centre row and
        // About-tab state can be SEEN rather than reasoned about. Composed away entirely when developer mode is off.
        if (dev)
            rows.Add(SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(Strings.Settings.Diag.SimulateUpdate),
                Description = Loc.Get(Strings.Settings.Diag.SimulateUpdateSub),
                HeaderIcon = SettingsGlyphs.Row(SettingsTab.General, "simulateUpdate"),
                Content = Button.Standard(Loc.Get(Strings.Settings.Diag.SimulateUpdateButton),
                    () => { if (nc is not null) FakeAppUpdateService.Start(nc); }),
                IsEnabled = nc is not null,
            }));

        return rows.ToArray();
    }

    /// <summary>One developer on/off row. The toggle is fed a FRESH signal seeded from the current value (the
    /// SettingsPage appearance-toggle pattern): the truth lives in settings, never in a mirror signal that could drift
    /// from it. <c>Bump()</c> after every write — <c>DeveloperMode.Enabled</c>/<c>FpsOverlay</c> being signals means a
    /// flip already re-renders this tab on its own, but Dealer archive is a plain settings write with no signal behind
    /// it, and the explicit bump is what makes the FPS Overlay row's <c>isEnabled: dev</c> repaint deterministic
    /// regardless of which switch changed (the old DiagnosticsPanel.SwitchRow's <c>_diagVersion</c> bump, ported).</summary>
    Element SwitchRow(string labelKey, string subKey, string icon, bool value, bool isEnabled, Action<bool> onSet)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = Loc.Get(labelKey),
            Description = Loc.Get(subKey),
            HeaderIcon = icon,
            IsEnabled = isEnabled,
            Content = ToggleSwitch.Create(new Signal<bool>(value), onChange: _ =>
            {
                onSet(!value);
                Bump();
            }, style: SettingsCard.CompactToggleStyle()),
        });

    /// <summary>Settings › General › Graphics — the render-GPU picker. Its own <see cref="Component"/> (moved here from
    /// the old About tab) so its hooks (<c>UseEffect</c> to seed the selection, <c>UseContext</c> for the settings
    /// seam) live on a child whose lifetime IS the tab, never in <c>GeneralTab</c> itself, which the tab switch calls
    /// conditionally. The adapter list is enumerated ONCE at mount (cold DXGI walk) and frozen. Selecting an adapter
    /// persists the LUID + name and live-applies via the engine's device-reset path
    /// (<see cref="GpuAdapterInfo.RequestAdapterSwitch"/>) — a brief flicker, no restart.</summary>
    sealed class GpuPickerCard : Component
    {
        readonly Signal<int> _selected = new(0);
        // Frozen at mount: one factory run per Embed.Comp, so this cold enumeration happens once, not per re-render.
        readonly IReadOnlyList<GpuAdapterDesc> _adapters = GpuAdapterInfo.EnumerateAdapters();

        public override Element Render()
        {
            var svc = UseContext(Services.Slot);

            // Seed the selection ONCE from the persisted preference: LUID first (the fast, exact match), then the
            // durable name (LUIDs are not stable across reboots), else 0 = Automatic. Runs after mount so the
            // ComboBox shows the honored choice without a write.
            UseEffect(() =>
            {
                if (svc is null) return;
                long luid = svc.Settings.Get(WaveeSettings.PreferredGpuLuid);
                string name = svc.Settings.Get(WaveeSettings.PreferredGpuName);
                int idx = 0;
                if (luid != 0L || name.Length > 0)
                {
                    for (int k = 0; k < _adapters.Count; k++)
                    {
                        if (luid != 0L && _adapters[k].Luid == luid) { idx = k + 1; break; }
                        if (idx == 0 && name.Length > 0 && string.Equals(_adapters[k].Name, name, StringComparison.Ordinal)) idx = k + 1;
                    }
                }
                _selected.Value = idx;
            }, DepKey.Empty);

            string[] labels = new string[_adapters.Count + 1];
            labels[0] = Loc.Get(Strings.Settings.Gpu.Automatic);
            for (int k = 0; k < _adapters.Count; k++)
            {
                var a = _adapters[k];
                labels[k + 1] = a.IsCurrent ? Strings.Settings.Gpu.InUse(a.Name) : a.Name;
            }

            return SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(Strings.Settings.Gpu.Label),
                Description = Loc.Get(Strings.Settings.Gpu.RestartSub),
                HeaderIcon = SettingsGlyphs.Row(SettingsTab.General, "preferredGpu"),
                Content = ComboBox.Create(labels, _selected, width: 300f, isEnabled: svc is not null,
                    onChange: i => Pick(svc, i)),
            });
        }

        void Pick(Services? svc, int i)
        {
            // Index 0 (or any out-of-range) = Automatic → clear the preference and pass LUID 0 (the engine's
            // HIGH_PERFORMANCE walk). Otherwise persist the chosen adapter's LUID + name and apply it.
            if (i <= 0 || i > _adapters.Count)
            {
                svc?.Settings.Set(WaveeSettings.PreferredGpuLuid, 0L);
                svc?.Settings.Set(WaveeSettings.PreferredGpuName, "");
                _selected.Value = 0;
                GpuAdapterInfo.RequestAdapterSwitch(0L);
                return;
            }
            var a = _adapters[i - 1];
            svc?.Settings.Set(WaveeSettings.PreferredGpuLuid, a.Luid);
            svc?.Settings.Set(WaveeSettings.PreferredGpuName, a.Name);
            _selected.Value = i;
            GpuAdapterInfo.RequestAdapterSwitch(a.Luid);
        }
    }
}
