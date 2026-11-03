param([switch]$Wpf)
$root = Split-Path -Parent $PSScriptRoot
$project = if ($Wpf) {
    Join-Path $root 'demos\Wpf\Scada.Wpf.V1.SeeData'
} else {
    Join-Path $root 'demos\WinForms\Scada.WinForms.V1.SeeData'
}
dotnet run --project $project -c Debug
