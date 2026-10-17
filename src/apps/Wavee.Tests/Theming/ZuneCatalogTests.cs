// ── Wavee.Tests/Theming/ZuneCatalogTests.cs — the Zune composition: manifest, resolution, options, capabilities, registry (P5 Z0) ────
//
// Zune is Developer-only: it validates and installs next to Classic, resolves only with Developer mode on (Classic otherwise, the stored
// id kept), stores its user options as `theme.zune.structure.<id>`, keeps its five structure parameters out of Settings and storage
// (each with a WVTH011 probe-matrix line of at least two valid values), and offers Zune's capabilities. Its registry: Zune's own
// (`ZuneStructure.RegisterOwn`) holds the Common blocks and Nothing for the drawer and the masthead; the installed composition holds a
// picture for EVERY key and style through the Classic stand-in, so a Developer who picks Zune never gets a blank surface.
// `Themes` is process state, so the class is in the platform collection and resets it around every fact.

using System.Reflection;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.Common;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ZuneCatalogTests : IDisposable
{
    readonly MemoryAppSettings _settings = new();

    public ZuneCatalogTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    static readonly string[] UserOptions = ["background", "panoramaFit", "bandPins"];
    static readonly string[] Parameters = ["collapseBand", "pivotOverflow", "seekTimes", "panelMode", "maxDockedPanes"];

    // ── the manifest ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_validates()
        => Assert.Empty(ZuneTheme.Create().Validate());

    [Fact]
    public void The_manifest_is_zune_structure_by_zune_style_and_developer_only()
    {
        var m = ZuneTheme.Create().Manifest;
        Assert.Equal("zune", m.Id);
        Assert.Equal("Zune.Structure", m.StructureId);
        Assert.Equal("Zune.Style", m.StyleId);
        Assert.Equal(ZuneStructure.LayerId, m.StructureId);
        Assert.Equal(ZuneStyle.LayerId, m.StyleId);
        Assert.Equal(ThemeAvailability.Developer, m.Availability);
        Assert.Equal("theme.zune.name", m.DisplayNameKey);
        Assert.Equal("theme.zune.description", m.DescriptionKey);
        Assert.Null(m.InheritsFrom);
    }

    // ── resolution ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_resolves_only_with_developer_mode()
    {
        ThemeHost.Install(ClassicTheme.Create(), ZuneTheme.Create());
        Assert.Equal(ClassicTheme.Id, ThemeCatalog.Resolve("zune", developerMode: false).Manifest.Id);
        Assert.Equal(ZuneTheme.Id, ThemeCatalog.Resolve("zune", developerMode: true).Manifest.Id);
    }

    [Fact]
    public void A_stored_zune_falls_back_to_classic_with_developer_mode_off_and_is_kept()
    {
        Platform.Developer.Enabled.Value = false;
        _settings.Set(new SettingKey<string>("theme.id", ClassicTheme.Id), ZuneTheme.Id);
        ThemeHost.Install(ClassicTheme.Create(), ZuneTheme.Create());
        Assert.Equal(ClassicTheme.Id, ThemeHost.Current.Peek().Manifest.Id);
        Assert.Equal(ZuneTheme.Id, _settings.Get(new SettingKey<string>("theme.id", "")));
    }

    [Fact]
    public void Installing_zune_next_to_classic_keeps_classic_first_and_both_installed()
    {
        ThemeHost.Install(ClassicTheme.Create(), ZuneTheme.Create());
        Assert.Equal(new[] { ClassicTheme.Id, ZuneTheme.Id }, ThemeCatalog.Installed.Select(m => m.Id));
    }

    // ── options ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_user_options_are_background_panorama_fit_and_band_pins_stored_under_theme_zune_structure()
    {
        var theme = ZuneTheme.Create();
        var structure = theme.AllOptions.Where(lo => lo.Layer == OptionLayer.Structure).ToList();
        Assert.Equal(UserOptions.Concat(Parameters), structure.Select(lo => lo.Option.Id));

        var exposed = structure.Where(theme.IsExposed).Select(lo => lo.QualifiedId).ToArray();
        Assert.Equal(new[] { "structure.background", "structure.panoramaFit", "structure.bandPins" }, exposed);
        Assert.Equal(ZuneStructure.BackgroundQualifiedId, exposed[0]);
        Assert.Equal(ZuneStructure.PanoramaFitQualifiedId, exposed[1]);
        Assert.Equal(ZuneStructure.BandPinsQualifiedId, exposed[2]);

        foreach (string id in UserOptions)
        {
            var lo = theme.Find(id)!.Value;
            Assert.True(theme.IsUserOption(lo), id + " is a user option");
            Assert.Equal("theme.zune.structure." + id, ThemeOptionStore.SettingName(theme.Manifest.Id, lo.Layer, lo.Option.Id));
        }
    }

    [Fact]
    public void The_structure_parameters_are_hidden_never_stored_and_never_shown()
    {
        var theme = ZuneTheme.Create();
        foreach (string id in Parameters)
        {
            var lo = theme.Find(id)!.Value;
            Assert.Equal(OptionLayer.Structure, lo.Layer);
            Assert.Equal(OptionVisibility.Hidden, lo.Option.Visibility);
            Assert.False(theme.IsExposed(lo), id + " is not shown");
            Assert.False(theme.IsUserOption(lo), id + " is not stored");
        }
    }

    [Fact]
    public void The_option_defaults_are_the_plans()
    {
        Assert.Equal(new ZuneParameters(ZuneBackground.ArtworkWash, PanoramaFit.Bleed, BandPins: true, CollapseBand: true,
            PivotOverflow.EdgeFadeScroll, SeekTimes.InFace, ZunePanelMode.InlineWhenRoom, MaxDockedPanes: 3), ZuneParameters.Default);
        Assert.Equal(ApplyMode.Live, ZuneStructure.Background.Apply);
        Assert.Equal(ApplyMode.Transaction, ZuneStructure.PanoramaFitOption.Apply);
        Assert.Equal(ApplyMode.Live, ZuneStructure.BandPins.Apply);

        // The composition lays the parameters' defaults over the hidden options, and the policies read them.
        var values = ZuneTheme.Create().Build(lo => lo.Option.Id switch
        {
            "background" => ZuneBackground.Mix,
            _ => null,
        });
        var p = ZuneParameters.From(values.Structure);
        Assert.Equal(ZuneBackground.Mix, p.Background);
        Assert.True(p.CollapseBand);
        Assert.Equal(3, p.MaxDockedPanes);
    }

    /// <summary>WVTH011: every hidden structure option has a probe-matrix line with at least two distinct values, each a valid value of the
    /// option, the default among them.</summary>
    [Fact]
    public void Every_structure_parameter_has_a_probe_matrix_line_of_valid_values()
    {
        string? path = FindMatrix();
        Assert.True(path is not null, "analyzer-input/WVTH011.matrix.txt was not found above the test binary");
        var matrix = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string raw in File.ReadAllLines(path!))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            matrix[line[..eq].Trim()] = line[(eq + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        foreach (var option in new ZuneStructure().Options.All.Where(o => o.Visibility == OptionVisibility.Hidden))
        {
            Assert.True(matrix.TryGetValue(option.Id, out var found), option.Id + " has no WVTH011 matrix line");
            string[] values = found!;
            Assert.True(values.Distinct(StringComparer.Ordinal).Count() >= 2, option.Id + " is exercised with fewer than two values");
            string def;
            switch (option)
            {
                case ToggleOption t:
                    Assert.All(values, v => Assert.True(bool.TryParse(v, out _), option.Id + ": '" + v + "' is not a bool"));
                    def = t.Default ? "true" : "false";
                    Assert.Contains(values, v => string.Equals(v, def, StringComparison.OrdinalIgnoreCase));
                    break;
                case SliderOption s:
                    Assert.All(values, v => Assert.True(float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)
                        && f >= s.Min && f <= s.Max, option.Id + ": '" + v + "' is outside " + s.Min + "-" + s.Max));
                    Assert.Contains(values, v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) == s.Default);
                    break;
                case SelectOption<PivotOverflow> o: AssertEnumValues(o.Id, values, o.Default); break;
                case SelectOption<SeekTimes> o: AssertEnumValues(o.Id, values, o.Default); break;
                case SelectOption<ZunePanelMode> o: AssertEnumValues(o.Id, values, o.Default); break;
                default: Assert.Fail(option.Id + " is a " + option.GetType().Name + " the test does not know"); break;
            }
        }
        Assert.Equal(Parameters.Order(StringComparer.Ordinal), new ZuneStructure().Options.All.Where(o => o.Visibility == OptionVisibility.Hidden)
            .Select(o => o.Id).Order(StringComparer.Ordinal));
    }

    static void AssertEnumValues<TEnum>(string id, string[] values, TEnum def) where TEnum : struct, Enum
    {
        Assert.All(values, v => Assert.True(Enum.TryParse<TEnum>(v, ignoreCase: false, out var e) && Enum.IsDefined(e), id + ": '" + v + "' is not a " + typeof(TEnum).Name));
        Assert.Contains(def.ToString(), values);
    }

    static string? FindMatrix()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "analyzer-input", "WVTH011.matrix.txt");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "src", "apps", "Wavee", "analyzer-input", "WVTH011.matrix.txt");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ── capabilities and policies ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_capabilities_are_zunes()
    {
        var caps = ZuneTheme.Create().Capabilities(ThemeOptionValues.Empty);
        Assert.Equal(new ShellCapabilities(Tabs: false, NavTree: false, FriendsPanel: true, ForwardButton: false, HistoryMenu: true, MaxBandPins: 6), caps);
        Assert.Same(ZunePolicies.Capabilities, caps);
    }

    [Fact]
    public void The_policies_validate_and_carry_zunes_tiers()
    {
        var policies = ZuneTheme.Create().Policies(ThemeOptionValues.Empty);
        Assert.Empty(policies.Validate());
        Assert.Same(ZuneTiers.Policy, policies.Tiers);
    }

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static List<(string Name, string Id, Type Arg)> Declared(Type keys)
    {
        var found = new List<(string, string, Type)>();
        foreach (FieldInfo f in keys.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object key = f.GetValue(null)!;
            found.Add((f.Name, (string)f.FieldType.GetProperty("Id")!.GetValue(key)!, f.FieldType.GetGenericArguments()[0]));
        }
        return found;
    }

    [Fact]
    public void Zunes_own_registry_holds_the_common_blocks_and_nothing_for_the_drawer_and_the_masthead()
    {
        var builder = new ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var own = builder.Build();

        Assert.Equal(CommonNothing.IdFor(ViewKeys.ShellNarrowDrawer.Id), own.TemplateIdOf(ViewKeys.ShellNarrowDrawer.Id));
        Assert.Equal(CommonNothing.IdFor(ViewKeys.ShellMasthead.Id), own.TemplateIdOf(ViewKeys.ShellMasthead.Id));
        Assert.Equal("common.history", own.TemplateIdOf(ViewKeys.HistoryPage.Id));
        foreach (string id in own.ViewKeyIds)
        {
            string templateId = own.TemplateIdOf(id)!;
            Assert.True(templateId.StartsWith("zune.", StringComparison.Ordinal) || templateId.StartsWith("common.", StringComparison.Ordinal),
                id + " -> " + templateId + " is neither Zune's own (zune.*) nor a Common block's (common.*)");
        }

        // `new ZuneStructure()` registers exactly that.
        Assert.False(new ZuneStructure().UsesStandIn);
        var plain = new ViewRegistryBuilder();
        new ZuneStructure().Register(plain);
        Assert.Equal(own.ViewKeyIds.Order(StringComparer.Ordinal), plain.Build().ViewKeyIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_installed_composition_has_a_picture_for_every_view_and_style_key()
    {
        var theme = ZuneTheme.Create();
        Assert.True(((ZuneStructure)theme.Structure).UsesStandIn, "until Z9, Classic's views stand in for the keys Zune has not drawn");
        var registry = theme.Registry;

        var views = Declared(typeof(ViewKeys));
        Assert.True(views.Count > 150, $"only {views.Count} view keys were found: the reflection missed the contract");
        var missing = views.Where(v => registry.TemplateIdOf(v.Id) is null).Select(v => "ViewKeys." + v.Name).ToList();
        Assert.True(missing.Count == 0, "Zune renders nothing for: " + string.Join(", ", missing));

        MethodInfo tryGet = typeof(ViewRegistry).GetMethod(nameof(ViewRegistry.TryGetStyle))!;
        var missingStyles = new List<string>();
        foreach (FieldInfo f in typeof(StyleKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object?[] args = [f.GetValue(null), null];
            bool found = (bool)tryGet.MakeGenericMethod(f.FieldType.GetGenericArguments()[0]).Invoke(registry, args)!;
            if (!found || args[1] is null) missingStyles.Add("StyleKeys." + f.Name);
        }
        Assert.True(missingStyles.Count == 0, "Zune has no style for: " + string.Join(", ", missingStyles));
    }
}
