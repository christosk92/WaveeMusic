using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Core;
using Wavee.Core.Home;

namespace Wavee;

// The versioned home-layout document: wire DTOs + the AOT source-generated context. Same contract as
// sidebar-layout.json (SidebarLayoutDoc): no polymorphic JSON, unknown members/kinds survive a round trip,
// and nothing here throws on an unknown or missing member.

public sealed class HomeLayoutDocDto
{
    public int Version { get; set; }
    public long UpdatedAtMs { get; set; }
    public string? AppVersion { get; set; }
    public HomeModuleDto[]? Modules { get; set; }
    /// <summary>Ordered dynamic section-deck ids. v1 UI does not edit this; the field is on the schema so a later
    /// customizer can reorder the deck without a document migration.</summary>
    public string[]? DeckOrder { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HomeModuleDto
{
    public string? Kind { get; set; }
    public bool? Hidden { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(HomeLayoutDocDto))]
public sealed partial class HomeLayoutJsonCtx : JsonSerializerContext { }

/// <summary>Opaque forward-compatibility carry produced by <see cref="HomeLayoutWire.Read"/>. Unknown kind
/// strings and unknown members on known modules are re-emitted on the next save.</summary>
public sealed class HomeLayoutWireCarry
{
    public static readonly HomeLayoutWireCarry Empty = new();

    internal readonly Dictionary<string, HomeModuleDto> Raw = new(StringComparer.Ordinal);
    internal readonly List<KeyValuePair<int, HomeModuleDto>> Unknown = new();
    internal Dictionary<string, JsonElement>? DocExtra;

    public int UnknownModuleCount => Unknown.Count;
    public bool IsEmpty => Raw.Count == 0 && Unknown.Count == 0 && DocExtra is null;

    public void CaptureDoc(HomeLayoutDocDto? doc) => DocExtra = doc?.Extra;

    public void ReattachDoc(HomeLayoutDocDto? doc)
    {
        if (doc is null) return;
        doc.Extra ??= DocExtra;
    }
}

public readonly record struct HomeLayoutRead(HomeLayoutDoc Layout, HomeLayoutWireCarry Carry);

public static class HomeLayoutWire
{
    public static HomeLayoutRead Read(HomeLayoutDocDto? dto)
    {
        var carry = new HomeLayoutWireCarry();
        if (dto is null) return new HomeLayoutRead(HomeLayoutDoc.Default, carry);
        carry.DocExtra = dto.Extra;

        var modules = new List<HomeModuleSpec>(dto.Modules?.Length ?? 0);
        var seen = new HashSet<HomeGroupKind>();
        var raw = dto.Modules;
        if (raw is not null)
            for (int i = 0; i < raw.Length; i++)
            {
                var m = raw[i];
                if (m is null) continue;
                if (!HomeLayoutModules.TryParseKind(m.Kind, out var kind) || !HomeLayoutModules.IsFixedLanding(kind))
                {
                    carry.Unknown.Add(new KeyValuePair<int, HomeModuleDto>(i, m));
                    continue;
                }
                if (!seen.Add(kind)) continue;
                if (!string.IsNullOrEmpty(m.Kind) && !carry.Raw.ContainsKey(m.Kind))
                    carry.Raw[m.Kind] = m;
                modules.Add(new HomeModuleSpec(kind, m.Hidden ?? false));
            }

        // A kind this build knows that the file never mentioned is APPENDED visible — a new module must appear,
        // not vanish (preserve-don't-destroy).
        var defaults = HomeLayoutModules.DefaultOrder;
        for (int i = 0; i < defaults.Length; i++)
            if (seen.Add(defaults[i])) modules.Add(new HomeModuleSpec(defaults[i]));

        IReadOnlyList<string>? deck = null;
        if (dto.DeckOrder is { Length: > 0 } ids)
        {
            var list = new List<string>(ids.Length);
            var deckSeen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] is { Length: > 0 } id && deckSeen.Add(id)) list.Add(id);
            if (list.Count > 0) deck = list;
        }

        return new HomeLayoutRead(new HomeLayoutDoc(modules, deck), carry);
    }

    public static HomeLayoutDocDto Write(HomeLayoutDoc layout, HomeLayoutWireCarry? carry)
    {
        carry ??= HomeLayoutWireCarry.Empty;
        var list = new List<HomeModuleDto>(layout.Modules.Count + carry.Unknown.Count);
        for (int i = 0; i < layout.Modules.Count; i++)
        {
            var spec = layout.Modules[i];
            string name = HomeLayoutModules.KindName(spec.Kind);
            var dto = new HomeModuleDto
            {
                Kind = name,
                Hidden = spec.Hidden ? true : null,
            };
            if (carry.Raw.TryGetValue(name, out var raw)) dto.Extra ??= raw.Extra;
            list.Add(dto);
        }

        if (carry.Unknown.Count > 0)
        {
            var pending = new List<KeyValuePair<int, HomeModuleDto>>(carry.Unknown);
            pending.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < pending.Count; i++)
            {
                int at = pending[i].Key;
                if (at < 0) at = 0;
                if (at > list.Count) at = list.Count;
                list.Insert(at, pending[i].Value);
            }
        }

        string[]? deck = null;
        if (layout.DeckOrder is { Count: > 0 } ids)
        {
            deck = new string[ids.Count];
            for (int i = 0; i < deck.Length; i++) deck[i] = ids[i];
        }

        return new HomeLayoutDocDto
        {
            Version = HomeLayoutStore.CurrentVersion,
            Modules = list.ToArray(),
            DeckOrder = deck,
            Extra = carry.DocExtra,
        };
    }
}
