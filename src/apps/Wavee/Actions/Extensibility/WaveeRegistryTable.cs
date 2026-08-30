using System;
using System.Collections.Generic;

namespace Wavee;

// The extension-registry BOOKKEEPING core (M1, plan REVISION 2 item 2 / platform doc "Action registry"): namespaced-key
// validation, the first-wins duplicate policy, ordered enumeration for the customizer's action picker, and the rejection
// diagnostics. Deliberately a standalone generic table rather than logic inside WaveeExtensionRegistry, because the
// registry itself is engine-bound (WaveeActionDescriptor carries ActionServices-typed delegates) while THIS is
// System-only — so src/apps/Wavee.Tests source-includes it and drives the real registration policy instead of a copy of
// it. WaveeExtensionRegistry is a thin shell over two instances (actions + data sources).
//
// THREADING: UI thread only, unsynchronized. Registration happens once at startup (BuiltInExtensionTable.RegisterAll,
// and in M3 the sandboxed host's manifest walk, which marshals to the UI thread before registering); lookup is a
// render-time read. There is no off-thread producer and no lock — the same discipline as SidebarPinStore.

/// <summary>Why a registration was accepted or refused. <see cref="Registered"/> is the only success.</summary>
public enum WaveeRegisterOutcome : byte
{
    Registered = 0,
    /// <summary>The descriptor/source itself was null, or carried no key at all.</summary>
    RejectedNull = 1,
    /// <summary>The key is not a valid namespaced key (see <see cref="WaveeExtensionKey.IsValid"/>).</summary>
    RejectedInvalidKey = 2,
    /// <summary>Something is already registered under this key. FIRST WINS — the earlier registration is kept
    /// untouched and this one is dropped, so a broken/malicious extension can never shadow a first-party action.</summary>
    RejectedDuplicate = 3,
}

/// <summary>One rejected (or accepted, for the audit trail) registration. Surfaced by
/// <c>WaveeExtensionRegistry.Diagnostics</c> — in M1 for the devtools/extension page, in M3 for the failure matrix's
/// structured log. Never a toast: a registration problem is a developer/publisher fact, not a user interruption.</summary>
public readonly record struct WaveeRegistryDiagnostic(string Key, WaveeRegisterOutcome Outcome, string Detail);

/// <summary>
/// The namespaced-key vocabulary shared by actions, data sources and (M3) every other contribution kind:
/// <c>publisher.contribution[.sub]</c> — <c>wavee.play</c>, <c>wavee.artist.topTracks</c>,
/// <c>publisher.extension.refresh</c>. One place, so a key a descriptor declares and a key a stored
/// <c>SidebarActionBinding</c> composes can never disagree.
/// </summary>
public static class WaveeExtensionKey
{
    public const char Separator = '.';

    /// <summary>The trusted first-party publisher segment. First-party contributions are literally the extension
    /// <c>"wavee"</c> (REVISION 2's forward-compatibility guardrail) — there is no privileged non-extension path.</summary>
    public const string FirstPartyPublisher = "wavee";

    /// <summary>Upper bound on a whole key. Generous but bounded: a key is persisted inside
    /// <c>sidebar-layout.json</c>, so an unbounded one is a document-size hazard.</summary>
    public const int MaxLength = 128;

