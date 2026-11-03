param([switch]$Wpf)
$root = Split-Path -Parent $PSScriptRoot
$project = if ($Wpf) {
    Join-Path $root 'demos\Wpf\Scada.Wpf.V2.Modbus'
} else {
    Join-Path $root 'demos\WinForms\Scada.WinForms.V2.Modbus'
}
dotnet run --project $project -c Debug
