$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host '== build ==' -ForegroundColor Cyan
dotnet build ScadaDemo.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Write-Host '== tests ==' -ForegroundColor Cyan
dotnet test tests\Scada.Tests\Scada.Tests.csproj -c Debug --nologo
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

function Invoke-Smoke {
    param(
        [string]$Name,
        [string]$Exe,
        [int]$Seconds,
        [string]$ReportName
    )

    if (-not (Test-Path $Exe)) { throw "missing exe: $Exe" }
    Write-Host "== smoke $Name ($Seconds s) ==" -ForegroundColor Cyan
    $process = Start-Process -FilePath $Exe -ArgumentList @('--smoke', $Seconds) -PassThru
    if (-not $process.WaitForExit(($Seconds + 40) * 1000)) {
        Stop-Process -Id $process.Id -Force
        throw "$Name timed out"
    }
    if ($process.ExitCode -ne 0) { throw "$Name exit code $($process.ExitCode)" }

    $reportPath = Join-Path (Split-Path $Exe) (Join-Path 'data' $ReportName)
    if (-not (Test-Path $reportPath)) { throw "$Name missing report $reportPath" }
    $report = Get-Content $reportPath -Raw | ConvertFrom-Json
    if (-not $report.success) { throw "$Name smoke report success=false" }
    Write-Host "   PASS $Name" -ForegroundColor Green
}

function Invoke-V5Smoke {
    param([string]$Name, [string]$Exe, [string]$ReportName)

    Write-Host "== smoke $Name (V5 + upper mock) ==" -ForegroundColor Cyan
    $mockExe = Join-Path $root 'tools\Scada.UpperSystemMock\bin\Debug\net10.0\Scada.UpperSystemMock.exe'
    $mock = Start-Process -FilePath $mockExe -ArgumentList @('--auto', '--duration', '8') -PassThru
    try {
        Start-Sleep -Milliseconds 1200
        Invoke-Smoke -Name $Name -Exe $Exe -Seconds 20 -ReportName $ReportName
    }
    finally {
        if (-not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
    }
}

$win = 'demos\WinForms'
$wpf = 'demos\Wpf'
$bin = 'bin\Debug\net10.0-windows'

Invoke-Smoke 'V1 WinForms' "$win\Scada.WinForms.V1.SeeData\$bin\Scada.WinForms.V1.SeeData.exe" 8 'smoke-Scada-WinForms-V1-SeeData.json'
Invoke-Smoke 'V1 WPF' "$wpf\Scada.Wpf.V1.SeeData\$bin\Scada.Wpf.V1.SeeData.exe" 8 'smoke-Scada-Wpf-V1-SeeData.json'
Invoke-Smoke 'V2 WinForms' "$win\Scada.WinForms.V2.Modbus\$bin\Scada.WinForms.V2.Modbus.exe" 10 'smoke-Scada-WinForms-V2-Modbus.json'
Invoke-Smoke 'V2 WPF' "$wpf\Scada.Wpf.V2.Modbus\$bin\Scada.Wpf.V2.Modbus.exe" 10 'smoke-Scada-Wpf-V2-Modbus.json'
Invoke-Smoke 'V3 WinForms' "$win\Scada.WinForms.V3.AlarmHistory\$bin\Scada.WinForms.V3.AlarmHistory.exe" 15 'smoke-Scada-WinForms-V3-AlarmHistory.json'
Invoke-Smoke 'V3 WPF' "$wpf\Scada.Wpf.V3.AlarmHistory\$bin\Scada.Wpf.V3.AlarmHistory.exe" 15 'smoke-Scada-Wpf-V3-AlarmHistory.json'
Invoke-Smoke 'V4 WinForms' "$win\Scada.WinForms.V4.Control\$bin\Scada.WinForms.V4.Control.exe" 15 'smoke-Scada-WinForms-V4-ControlDemo.json'
Invoke-Smoke 'V4 WPF' "$wpf\Scada.Wpf.V4.Control\$bin\Scada.Wpf.V4.Control.exe" 15 'smoke-Scada-Wpf-V4-Control.json'
Invoke-V5Smoke 'V5 WinForms' "$win\Scada.WinForms.V5.Northbound\$bin\Scada.WinForms.V5.Northbound.exe" 'smoke-Scada-WinForms-V5-Northbound.json'
Invoke-V5Smoke 'V5 WPF' "$wpf\Scada.Wpf.V5.Northbound\$bin\Scada.Wpf.V5.Northbound.exe" 'smoke-Scada-Wpf-V5-Northbound.json'

Write-Host ''
Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green