    /// <summary>A valid key is 2+ <see cref="Separator"/>-separated segments, each starting with an ASCII letter and
    /// continuing with ASCII letters/digits/<c>-</c>/<c>_</c>. camelCase is allowed and used (<c>wavee.playNext</c>);
    /// what is refused is an empty segment, a leading/trailing/doubled dot, whitespace, and a single-segment key (which
    /// would let a contribution squat the publisher namespace itself).</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key!.Length > MaxLength) return false;
        int segments = 0;
        int i = 0;
        while (i < key.Length)
        {
            int start = i;
            while (i < key.Length && key[i] != Separator) i++;
            if (i == start) return false;                                     // empty segment ⇒ "", ".x", "a..b", "a."
            if (!IsSegmentStart(key[start])) return false;
            for (int k = start + 1; k < i; k++)
                if (!IsSegmentBody(key[k])) return false;
            segments++;
            if (i < key.Length) i++;                                          // step over the separator
        }
        return segments >= 2;
    }

    static bool IsSegmentStart(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
    static bool IsSegmentBody(char c) =>
        IsSegmentStart(c) || (uint)(c - '0') <= 9 || c == '-' || c == '_';

    /// <summary>The registry key a stored binding resolves to. A binding carries the publisher (<c>ProviderId</c>) and
    /// the contribution (<c>ActionId</c>) separately; older/hand-edited documents may already carry the fully-qualified
    /// form in <c>ActionId</c>, so an <c>ActionId</c> that ALREADY starts with <c>ProviderId + '.'</c> is taken as-is
    /// rather than double-prefixed. Returns "" when either half is missing — an unresolvable key, which the registry
    /// then reports as a missing action rather than silently matching something else.</summary>
    public static string Compose(string? providerId, string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return "";
        if (string.IsNullOrEmpty(providerId)) return actionId!;
        if (actionId!.Length > providerId!.Length
            && actionId[providerId.Length] == Separator
            && actionId.StartsWith(providerId, StringComparison.Ordinal)) return actionId;
        return providerId + Separator + actionId;
    }

    /// <summary>The publisher segment of a key ("" when there is none).</summary>
    public static string PublisherOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        int dot = key!.IndexOf(Separator);
        return dot <= 0 ? "" : key.Substring(0, dot);
    }

    public static bool IsFirstParty(string? key) =>
        string.Equals(PublisherOf(key), FirstPartyPublisher, StringComparison.Ordinal);
}

/// <summary>
/// An append-only, insertion-ordered, key-unique table of contributions of one kind. <see cref="Items"/> preserves
/// registration order (the customizer's picker lists first-party first because <c>BuiltInExtensionTable</c> registers
/// first), and <see cref="Diagnostics"/> records every refusal so a silently-missing action is always explainable.
/// </summary>
public sealed class WaveeRegistryTable<T> where T : class
{
    readonly List<string> _keys = new();
    readonly List<T> _items = new();
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    readonly List<WaveeRegistryDiagnostic> _diagnostics = new();

    public int Count => _items.Count;

    /// <summary>Registration order. Stable: nothing is ever removed (a disabled extension is filtered at the
    /// consumption site, never unregistered — that keeps every stored binding resolvable to a descriptor so the row can
    /// render visible-but-disabled with a reason instead of vanishing).</summary>
    public IReadOnlyList<T> Items => _items;

    public IReadOnlyList<string> Keys => _keys;

    public IReadOnlyList<WaveeRegistryDiagnostic> Diagnostics => _diagnostics;

    public string KeyAt(int i) => _keys[i];
    public T ItemAt(int i) => _items[i];

    public bool Contains(string? key) => key is not null && _index.ContainsKey(key);

    /// <summary>Register under <paramref name="key"/>. FIRST WINS: a duplicate is dropped with a
    /// <see cref="WaveeRegisterOutcome.RejectedDuplicate"/> diagnostic and the existing entry is left exactly as it
    /// was. Every outcome (including success) appends a diagnostic only when it is NOT a plain success — the audit list
    /// stays empty on a healthy startup.</summary>
    public WaveeRegisterOutcome Add(string? key, T? value)
    {
        if (value is null || string.IsNullOrEmpty(key))
            return Reject(key ?? "", WaveeRegisterOutcome.RejectedNull,
                value is null ? "null contribution" : "empty key");

        if (!WaveeExtensionKey.IsValid(key))
            return Reject(key!, WaveeRegisterOutcome.RejectedInvalidKey,
                "not a namespaced 'publisher.contribution' key");

        if (_index.TryGetValue(key!, out _))
            return Reject(key!, WaveeRegisterOutcome.RejectedDuplicate,
                "already registered; the first registration wins");

        _index[key!] = _items.Count;
        _keys.Add(key!);
        _items.Add(value);
        return WaveeRegisterOutcome.Registered;
    }

    public bool TryGet(string? key, out T value)
    {
        if (key is not null && _index.TryGetValue(key, out int i)) { value = _items[i]; return true; }
        value = null!;
        return false;
    }

    public int IndexOf(string? key) => key is not null && _index.TryGetValue(key, out int i) ? i : -1;

    WaveeRegisterOutcome Reject(string key, WaveeRegisterOutcome outcome, string detail)
    {
        _diagnostics.Add(new WaveeRegistryDiagnostic(key, outcome, detail));
        return outcome;
    }
}
