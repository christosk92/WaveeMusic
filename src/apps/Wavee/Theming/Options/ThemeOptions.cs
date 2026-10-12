// ── Theming/Options/ThemeOptions.cs — option kinds and the schema (themes plan 5.3, BP 1.6) ──────────────────────────────
//
// A layer (structure, style) and a composition declare their options as DATA: a `ThemeOptionSchema` of `ThemeOption`s.
// Nothing here draws anything; the generated Settings section (T1b) reads the schema, the `ThemeOptionStore` persists the
// values, and a view reads one through `ctx.Options.Get(option.Key)`.
//
// STORAGE (one flat key per option): `theme.<themeId>.<layer>.<optionId>` through `Platform.Settings`, typed by the kind:
// toggle = bool, select = int (the enum value), slider = float, colour = int (packed ARGB). A stored value that does not
// read as the kind, falls outside the slider range, is no longer a choice, or whose option was renamed answers the
// option's default (BP 1.6 value rules); the stored value is left in place.

using System.Collections.Immutable;
using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>Which layer of a composition declares an option.</summary>
public enum OptionLayer : byte { Structure, Style, Composition }

/// <summary>Whether and to whom an option is shown.</summary>
public enum OptionVisibility : byte
{
    /// <summary>Shown in Settings.</summary>
    Always,
    /// <summary>Shown only with Developer mode on.</summary>
    Developer,
    /// <summary>A STRUCTURE PARAMETER: never shown, never persisted; a composition sets it through
    /// <see cref="CompositionEffect.StructureParameters"/> (WVTH011 requires the probe matrix to exercise it).</summary>
    Hidden,
}

/// <summary>How a change to an option reaches the screen (themes plan 4.2).</summary>
public enum ApplyMode : byte
{
    /// <summary>Written to the cell; the views that read it re-render. Only for colour-only or production-behaviour options.</summary>
    Live,
    /// <summary>Needs the theme transaction (a cut remount until A4.2): the option changes structure or a non-colour role.</summary>
    Transaction,
}

/// <summary>The core preferences a theme may surface as a read-only row of its own options page.</summary>
public enum CorePref : byte { AppMode, Zoom, ReducedMotionInfo, Marquee, StageFace }

/// <summary>How a select option is drawn.</summary>
public enum PickerStyle : byte { Radio, Combo, Cards, Swatches, Toggle /* exactly two choices */ }

/// <summary>The option is visible only while another option of the same layer holds <paramref name="EqualsValue"/>.</summary>
public sealed record OptionCondition(string OptionId, object EqualsValue);

/// <summary>One option a layer or a composition declares. Subclasses are the kinds below; the members the store needs are
/// internal and non-generic so it can persist any option without knowing its value type.</summary>
public abstract record ThemeOption(string Id, string TitleKey, string? DescriptionKey, string Group,
    OptionVisibility Visibility = OptionVisibility.Always, OptionCondition? VisibleWhen = null, bool Command = false,
    ApplyMode Apply = ApplyMode.Live)
{
    /// <summary>False for a command or a reference: nothing is stored under its id.</summary>
    internal abstract bool HoldsValue { get; }

    /// <summary>The declared default, boxed (null when the option holds no value).</summary>
    internal abstract object? DefaultBoxed { get; }

    /// <summary>The stored value under <paramref name="name"/> after the value rules; <paramref name="fallback"/> (the
    /// declared default, or a composition's override of it) when nothing valid is stored.</summary>
    internal abstract object? Read(IAppSettings settings, string name, object? fallback);

    /// <summary>Stores <paramref name="value"/> under <paramref name="name"/> (a value of another type is ignored).</summary>
    internal abstract void Write(IAppSettings settings, string name, object? value);

    /// <summary>True with the coerced value when <paramref name="input"/> is a valid value of this option.</summary>
    internal abstract bool TryCoerce(object? input, out object? value);

    /// <summary>Publishes <paramref name="value"/> to the option's cell; true when the cell changed.</summary>
    internal abstract bool Publish(OptionStore store, object? value, bool persist);

    /// <summary>The cell's current value, boxed.</summary>
    internal abstract object? Peek(OptionStore store);

    /// <summary>The loc keys of a select option's choices, in order (what the generated Settings row lists); empty for every other kind.</summary>
    internal virtual IReadOnlyList<string> ChoiceTitleKeys => Array.Empty<string>();

    /// <summary>The position of <paramref name="value"/> among a select option's choices, or -1 (not a select, not a choice).</summary>
    internal virtual int ChoiceIndexOf(object? value) => -1;

    /// <summary>The value of choice <paramref name="index"/> of a select option, or null when there is no such choice.</summary>
    internal virtual object? ChoiceAt(int index) => null;

    /// <summary>How a select option is drawn; null for every other kind.</summary>
    internal virtual PickerStyle? SelectPicker => null;
}

