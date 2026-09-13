# Wavee's notification-area (tray) icons: 3 glyphs x 2 taskbar variants x 6 frames, written as committed .ico files.
# Run: powershell -ExecutionPolicy Bypass -File ops/build/generate-tray-icons.ps1 [-PreviewPath <sheet.png>]
# Spec: docs/plans/wavee/wavee-0.3-tray-implementation.md section 6 (section 13 decides the rest). No build step runs
# this; re-run it by hand after changing the brand artwork or a mask, and commit src/apps/Wavee/assets/tray/*.ico.
#
# WHERE THE PIXELS COME FROM
#   24/32/40/48  The W-ribbon SILHOUETTE traced from src/apps/Wavee/assets/AppIcon/appicon-source.png (the ribbon is
#                blue-bright, the navy plate is not), placed on a 24-unit master (x 1..23, centred on y 12) and
#                box-filtered down. The master is 960 px, so every frame is an exact integer box: no resampler blur.
#   16/20        Hand-hinted masks, ops/build/tray-icons/tray-{16,20}-glyph.txt: a downsampled ribbon is a smudge at
#                16 px. Alpha per character: '#' 255, '*' 192, '+' 128, '-' 64, '.' 0. Text, so a review can diff it.
#
# THE THREE GLYPHS (Tray.Glyph in src/apps/Wavee/Platform/Tray.cs)
#   normal   The ribbon. Playing/paused is deliberately NOT a glyph state (it would flip on every skip).
#   offline  The ribbon at 45 % alpha. A cut-out "blocked" badge disconnects the ribbon's right arm and reads "W!" at
#            16 px; dimming is the one "not doing anything" cue that survives every frame.
#   update   The ribbon plus a solid dot in the free bottom-right corner (tray-{16,20}-dot.txt, an analytic disc
#            above 20 px), in the glyph colour, never accent. It touches no stroke, so nothing is cut away.
# TWO VARIANTS  on-dark = white ink for a dark taskbar, on-light = #1B1B1B ink for a light one (Tray.IconFileName).
# FRAMES        16 20 24 32 40 48 = SM_CXSMICON at 100/125/150/200/250/300 % (Tray.Frames / Tray.IconFrameFor).
param([string]$PreviewPath = '')

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$brand = Join-Path $repo 'src\apps\Wavee\assets\AppIcon\appicon-source.png'
$maskDir = Join-Path $PSScriptRoot 'tray-icons'
$outDir = Join-Path $repo 'src\apps\Wavee\assets\tray'
if (-not (Test-Path $brand)) { throw "missing brand artwork: $brand" }
New-Item -ItemType Directory -Force $outDir | Out-Null

$sizes = 16, 20, 24, 32, 40, 48
$states = 'normal', 'offline', 'update'
$variants = [ordered]@{ 'on-dark' = @(255, 255, 255); 'on-light' = @(27, 27, 27) }

