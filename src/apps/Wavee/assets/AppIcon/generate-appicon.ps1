# Multi-res Wavee appicon.ico from appicon-source.png (the WaveeMusic W-ribbon).
# Run: powershell -ExecutionPolicy Bypass -File src/apps/Wavee/assets/AppIcon/generate-appicon.ps1
#
# The source is a squircle anti-aliased against WHITE, then given a transparent outside. A second rounded clip
# (and Windows 11's own taskbar clip on top of that) left a 1px white fringe at the corners. This script
# fills transparent / near-white fringe pixels with the plate navy so the .ico is a full-bleed opaque square;
# Explorer / the taskbar apply the squircle.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$source = Join-Path $here 'appicon-source.png'
$icoPath = Join-Path $here 'appicon.ico'
if (-not (Test-Path $source)) { throw "missing source artwork: $source" }

Add-Type -ReferencedAssemblies System.Drawing.dll -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class WaveeIconPlate {
  // Opaque navy on every non-plate pixel (transparent outside + the white AA fringe from a white-matted export).
  public static void FillFringe(Bitmap bmp) {
    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
    var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    try {
      int stride = data.Stride;
      int n = Math.Abs(stride) * bmp.Height;
      byte[] px = new byte[n];
      Marshal.Copy(data.Scan0, px, 0, n);
      int sx = bmp.Width / 2;
      int sy = Math.Max(0, (int)(bmp.Height * 0.08));
      int si = sy * stride + sx * 4;
      byte pb = px[si], pg = px[si + 1], pr = px[si + 2];
      for (int i = 0; i < n; i += 4) {
        byte B = px[i], G = px[i + 1], R = px[i + 2], A = px[i + 3];
        int max = Math.Max(R, Math.Max(G, B));
        int min = Math.Min(R, Math.Min(G, B));
        // Near-white/gray (the fringe), not the cyan W (high G/B, low R → large max-min).
        bool whiteish = max > 210 && (max - min) < 36;
        if (A < 255 || whiteish) {
          px[i] = pb; px[i + 1] = pg; px[i + 2] = pr; px[i + 3] = 255;
        }
      }
      Marshal.Copy(px, 0, data.Scan0, n);
    } finally { bmp.UnlockBits(data); }
  }
}
"@

$srcImg = [System.Drawing.Image]::FromFile($source)
$side = [Math]::Min($srcImg.Width, $srcImg.Height)
$srcSquare = New-Object System.Drawing.Bitmap($side,$side,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [System.Drawing.Graphics]::FromImage($srcSquare)
$sg.DrawImage($srcImg, (New-Object System.Drawing.Rectangle(0,0,$side,$side)),
              [int](($srcImg.Width-$side)/2),[int](($srcImg.Height-$side)/2),$side,$side,[System.Drawing.GraphicsUnit]::Pixel)
$sg.Dispose(); $srcImg.Dispose()
[WaveeIconPlate]::FillFringe($srcSquare)

function New-IconBitmap([int]$size){
  $bmp = New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::FromArgb(255, $srcSquare.GetPixel([int]($side/2), [int]($side*0.08))))
  $attr = New-Object System.Drawing.Imaging.ImageAttributes
  $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
  $dest = New-Object System.Drawing.Rectangle(0,0,$size,$size)
  $g.DrawImage($srcSquare,$dest,0,0,$side,$side,[System.Drawing.GraphicsUnit]::Pixel,$attr)
  $g.Dispose(); $attr.Dispose()
  return $bmp
}

$sizes = 16,24,32,48,64,128,256
$png=@{}
foreach($s in $sizes){
  $b=New-IconBitmap $s
  $ms=New-Object System.IO.MemoryStream
  $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
  $png[$s]=$ms.ToArray()
  $ms.Dispose(); $b.Dispose()
}
$fs=[System.IO.File]::Create($icoPath); $bw=New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset=6+16*$sizes.Count
foreach($s in $sizes){ $len=$png[$s].Length; $wh=if($s -ge 256){0}else{$s}
  $bw.Write([byte]$wh); $bw.Write([byte]$wh); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset+=$len }
foreach($s in $sizes){ $bw.Write($png[$s]) }
$bw.Flush(); $fs.Close()
$srcSquare.Dispose()
Write-Output ("wrote {0} ({1} bytes)" -f $icoPath,(Get-Item $icoPath).Length)
