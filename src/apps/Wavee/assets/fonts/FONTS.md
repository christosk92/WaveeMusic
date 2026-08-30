# Bundled fonts

| File | What | Origin / licence |
| --- | --- | --- |
| `SegoeFluentIcons.ttf` | Segoe Fluent Icons v1.54, **unmodified** (2,033 glyphs, incl. the Fluent-only ones such as RefineSparkle U+F1D5) | Microsoft's Segoe Fluent Icons symbol font, (c) Microsoft Corporation, redistributed inside this Windows application per its licence (Microsoft provides the face for use in Windows apps: <https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font>). Embedding flag: Editable (`OS/2.fsType = 8`). Listed in `ops/build/notices-extra.json` -> `THIRD-PARTY-NOTICES.txt`. |
| `wavee-icons.otf` | Wavee's own three marks (Play next / Add to queue / Lyrics), built by `build-wavee-icons.py` | First party. |

Why Segoe Fluent Icons is bundled: the system family of the same name ships with Windows 11 only. On Windows 10 the
app's icon font used to be the OS face, so DirectWrite substituted Segoe MDL2 Assets and every glyph *added* in Fluent
Icons drew as tofu. `Design/Glyphs.cs` (`WaveeFonts.Icons`) points `Theme.IconFont` at this file at startup
(`Program.cs`); the engine loads it by path (`IDWriteFactory::CreateFontFileReference`), so the OS install state no
longer matters.

Refreshing the file: copy `C:\Windows\Fonts\SegoeIcons.ttf` from a current Windows 11 install (or the official
download, <https://aka.ms/SegoeFluentIcons>) over `SegoeFluentIcons.ttf`; the engine's generated `Icons.*` table
(`..\fluent-gpu\src\FluentGpu.Controls\glyphs.json`) must not reference a codepoint the shipped version lacks.
