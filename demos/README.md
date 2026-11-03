# Demo 目录

每个版本都是独立可运行的程序，共享 `src/` 下的核心库；版本之间的差异见 [版本演进](../docs/02-版本演进与代码导读.md)。

## WinForms

| 版本 | 项目 | 运行 |
|---|---|---|
| V1 | `Scada.WinForms.V1.SeeData` | `dotnet run --project demos/WinForms/Scada.WinForms.V1.SeeData -c Debug` |
| V2 | `Scada.WinForms.V2.Modbus` | `dotnet run --project demos/WinForms/Scada.WinForms.V2.Modbus -c Debug` |
| V3 | `Scada.WinForms.V3.AlarmHistory` | `dotnet run --project demos/WinForms/Scada.WinForms.V3.AlarmHistory -c Debug` |
| V4 | `Scada.WinForms.V4.Control` | `dotnet run --project demos/WinForms/Scada.WinForms.V4.Control -c Debug` |
| V5 | `Scada.WinForms.V5.Northbound` | `scripts\run-v5.ps1`（需上位模拟器） |

## WPF

| 版本 | 项目 | 运行 |
|---|---|---|
| V1 | `Scada.Wpf.V1.SeeData` | `dotnet run --project demos/Wpf/Scada.Wpf.V1.SeeData -c Debug` |
| V2 | `Scada.Wpf.V2.Modbus` | `dotnet run --project demos/Wpf/Scada.Wpf.V2.Modbus -c Debug` |
| V3 | `Scada.Wpf.V3.AlarmHistory` | `dotnet run --project demos/Wpf/Scada.Wpf.V3.AlarmHistory -c Debug` |
| V4 | `Scada.Wpf.V4.Control` | `dotnet run --project demos/Wpf/Scada.Wpf.V4.Control -c Debug` |
| V5 | `Scada.Wpf.V5.Northbound` | `scripts\run-v5.ps1 -Wpf` |

## 通用启动参数

- `--smoke 秒数`：自动启动、运行、写 `data/smoke-*.json`、退出。
- `--demo`：自动启动，供截图/演示。
- `--no-autostart`：手动点按钮。
- `--config <path>`：指定 `devices.json`。
- `--northbound <path>`：V5 指定 `northbound.json`。

## 截图

- [V1 WinForms](../docs/images/v1-winforms.png)
- [V2 WinForms](../docs/images/v2-winforms.png)

WPF 版界面结构与 WinForms 一致，采用同一套深色主题和点位卡。
