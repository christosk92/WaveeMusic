// ── Wavee.Tests/Theming/RegistryCompletenessTests.cs — every published key has a picture in Classic (themes plan 2.4, T13) ──────────
//
// The contract is the static fields of `ViewKeys` and `StyleKeys` (`Theming\Contracts\ViewKeys*.cs`, `StyleKeys*.cs`): read by reflection, so a key
// a PR adds is covered without touching this file. Classic must register a template for every view key and a style for every style key, either
// itself (`classic.*` template ids) or through `CommonBlocks.Register` (`common.*`), and nothing the contract does not declare. A slot whose key is
// missing renders nothing in Release, so a gap here is a blank surface in the app.

using System.Reflection;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class RegistryCompletenessTests
{
    /// <summary>Every declared key of <paramref name="keys"/>: the field, its id and the key's type argument.</summary>
    static List<(FieldInfo Field, string Id, Type Arg)> Declared(Type keys, Type openKey)
    {
        var found = new List<(FieldInfo, string, Type)>();
        foreach (FieldInfo f in keys.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Assert.True(f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == openKey,
                $"{keys.Name}.{f.Name} is a {f.FieldType.Name}, not a {openKey.Name}: the class holds keys only");
            object key = f.GetValue(null)!;
            string id = (string)f.FieldType.GetProperty("Id")!.GetValue(key)!;
            found.Add((f, id, f.FieldType.GetGenericArguments()[0]));
        }
        return found;
    }

    [Fact]
    public void Classic_validates()
        => Assert.Empty(ClassicTheme.Create().Validate());

    [Fact]
    public void Every_view_key_has_a_classic_or_common_template_and_nothing_else_is_registered()
    {
        var registry = ClassicTheme.Create().Registry;
        var declared = Declared(typeof(ViewKeys), typeof(ViewKey<>));
        Assert.True(declared.Count > 150, $"only {declared.Count} view keys were found: the reflection missed the contract");

        var missing = new List<string>();
        var foreign = new List<string>();
        foreach (var (field, id, _) in declared)
        {
            string? templateId = registry.TemplateIdOf(id);
            if (templateId is null) missing.Add($"ViewKeys.{field.Name} ({id})");
            else if (!templateId.StartsWith("classic.", StringComparison.Ordinal) && !templateId.StartsWith("common.", StringComparison.Ordinal))
                foreign.Add($"ViewKeys.{field.Name} ({id}) -> {templateId}");
        }
        Assert.True(missing.Count == 0, "Classic registers no template for: " + string.Join(", ", missing));
        Assert.True(foreign.Count == 0, "a template id is neither Classic's own (classic.*) nor a Common block's (common.*): " + string.Join(", ", foreign));

        var ids = declared.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ids.Count, declared.Count);
        var undeclared = registry.ViewKeyIds.Where(id => !ids.Contains(id)).ToList();
        Assert.True(undeclared.Count == 0, "Classic registers ids the contract does not declare: " + string.Join(", ", undeclared));
        Assert.Equal(declared.Count, registry.ViewCount);
    }

    [Fact]
    public void Every_style_key_has_a_classic_style()
    {
        var registry = ClassicTheme.Create().Registry;
        var declared = Declared(typeof(StyleKeys), typeof(StyleKey<>));
        Assert.NotEmpty(declared);
        MethodInfo tryGet = typeof(ViewRegistry).GetMethod(nameof(ViewRegistry.TryGetStyle))!;

        var missing = new List<string>();
        foreach (var (field, id, arg) in declared)
        {
            object?[] args = [field.GetValue(null), null];
            bool found = (bool)tryGet.MakeGenericMethod(arg).Invoke(registry, args)!;
            if (!found || args[1] is null) missing.Add($"StyleKeys.{field.Name} ({id})");
        }
        Assert.True(missing.Count == 0, "Classic registers no style for: " + string.Join(", ", missing));
        Assert.Equal(declared.Count, declared.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
