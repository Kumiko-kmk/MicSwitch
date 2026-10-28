$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class IconNative { [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h); }
'@
foreach ($mode in @('on','off')) {
    $bitmap = New-Object System.Drawing.Bitmap 32,32
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#182436'))
    $color = if ($mode -eq 'on') { '#58DFBD' } else { '#99ACC5' }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($color)),2
    $g.FillEllipse($bg,0,0,31,31)
    $g.DrawArc($pen,12,5,8,8,180,180)
    $g.DrawLine($pen,12,9,12,16)
    $g.DrawLine($pen,20,9,20,16)
    $g.DrawArc($pen,12,12,8,8,0,180)
    $g.DrawArc($pen,8,10,16,15,0,180)
    $g.DrawLine($pen,16,25,16,28)
    $g.DrawLine($pen,11,28,21,28)
    if ($mode -eq 'off') { $g.DrawLine($pen,7,5,26,27) }
    $handle = $bitmap.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $file = [System.IO.File]::Create((Join-Path $PSScriptRoot "mic-$mode.ico"))
    $icon.Save($file)
    $file.Dispose(); $icon.Dispose(); [IconNative]::DestroyIcon($handle) | Out-Null
    $pen.Dispose(); $bg.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
