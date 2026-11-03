param([int]$Port = 15020, [int]$Unit = 1, [int]$Duration = 0)
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools\Scada.PlcSimulator'
$runArgs = @('run', '--project', $project, '-c', 'Debug', '--', '--port', $Port, '--unit', $Unit)
if ($Duration -gt 0) { $runArgs += @('--duration', $Duration) }
& dotnet @runArgs
