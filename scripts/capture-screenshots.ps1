$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
New-Item -ItemType Directory -Force docs\images | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ScadaCapture {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

function Capture-App {
    param([string]$Exe, [string]$Output)

    $process = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
    try {
        for ($i = 0; $i -lt 40 -and $process.MainWindowHandle -eq 0; $i++) {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
        }
        Start-Sleep -Seconds 3
        $handle = $process.MainWindowHandle
        if ($handle -eq 0) { throw "no window: $Exe" }

        $rect = New-Object ScadaCapture+RECT
        [ScadaCapture]::GetWindowRect($handle, [ref]$rect) | Out-Null
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        $bitmap = New-Object System.Drawing.Bitmap $width, $height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        [ScadaCapture]::PrintWindow($handle, $hdc, 2) | Out-Null
        $graphics.ReleaseHdc($hdc)
        $bitmap.Save((Join-Path $root $Output), [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        Write-Host "captured $Output" -ForegroundColor Green
    }
    finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
}

$bin = 'bin\Debug\net10.0-windows'
Capture-App "$root\demos\WinForms\Scada.WinForms.V1.SeeData\$bin\Scada.WinForms.V1.SeeData.exe" 'docs\images\v1-winforms.png'
Capture-App "$root\demos\WinForms\Scada.WinForms.V2.Modbus\$bin\Scada.WinForms.V2.Modbus.exe" 'docs\images\v2-winforms.png'
