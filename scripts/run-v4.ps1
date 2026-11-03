param([switch]$Wpf)
$root = Split-Path -Parent $PSScriptRoot
$project = if ($Wpf) {
    Join-Path $root 'demos\Wpf\Scada.Wpf.V4.Control'
} else {
    Join-Path $root 'demos\WinForms\Scada.WinForms.V4.Control'
}
dotnet run --project $project -c Debug
