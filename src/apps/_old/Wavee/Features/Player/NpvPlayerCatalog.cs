using System;

namespace Wavee;

public enum NpvPlayerGroup : byte { Media, Devices, Software }
public enum NpvOptionKind : byte { Segmented, Swatch }

/// The one table behind the flyout, the Settings rows, the art context menu and the tests. Ids and slugs are PERSISTED.
/// Labels are Loc KEYS. The DEFAULT choice is always listed FIRST (so stored default 0 is always valid).
public static class NpvPlayerCatalog
{
    public readonly record struct Choice(string Slug, string LabelKey, uint Swatch = 0);
    public readonly record struct OptionDef(string Slug, string LabelKey, NpvOptionKind Kind, Choice[] Choices);
    public readonly record struct Preset(int Id, string Slug, string LabelKey, string ShortLabelKey, NpvPlayerGroup Group, OptionDef[] Options);

    public const int Record = 0, Cassette = 1, Reel = 2, Cd = 3, Turntable = 4, Ipod = 5, Winamp = 6, Vu = 7,
                     Zune = 8, Wmp = 9, Canvas = 10, Picture = 11;
    public const int DefaultPresetId = Record;
    public const int PerGroup = 4;
    public const uint FromCover = 0;   // Swatch 0 = derive from the cover (album-colour vinyl)

    static readonly OptionDef Finish = new("finish", "player.opt.finish", NpvOptionKind.Swatch,
    [
        new("black", "player.choice.black", 0xFF15171C), new("clear", "player.choice.clear", 0xFF8A93A6),
        new("album", "player.choice.albumColour", FromCover), new("splatter", "player.choice.splatter", 0xFFF5F1EA),
        new("marble", "player.choice.marble", 0xFF6B4FA8),
    ]);
    static readonly OptionDef Size   = Seg("size",   "player.opt.size",   ("12", "player.choice.lp12"), ("7", "player.choice.single7"));
    static readonly OptionDef Rpm    = Seg("rpm",    "player.opt.speed",  ("33", "player.choice.rpm33"), ("45", "player.choice.rpm45"));
    static readonly OptionDef Sleeve = Seg("sleeve", "player.opt.sleeve", ("on", "player.choice.shown"), ("off", "player.choice.hidden"));

    public static readonly Preset[] Presets =
    [
        new(Record,    "record",    "player.style.record",    "player.style.record",      NpvPlayerGroup.Media,    [Finish, Size, Rpm, Sleeve]),
        new(Cassette,  "cassette",  "player.style.cassette",  "player.style.cassette",    NpvPlayerGroup.Media,
        [
            Seg("shell", "player.opt.shell", ("black","player.choice.black"), ("clear","player.choice.clear"), ("cream","player.choice.cream"), ("smoke","player.choice.smoke")),
            Seg("label", "player.opt.label", ("type1","player.choice.typeI"), ("chrome","player.choice.chrome"), ("hand","player.choice.handwritten")),
            Seg("side",  "player.opt.side",  ("a","player.choice.sideA"), ("b","player.choice.sideB")),
        ]),
        new(Reel,      "reel",      "player.style.reel",      "player.style.reelShort",   NpvPlayerGroup.Media,
            [Seg("reel", "player.opt.reels", ("alu","player.choice.aluminium"), ("black","player.choice.black"), ("clear","player.choice.clear"))]),
        new(Cd,        "cd",        "player.style.cd",        "player.style.cdShort",     NpvPlayerGroup.Media,
            [Seg("format", "player.opt.format", ("cd","player.choice.cd"), ("md","player.choice.minidisc"))]),
        new(Turntable, "turntable", "player.style.turntable", "player.style.turntable",   NpvPlayerGroup.Devices,  [Finish, Size, Rpm, Sleeve]),
        new(Ipod,      "ipod",      "player.style.ipod",      "player.style.ipodShort",   NpvPlayerGroup.Devices,
        [
            Seg("body", "player.opt.body", ("silver","player.choice.silver"), ("black","player.choice.black"), ("u2","player.choice.u2")),
            Seg("lcd",  "player.opt.lcd",  ("white","player.choice.white"), ("green","player.choice.green")),
        ]),
        new(Winamp,    "winamp",    "player.style.winamp",    "player.style.winamp",      NpvPlayerGroup.Devices,
        [
            Seg("skin", "player.opt.skin",     ("base","player.choice.base"), ("modern","player.choice.modern"), ("dark","player.choice.dark")),
            Seg("vis",  "player.opt.analyser", ("spectrum","player.choice.spectrum"), ("scope","player.choice.oscilloscope")),
        ]),
        new(Vu,        "vu",        "player.style.vu",        "player.style.vuShort",     NpvPlayerGroup.Devices,
        [
            Seg("face",       "player.opt.face",    ("ivory","player.choice.ivory"), ("blue","player.choice.blue"), ("black","player.choice.black")),
            Seg("ballistics", "player.opt.needles", ("vu","player.choice.vu"), ("ppm","player.choice.ppm")),
        ]),
        new(Zune,      "zune",      "player.style.zune",      "player.style.zune",        NpvPlayerGroup.Software,
        [
            new("accent", "player.opt.accent", NpvOptionKind.Swatch,
            [
                new("pink","player.choice.pink",0xFFF0568C), new("orange","player.choice.orange",0xFFFF7A1A),
                new("green","player.choice.green",0xFF8CBF26), new("blue","player.choice.blue",0xFF1BA1E2),
            ]),
            Rpm,
        ]),
        new(Wmp,       "wmp",       "player.style.wmp",       "player.style.wmpShort",    NpvPlayerGroup.Software,
        [
            Seg("preset", "player.opt.preset", ("bars","player.choice.barsWaves"), ("alchemy","player.choice.alchemy"), ("battery","player.choice.battery")),
            Seg("colour", "player.opt.colour", ("accent","player.choice.accent"), ("cover","player.choice.fromCover")),
        ]),
        new(Canvas,    "canvas",    "player.style.canvas",    "player.style.canvasShort", NpvPlayerGroup.Software,
        [
            Seg("drift", "player.opt.drift", ("slow","player.choice.slow"), ("fast","player.choice.fast")),
            Seg("bleed", "player.opt.bleed", ("soft","player.choice.soft"), ("strong","player.choice.strong")),
        ]),
        new(Picture,   "picture",   "player.style.picture",   "player.style.pictureShort", NpvPlayerGroup.Software, [Size, Rpm, Sleeve]),
    ];

    public static bool IsPresetId(int id) => (uint)id < (uint)Presets.Length && Presets[id].Id == id;
    public static Preset ById(int id) => Presets[IsPresetId(id) ? id : DefaultPresetId];
    public static int Group(NpvPlayerGroup g, Span<Preset> dest) { int n = 0; foreach (var p in Presets) if (p.Group == g) dest[n++] = p; return n; }
    /// Index of the current choice for option `slug` (or 0) — the helper faces use: Choice(settings, preset, "rpm").
    public static OptionDef Option(in Preset p, string slug) { foreach (var o in p.Options) if (o.Slug == slug) return o; return p.Options[0]; }
    static OptionDef Seg(string slug, string labelKey, params (string Slug, string LabelKey)[] choices)
    { var c = new Choice[choices.Length]; for (int i = 0; i < c.Length; i++) c[i] = new(choices[i].Slug, choices[i].LabelKey); return new(slug, labelKey, NpvOptionKind.Segmented, c); }
}
