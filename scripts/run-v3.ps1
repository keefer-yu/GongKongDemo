param([switch]$Wpf)
$root = Split-Path -Parent $PSScriptRoot
$project = if ($Wpf) {
    Join-Path $root 'demos\Wpf\Scada.Wpf.V3.AlarmHistory'
} else {
    Join-Path $root 'demos\WinForms\Scada.WinForms.V3.AlarmHistory'
}
dotnet run --project $project -c Debug
