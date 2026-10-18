// ── Wavee.Tests/Theming/ZuneRegistryCompletenessTests.cs — every key the contract declares has a picture in Zune (P5 Z9) ───────────────
//
// The twin of Classic's RegistryCompletenessTests. The contract is the static fields of `ViewKeys` and `StyleKeys`, read by reflection, so a key
// a PR adds is covered without touching this file. The INSTALLED Zune (`ZuneTheme.Create().Registry`: Zune's own views over Classic's stand-in,
// `ViewRegistryBuilder.AddMissing`) must hold a picture for every view key and a style for every style key, and every template id is Zune's
// own (`zune.*`), a Common block's (`common.*`) or one of the documented stand-ins below (Classic's, `classic.*`). Zune's OWN registry
// (`new ZuneStructure()`) may leave a key to the stand-in only when it is listed in `DeliberateStandIns` WITH its reason: a new key, or a Zune
// family that stops registering one, fails here instead of silently drawing Classic's picture.
//
// `Themes` is process state: the test class is in the platform collection and resets it around every fact.

using System.Reflection;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class ZuneRegistryCompletenessTests : IDisposable
{
    public ZuneRegistryCompletenessTests() => ThemeHost.Reset();

    public void Dispose() => ThemeHost.Reset();

    /// <summary>The view keys Zune deliberately leaves to Classic's picture (key id -> why). Every other key is Zune's own (`zune.*`) or a Common block's.</summary>
    static readonly Dictionary<string, string> DeliberateStandIns = new(StringComparer.Ordinal)
    {
    };

    static List<(string Name, string Id, Type Arg, object Key)> Declared(Type keys)
    {
        var found = new List<(string, string, Type, object)>();
        foreach (FieldInfo f in keys.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object key = f.GetValue(null)!;
            found.Add((f.Name, (string)f.FieldType.GetProperty("Id")!.GetValue(key)!, f.FieldType.GetGenericArguments()[0], key));
        }
        return found;
    }

    [Fact]
    public void Zune_validates_and_is_developer_only()
    {
        var theme = ZuneTheme.Create();
        Assert.Empty(theme.Validate());
        Assert.Equal(ThemeAvailability.Developer, theme.Manifest.Availability);
        Assert.Equal(ThemeAvailability.Developer, ZuneTheme.Create().Manifest.Availability);
    }

    [Fact]
    public void Every_view_key_has_a_zune_common_or_documented_stand_in_template()
    {
        var registry = ZuneTheme.Create().Registry;
        var declared = Declared(typeof(ViewKeys));
        Assert.True(declared.Count > 150, $"only {declared.Count} view keys were found: the reflection missed the contract");

        var missing = new List<string>();
        var foreign = new List<string>();
        foreach (var (name, id, _, _) in declared)
        {
            string? templateId = registry.TemplateIdOf(id);
            if (templateId is null) { missing.Add($"ViewKeys.{name} ({id})"); continue; }
            bool own = templateId.StartsWith("zune.", StringComparison.Ordinal) || templateId.StartsWith("common.", StringComparison.Ordinal);
            if (!own && !DeliberateStandIns.ContainsKey(id)) foreign.Add($"ViewKeys.{name} ({id}) -> {templateId}");
        }
        Assert.True(missing.Count == 0, "Zune renders nothing for: " + string.Join(", ", missing));
        Assert.True(foreign.Count == 0,
            "these keys are drawn by the Classic stand-in and are not listed in DeliberateStandIns (draw them in Zune, or list them with a reason): "
            + string.Join(", ", foreign));

        // A listed stand-in that Zune now draws is a stale line.
        var stale = DeliberateStandIns.Keys.Where(id => registry.TemplateIdOf(id) is { } t && (t.StartsWith("zune.", StringComparison.Ordinal) || t.StartsWith("common.", StringComparison.Ordinal))).ToList();
        Assert.True(stale.Count == 0, "DeliberateStandIns lists keys Zune draws itself: " + string.Join(", ", stale));
    }

    [Fact]
    public void Zunes_own_registry_registers_only_declared_keys_with_zune_or_common_ids()
    {
        var builder = new ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var own = builder.Build();

        var declared = Declared(typeof(ViewKeys)).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var undeclared = own.ViewKeyIds.Where(id => !declared.Contains(id)).ToList();
        Assert.True(undeclared.Count == 0, "Zune registers ids the contract does not declare: " + string.Join(", ", undeclared));
        foreach (string id in own.ViewKeyIds)
        {
            string templateId = own.TemplateIdOf(id)!;
            Assert.True(templateId.StartsWith("zune.", StringComparison.Ordinal) || templateId.StartsWith("common.", StringComparison.Ordinal),
                id + " -> " + templateId + " is neither Zune's own (zune.*) nor a Common block's (common.*)");
        }

        // The keys Zune's own registry leaves out are exactly the documented stand-ins: nothing is inherited by accident.
        var left = declared.Where(id => own.TemplateIdOf(id) is null).Order(StringComparer.Ordinal).ToList();
        Assert.True(left.SequenceEqual(DeliberateStandIns.Keys.Order(StringComparer.Ordinal)),
            "keys Zune leaves to the stand-in: [" + string.Join(", ", left) + "]; documented: [" + string.Join(", ", DeliberateStandIns.Keys.Order(StringComparer.Ordinal)) + "]");
    }

    [Fact]
    public void Every_style_key_has_a_style_in_the_installed_zune()
    {
        var registry = ZuneTheme.Create().Registry;
        var declared = Declared(typeof(StyleKeys));
        Assert.NotEmpty(declared);
        MethodInfo tryGet = typeof(ViewRegistry).GetMethod(nameof(ViewRegistry.TryGetStyle))!;

        var missing = new List<string>();
        foreach (var (name, id, arg, key) in declared)
        {
            object?[] args = [key, null];
            bool found = (bool)tryGet.MakeGenericMethod(arg).Invoke(registry, args)!;
            if (!found || args[1] is null) missing.Add($"StyleKeys.{name} ({id})");
        }
        Assert.True(missing.Count == 0, "Zune has no style for: " + string.Join(", ", missing));
        Assert.Equal(declared.Count, declared.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
