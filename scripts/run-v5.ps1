param([switch]$Wpf, [int]$Duration = 8)
$root = Split-Path -Parent $PSScriptRoot
$mockProject = Join-Path $root 'tools\Scada.UpperSystemMock'
$appProject = if ($Wpf) {
    Join-Path $root 'demos\Wpf\Scada.Wpf.V5.Northbound'
} else {
    Join-Path $root 'demos\WinForms\Scada.WinForms.V5.Northbound'
}

dotnet build $mockProject -c Debug --nologo | Out-Null
$mockExe = Join-Path $mockProject 'bin\Debug\net10.0\Scada.UpperSystemMock.exe'
$mock = Start-Process -FilePath $mockExe `
    -ArgumentList @('--auto', '--duration', $Duration) `
    -PassThru
try {
    Start-Sleep -Milliseconds 1500
    dotnet run --project $appProject -c Debug
}
finally {
    if (-not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}
