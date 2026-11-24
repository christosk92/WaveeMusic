# Generates src/apps/Wavee/Playback/LocalMatch.Fold.cs: NFKD over the ranges below (Python unicodedata).
# Run: python -I tools/localmatch-fold.py src/apps/Wavee/Playback/LocalMatch.Fold.cs
import sys, unicodedata as u
R=[(0xA0,0x24F),(0x370,0x3FF),(0x400,0x4FF),(0x1E00,0x1EFF),(0x1F00,0x1FFF),(0x2000,0x218F),(0x2460,0x24FF),(0x3000,0x30FF),(0xFB00,0xFB4F),(0xFE30,0xFE6F),(0xFF00,0xFFEF)]
keys=[];vals=[]
for a,b in R:
  for c in range(a,b+1):
    ch=chr(c); d=u.normalize('NFKD',ch)
    if d!=ch:
      assert all(ord(x)<0x10000 for x in d) and len(d)<16
      keys.append(c); vals.append(d)
def esc(s):
  BS=chr(92)
  return "".join(x if 0x20<=ord(x)<0x7F and x not in (chr(34),BS) else BS+"u%04X"%ord(x) for x in s)
out=[]
out.append('// ── Playback/LocalMatch.Fold.cs — GENERATED: the compatibility decomposition LocalMatch folds with ───────────────────────')
out.append('//')
out.append('// `string.Normalize` is a no-op under InvariantGlobalization on NativeAOT (probed 2026-11-23: NFC/NFD/NFKC/NFKD all return')
out.append('// their input and `IsNormalized` answers true), so the matching keys fold with this table instead: NFKD of every code')
out.append('// point in Latin-1 Supplement and Latin Extended-A/B, Greek, Cyrillic, Latin Extended Additional, Greek Extended, General')
out.append('// Punctuation through Number Forms, Enclosed Alphanumerics, CJK Symbols and kana, Alphabetic Presentation Forms, CJK')
out.append('// Compatibility/Small Forms and Halfwidth/Fullwidth Forms, from Unicode %s (Python\'s unicodedata). %d entries.' % (u.unidata_version, len(keys)))
out.append('// Regenerate with tools/localmatch-fold.py; do not edit by hand.')
out.append('')
out.append('namespace Wavee;')
out.append('')
out.append('public static partial class LocalMatch')
out.append('{')
out.append('    /// <summary>The longest decomposition in the table: a key buffer is the text length times this.</summary>')
out.append('    const int MaxFold = %d;' % max(len(v) for v in vals))
out.append('')
out.append('    /// <summary>The code points that decompose, ascending (binary-searched).</summary>')
out.append('    static ReadOnlySpan<char> FoldKeys =>')
line='        "'
parts=[]
for i in range(0,len(keys),24):
  parts.append('        "'+esc(''.join(chr(k) for k in keys[i:i+24]))+'"')
out.append(' +\n'.join(parts)+';')
out.append('')
out.append('    /// <summary>Each key\'s decomposition, concatenated in key order; <see cref="FoldLengths"/> splits it.</summary>')
out.append('    static ReadOnlySpan<char> FoldValues =>')
allv=''.join(vals)
parts=[]
for i in range(0,len(allv),24):
  parts.append('        "'+esc(allv[i:i+24])+'"')
out.append(' +\n'.join(parts)+';')
out.append('')
out.append('    /// <summary>The length of each key\'s decomposition.</summary>')
out.append('    static ReadOnlySpan<byte> FoldLengths =>')
out.append('    [')
for i in range(0,len(vals),40):
  out.append('        '+', '.join(str(len(v)) for v in vals[i:i+40])+',')
out.append('    ];')
out.append('}')
open(sys.argv[1],'w',encoding='utf-8',newline='\n').write('\n'.join(out)+'\n')
print(len(keys), len(allv))