/// <summary>An on/off option.</summary>
public sealed record ToggleOption(string Id, string TitleKey, string? DescriptionKey, string Group, bool Default)
    : ThemeOption(Id, TitleKey, DescriptionKey, Group)
{
    /// <summary>The key a view reads and a store publishes.</summary>
    public OptionKey<bool> Key => new(Id, Default);

    internal override bool HoldsValue => true;
    internal override object? DefaultBoxed => Default;

    internal override object? Read(IAppSettings settings, string name, object? fallback)
        => settings.Get(new SettingKey<bool>(name, fallback is bool b ? b : Default));

    internal override void Write(IAppSettings settings, string name, object? value)
    {
        if (value is bool b) settings.Set(new SettingKey<bool>(name, Default), b);
    }

    internal override bool TryCoerce(object? input, out object? value)
    {
        if (input is bool b) { value = b; return true; }
        value = null;
        return false;
    }

    internal override bool Publish(OptionStore store, object? value, bool persist) => value is bool b && store.Publish(Key, b, persist);
    internal override object? Peek(OptionStore store) => store.Peek(Key);
}

/// <summary>One choice of a select option.</summary>
public sealed record SelectChoice<TEnum>(TEnum Value, string TitleKey) where TEnum : struct, Enum;

/// <summary>A choice among the members of an enum. Stored as the enum's int value, so a member appended later never
/// re-means a stored value; a stored value that is not one of <see cref="Choices"/> answers the default.</summary>
public sealed record SelectOption<TEnum>(string Id, string TitleKey, string? DescriptionKey, string Group, TEnum Default,
    ImmutableArray<SelectChoice<TEnum>> Choices, PickerStyle Picker)
    : ThemeOption(Id, TitleKey, DescriptionKey, Group)
    where TEnum : struct, Enum
{
    /// <summary>The key a view reads and a store publishes.</summary>
    public OptionKey<TEnum> Key => new(Id, Default);

    internal override bool HoldsValue => true;
    internal override object? DefaultBoxed => Default;

    internal override object? Read(IAppSettings settings, string name, object? fallback)
    {
        TEnum fb = fallback is TEnum e ? e : Default;
        int stored = settings.Get(new SettingKey<int>(name, ToInt(fb)));
        return IsChoice(stored, out TEnum choice) ? choice : fb;
    }

    internal override void Write(IAppSettings settings, string name, object? value)
    {
        if (value is TEnum e) settings.Set(new SettingKey<int>(name, ToInt(Default)), ToInt(e));
    }

    internal override bool TryCoerce(object? input, out object? value)
    {
        if (input is TEnum e && IsChoice(ToInt(e), out _)) { value = e; return true; }
        value = null;
        return false;
    }

    internal override bool Publish(OptionStore store, object? value, bool persist) => value is TEnum e && store.Publish(Key, e, persist);
    internal override object? Peek(OptionStore store) => store.Peek(Key);

    internal override IReadOnlyList<string> ChoiceTitleKeys
    {
        get
        {
            if (Choices.IsDefaultOrEmpty) return Array.Empty<string>();
            var keys = new string[Choices.Length];
            for (int i = 0; i < keys.Length; i++) keys[i] = Choices[i].TitleKey;
            return keys;
        }
    }

    internal override int ChoiceIndexOf(object? value)
    {
        if (value is not TEnum e || Choices.IsDefaultOrEmpty) return -1;
        int raw = ToInt(e);
        for (int i = 0; i < Choices.Length; i++)
            if (ToInt(Choices[i].Value) == raw) return i;
        return -1;
    }

    internal override object? ChoiceAt(int index)
        => !Choices.IsDefaultOrEmpty && (uint)index < (uint)Choices.Length ? Choices[index].Value : null;

    internal override PickerStyle? SelectPicker => Picker;

    private static int ToInt(TEnum value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private bool IsChoice(int raw, out TEnum choice)
    {
        if (!Choices.IsDefaultOrEmpty)
        {
            foreach (var c in Choices)
            {
                if (ToInt(c.Value) == raw) { choice = c.Value; return true; }
            }
        }
        choice = Default;
        return false;
    }
}

/// <summary>A number in a range, moved in steps.</summary>
public sealed record SliderOption(string Id, string TitleKey, string? DescriptionKey, string Group, float Default,
    float Min, float Max, float Step, string? UnitKey)
    : ThemeOption(Id, TitleKey, DescriptionKey, Group)
{
    /// <summary>The key a view reads and a store publishes.</summary>
    public OptionKey<float> Key => new(Id, Default);

    internal override bool HoldsValue => true;
    internal override object? DefaultBoxed => Default;

    internal override object? Read(IAppSettings settings, string name, object? fallback)
    {
        float fb = fallback is float f ? f : Default;
        float stored = settings.Get(new SettingKey<float>(name, fb));
        return InRange(stored) ? stored : fb;
    }

    internal override void Write(IAppSettings settings, string name, object? value)
    {
        if (value is float f) settings.Set(new SettingKey<float>(name, Default), f);
    }

    internal override bool TryCoerce(object? input, out object? value)
    {
        float f;
        switch (input)
        {
            case float single: f = single; break;
            case double d: f = (float)d; break;
            case int i: f = i; break;
            default: value = null; return false;
        }
        if (!InRange(f)) { value = null; return false; }
        value = f;
        return true;
    }

    internal override bool Publish(OptionStore store, object? value, bool persist) => value is float f && store.Publish(Key, f, persist);
    internal override object? Peek(OptionStore store) => store.Peek(Key);

    private bool InRange(float v) => float.IsFinite(v) && v >= Min && v <= Max;
}

/// <summary>A colour, optionally offered as swatches. Stored as packed ARGB (8 bits per channel).</summary>
public sealed record ColorOption(string Id, string TitleKey, string? DescriptionKey, string Group, ColorF Default,
    ImmutableArray<ColorF> Swatches)
    : ThemeOption(Id, TitleKey, DescriptionKey, Group)
{
    /// <summary>The key a view reads and a store publishes.</summary>
    public OptionKey<ColorF> Key => new(Id, Default);

    internal override bool HoldsValue => true;
    internal override object? DefaultBoxed => Default;

    internal override object? Read(IAppSettings settings, string name, object? fallback)
    {
        ColorF fb = fallback is ColorF c ? c : Default;
        return settings.TryGet(new SettingKey<int>(name, Pack(fb)), out int packed) ? Unpack(packed) : fb;
    }

    internal override void Write(IAppSettings settings, string name, object? value)
    {
        if (value is ColorF c) settings.Set(new SettingKey<int>(name, Pack(Default)), Pack(c));
    }

    internal override bool TryCoerce(object? input, out object? value)
    {
        if (input is ColorF c) { value = c; return true; }
        value = null;
        return false;
    }

    internal override bool Publish(OptionStore store, object? value, bool persist) => value is ColorF c && store.Publish(Key, c, persist);
    internal override object? Peek(OptionStore store) => store.Peek(Key);

    /// <summary>The colour as 0xAARRGGBB.</summary>
    internal static int Pack(ColorF c)
    {
        static uint Ch(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return unchecked((int)((Ch(c.A) << 24) | (Ch(c.R) << 16) | (Ch(c.G) << 8) | Ch(c.B)));
    }

    /// <summary>The inverse of <see cref="Pack"/>.</summary>
    internal static ColorF Unpack(int argb)
    {
        uint u = unchecked((uint)argb);
        return ColorF.FromRgba((byte)((u >> 16) & 0xFF), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF), (byte)(u >> 24));
    }
}

/// <summary>A button in the options section that runs a verb (reset, open a folder...). Holds no value.</summary>
public sealed record ActionOption(string Id, string TitleKey, string Group, string VerbId)
    : ThemeOption(Id, TitleKey, null, Group, Command: true)
{
    internal override bool HoldsValue => false;
    internal override object? DefaultBoxed => null;
    internal override object? Read(IAppSettings settings, string name, object? fallback) => null;
    internal override void Write(IAppSettings settings, string name, object? value) { }
    internal override bool TryCoerce(object? input, out object? value) { value = null; return false; }
    internal override bool Publish(OptionStore store, object? value, bool persist) => false;
    internal override object? Peek(OptionStore store) => null;
}

/// <summary>A row that points at a core preference (mode, zoom...) instead of owning a value. Holds no value.</summary>
public sealed record PrefReferenceOption(string Id, string Group, CorePref Pref)
    : ThemeOption(Id, "", null, Group)
{
    internal override bool HoldsValue => false;
    internal override object? DefaultBoxed => null;
    internal override object? Read(IAppSettings settings, string name, object? fallback) => null;
    internal override void Write(IAppSettings settings, string name, object? value) { }
    internal override bool TryCoerce(object? input, out object? value) { value = null; return false; }
    internal override bool Publish(OptionStore store, object? value, bool persist) => false;
    internal override object? Peek(OptionStore store) => null;
}

/// <summary>The options one layer (or a composition) declares, in display order.</summary>
public sealed record ThemeOptionSchema(ImmutableArray<ThemeOption> Options)
{
    /// <summary>A schema with no options.</summary>
    public static ThemeOptionSchema Empty { get; } = new(ImmutableArray<ThemeOption>.Empty);

    /// <summary>A schema of <paramref name="options"/>.</summary>
    public static ThemeOptionSchema Of(params ThemeOption[] options) => new(ImmutableArray.Create(options));

    /// <summary>The options, never a default (uninitialised) array.</summary>
    public ImmutableArray<ThemeOption> All => Options.IsDefault ? ImmutableArray<ThemeOption>.Empty : Options;

    /// <summary>The option with <paramref name="id"/>, or null.</summary>
    public ThemeOption? Find(string id)
    {
        foreach (var o in All)
            if (string.Equals(o.Id, id, StringComparison.Ordinal)) return o;
        return null;
    }
}