Add-Type -ReferencedAssemblies System.Drawing.dll -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class WaveeTrayIcons
{
    const int M = 960;                       // master canvas px: 24 units x 40; 16, 20, 24, 32, 40 and 48 all divide it
    const float U = M / 24f;
    const float GlyphLeft = 1f, GlyphRight = 23f, GlyphCenterY = 12f;
    const float DotCx = 21.3f, DotCy = 21.3f, DotR = 2.5f, DotGap = 1.0f;
    public const float OfflineAlpha = 0.45f;

    static float[] s_glyph;

    // Trace the ribbon out of the brand artwork onto the master. Returns a one-line description for the log.
    public static string LoadBrand(string path)
    {
        int w, h;
        float[] cov;
        using (Bitmap b = new Bitmap(path))
        {
            w = b.Width; h = b.Height;
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = d.Stride;
            byte[] px = new byte[Math.Abs(stride) * h];
            Marshal.Copy(d.Scan0, px, 0, px.Length);
            b.UnlockBits(d);
            cov = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    // Ribbon pixels carry blue >= ~220 (cyan, blue and the violet fold alike); the plate stays <= ~120.
                    float c = px[i + 3] < 128 ? 0f : (px[i] - 130f) / 85f;
                    cov[y * w + x] = Clamp01(c);
                }
        }
        int x0 = w, x1 = -1, y0 = h, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (cov[y * w + x] >= 0.5f)
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
        if (x1 < 0) throw new InvalidDataException("no ribbon found in " + path);
        float gw = x1 - x0 + 1, gh = y1 - y0 + 1;
        float scale = (GlyphRight - GlyphLeft) * U / gw;
        float mx0 = GlyphLeft * U, my0 = GlyphCenterY * U - gh * scale / 2f;
        s_glyph = new float[M * M];
        for (int my = 0; my < M; my++)
            for (int mx = 0; mx < M; mx++)
                s_glyph[my * M + mx] = Bilinear(cov, w, h, x0 + (mx + 0.5f - mx0) / scale - 0.5f, y0 + (my + 0.5f - my0) / scale - 0.5f);
        return string.Format("ribbon bbox x{0}..{1} y{2}..{3} -> units x{4:F2}..{5:F2} y{6:F2}..{7:F2}",
            x0, x1, y0, y1, GlyphLeft, GlyphRight, my0 / U, (my0 + gh * scale) / U);
    }

    static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }

    static float Bilinear(float[] c, int w, int h, float x, float y)
    {
        if (x < 0 || y < 0 || x > w - 1 || y > h - 1) return 0f;
        int ix = Math.Min((int)x, w - 2), iy = Math.Min((int)y, h - 2);
        float fx = x - ix, fy = y - iy;
        float a = c[iy * w + ix] * (1 - fx) + c[iy * w + ix + 1] * fx;
        float b = c[(iy + 1) * w + ix] * (1 - fx) + c[(iy + 1) * w + ix + 1] * fx;
        return a * (1 - fy) + b * fy;
    }

    // One frame's alpha from the traced master (24 px and up).
    public static byte[] Vector(int size, string state)
    {
        if (M % size != 0) throw new ArgumentException("frame " + size + " does not divide the master");
        int k = M / size;
        byte[] a = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float sum = 0f;
                for (int sy = 0; sy < k; sy++)
                    for (int sx = 0; sx < k; sx++)
                    {
                        int my = y * k + sy, mx = x * k + sx;
                        float g = s_glyph[my * M + mx];
                        if (state == "update")
                        {
                            float px = (mx + 0.5f) / U - DotCx, py = (my + 0.5f) / U - DotCy;
                            float dist = (float)Math.Sqrt(px * px + py * py);
                            g = Math.Max(g * (1f - Clamp01((DotR + DotGap - dist) * U + 0.5f)), Clamp01((DotR - dist) * U + 0.5f));
                        }
                        sum += g;
                    }
                a[y * size + x] = (byte)Math.Round(255f * sum / (k * k));
            }
        if (state == "offline") Dim(a);
        return a;
    }

    // One frame's alpha from the hand-hinted masks (16 and 20 px). dotText may be null for the normal/offline glyphs.
    public static byte[] Hinted(int size, string state, string glyphText, string dotText)
    {
        byte[] a = ParseMask(glyphText, size);
        if (state == "update")
        {
            byte[] dot = ParseMask(dotText, size);
            for (int i = 0; i < a.Length; i++) a[i] = Math.Max(a[i], dot[i]);
        }
        if (state == "offline") Dim(a);
        return a;
    }

    static void Dim(byte[] a)
    {
        for (int i = 0; i < a.Length; i++) a[i] = (byte)Math.Round(a[i] * OfflineAlpha);
    }

    public static byte[] ParseMask(string text, int size)
    {
        string[] raw = text.Replace("\r", "").Split('\n');
        byte[] a = new byte[size * size];
        int row = 0;
        foreach (string line in raw)
        {
            string r = line.Trim();
            if (r.Length == 0) continue;
            if (row >= size) throw new InvalidDataException("mask has more than " + size + " rows");
            if (r.Length != size) throw new InvalidDataException("mask row " + row + " is " + r.Length + " chars, expected " + size);
            for (int x = 0; x < size; x++)
            {
                int v;
                switch (r[x])
                {
                    case '#': v = 255; break;
                    case '*': v = 192; break;
                    case '+': v = 128; break;
                    case '-': v = 64; break;
                    case '.': v = 0; break;
                    default: throw new InvalidDataException("mask row " + row + " has an unknown character '" + r[x] + "'");
                }
                a[row * size + x] = (byte)v;
            }
            row++;
        }
        if (row != size) throw new InvalidDataException("mask has " + row + " rows, expected " + size);
        return a;
    }

    // Straight (non-premultiplied) ARGB PNG bytes: the ink colour everywhere, the frame's alpha per pixel.
    public static byte[] Png(byte[] alpha, int size, int r, int g, int b)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            byte[] px = new byte[Math.Abs(d.Stride) * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = y * d.Stride + x * 4;
                    byte al = alpha[y * size + x];
                    px[i] = al == 0 ? (byte)0 : (byte)b;
                    px[i + 1] = al == 0 ? (byte)0 : (byte)g;
                    px[i + 2] = al == 0 ? (byte)0 : (byte)r;
                    px[i + 3] = al;
                }
            Marshal.Copy(px, 0, d.Scan0, px.Length);
            bmp.UnlockBits(d);
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    // A review sheet: one row per variant x state, every frame at 6x (nearest) and at 1x, on the taskbar colour.
    public static void Sheet(string path, byte[][] alphas, int[] frameSizes, int rows, int[][] inks, int[][] backs)
    {
        const int Zoom = 6, Pad = 8;
        int cellW = 0;
        foreach (int s in frameSizes) cellW += s * Zoom + Pad;
        int rowH = 48 * Zoom + Pad + 48 + Pad;
        int per = frameSizes.Length;
        using (Bitmap sheet = new Bitmap(Pad + cellW + 6 * 48, Pad + rows * rowH))
        {
            for (int r = 0; r < rows; r++)
            {
                int[] ink = inks[r], back = backs[r];
                int oy = Pad + r * rowH, ox = Pad;
                for (int y = oy - Pad; y < oy + rowH; y++)
                    for (int x = 0; x < sheet.Width; x++)
                        if (y >= 0 && y < sheet.Height) sheet.SetPixel(x, y, Color.FromArgb(255, back[0], back[1], back[2]));
                int smallX = Pad;
                for (int f = 0; f < per; f++)
                {
                    int s = frameSizes[f];
                    byte[] a = alphas[r * per + f];
                    for (int y = 0; y < s; y++)
                        for (int x = 0; x < s; x++)
                        {
                            int al = a[y * s + x];
                            Color c = Color.FromArgb(255, (ink[0] * al + back[0] * (255 - al)) / 255,
                                (ink[1] * al + back[1] * (255 - al)) / 255, (ink[2] * al + back[2] * (255 - al)) / 255);
                            for (int zy = 0; zy < Zoom; zy++)
                                for (int zx = 0; zx < Zoom; zx++) sheet.SetPixel(ox + x * Zoom + zx, oy + y * Zoom + zy, c);
                            sheet.SetPixel(smallX + x, oy + 48 * Zoom + Pad + y, c);
                        }
                    ox += s * Zoom + Pad;
                    smallX += s + Pad;
                }
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
"@

function Write-Ico([string]$path, [int[]]$frameSizes, [hashtable]$png) {
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frameSizes.Count)   # ICONDIR: reserved, type 1, count
        $offset = 6 + 16 * $frameSizes.Count
        foreach ($s in $frameSizes) {                                                         # ICONDIRENTRY per frame
            $len = $png[$s].Length
            $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0)
            $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
            $offset += $len
        }
        foreach ($s in $frameSizes) { $bw.Write($png[$s]) }
        $bw.Flush()
    }
    finally { $bw.Dispose(); $fs.Dispose() }
}

Write-Output ([WaveeTrayIcons]::LoadBrand($brand))

$masks = @{}
foreach ($s in 16, 20) {
    foreach ($part in 'glyph', 'dot') {
        $file = Join-Path $maskDir "tray-$s-$part.txt"
        if (-not (Test-Path $file)) { throw "missing hinted mask: $file" }
        $masks["$s-$part"] = [System.IO.File]::ReadAllText($file)
    }
}

$sheetAlphas = New-Object System.Collections.ArrayList
$sheetInks = New-Object System.Collections.ArrayList
$sheetBacks = New-Object System.Collections.ArrayList
foreach ($variant in $variants.Keys) {
    $ink = $variants[$variant]
    foreach ($state in $states) {
        $png = @{}
        foreach ($s in $sizes) {
            if ($s -le 20) { $alpha = [WaveeTrayIcons]::Hinted($s, $state, $masks["$s-glyph"], $masks["$s-dot"]) }
            else { $alpha = [WaveeTrayIcons]::Vector($s, $state) }
            $png[$s] = [WaveeTrayIcons]::Png($alpha, $s, $ink[0], $ink[1], $ink[2])
            [void]$sheetAlphas.Add($alpha)
        }
        $ico = Join-Path $outDir "wavee-$variant-$state.ico"
        Write-Ico $ico $sizes $png
        [void]$sheetInks.Add([int[]]$ink)
        if ($variant -eq 'on-dark') { [void]$sheetBacks.Add([int[]]@(32, 32, 32)) } else { [void]$sheetBacks.Add([int[]]@(238, 238, 238)) }
        Write-Output ("wrote {0} ({1} bytes)" -f $ico, (Get-Item $ico).Length)
    }
}

if ($PreviewPath) {
    [WaveeTrayIcons]::Sheet($PreviewPath, [byte[][]]$sheetAlphas.ToArray([byte[]]), [int[]]$sizes, $sheetInks.Count,
        [int[][]]$sheetInks.ToArray([int[]]), [int[][]]$sheetBacks.ToArray([int[]]))
    Write-Output "preview $PreviewPath"
}
