# Modbus TCP 入门（结合本项目实现）

## 1. 一帧 Modbus TCP 长什么样

```text
┌──────────────┬──────────────┬──────────────┬──────────┬──────────────────┐
│ Transaction  │ Protocol ID  │ Length       │ Unit ID  │ PDU              │
│ 2 bytes      │ 2 bytes = 0  │ 2 bytes      │ 1 byte   │ Function + Data  │
└──────────────┴──────────────┴──────────────┴──────────┴──────────────────┘
                            MBAP Header (7 bytes)
```

- **Transaction ID**：客户端递增，用来匹配请求/响应。
- **Protocol ID**：Modbus 固定 0。
- **Length**：后面的字节数，**包含 Unit ID**。
- **Unit ID**：从站地址（PLC 站号）。
- **PDU**：功能码 + 数据。

代码：`Scada.Protocol/Modbus/ModbusTcpClient.cs`（BuildAdu / ValidateResponse）、`ModbusTcpSlave.cs`（ProcessAdu）。

## 2. 本项目用到的功能码

| FC | 名称 | 用途 | 本 Demo |
|---|---|---|---|
| 01 | Read Coils | 读线圈 | 读泵/加热/阀/自动 |
| 03 | Read Holding Registers | 读保持寄存器 | 读液位/温度/压力/设定 |
| 05 | Write Single Coil | 写单线圈 | 启停泵/加热/阀/自动 |
| 06 | Write Single Register | 写单寄存器 | 写温度设定 |
| 16 (0x10) | Write Multiple Registers | 写多寄存器 | 写 float32 / 预留 |

从站还实现了 02（离散输入）、04（输入寄存器）以及异常响应。

## 3. 真实报文示例

读 6 个保持寄存器（液位、温度 2 个、压力、设定、心跳）：

```text
TX  00 01 00 00 00 06 01 03 00 00 00 06
    TID=1  Proto=0  Len=6  Unit=1  FC=03  Start=0  Qty=6

RX  00 01 00 00 00 0F 01 03 0C 01 A2 42 8A 00 00 00 FA 02 58 00 1C
    TID=1  Len=15  Unit=1  FC=03  ByteCount=12
    0x01A2 = 418  -> 液位 41.8%
    0x428A0000 = 69.0f -> 温度
    0x00FA = 250 -> 压力 0.250MPa
    0x0258 = 600 -> 设定 60.0°C
    0x001C = 心跳
```

写泵线圈 ON：

```text
TX  00 02 00 00 00 06 01 05 00 00 FF 00
    FC=05 Address=0 Value=0xFF00 (ON)
RX  00 02 00 00 00 06 01 05 00 00 FF 00
```

写温度设定 75.0°C（寄存器值 750 = 0x02EE）：

```text
TX  00 03 00 00 00 06 01 06 00 04 02 EE
```

在 V2 的右侧报文表里能直接看到这些帧，`TX` 是客户端发出，`RX` 是收到。

## 4. 地址写法

本项目同时支持：

| 写法 | 含义 |
|---|---|
| `HR:0` | 保持寄存器 0（0 基） |
| `C:3` | 线圈 3（0 基） |
| `IR:0` / `DI:0` | 输入寄存器 / 离散输入 |
| `40001` | 保持寄存器 0（1 基传统写法） |
| `30001` | 输入寄存器 0 |
| `10001` | 离散输入 0 |
| `00001` | 线圈 0 |

注意：不同 PLC 厂商/文档对 4xxxx 的偏移定义可能差 1，接现场时要用 Modbus Poll 实测确认。代码在 `TagAddress.Parse`。

## 5. 字节序

`float32` 占两个 16 位寄存器：

- `ABCD`：高字在前，字内高字节在前（最常见）
- `DCBA`：完全反序
- `BADC`：字内字节交换
- `CDAB`：字交换

在 `config/devices.json` 改 `byteOrder` 即可。V2 练习：把温度改成 `DCBA`，看数值变成什么样，再改回。

## 6. 异常响应

从站返回功能码最高位为 1：

```text
RX  00 01 00 00 00 03 01 83 02
    FC=0x83 (03|0x80)  ExceptionCode=02 (非法数据地址)
```

常见异常码：01 非法功能、02 非法地址、03 非法值、04 从站故障。

## 7. 本实现的边界

这是**教学实现**：

- 只支持 Modbus TCP，不支持 RTU；
- 只实现项目需要的功能码；
- 没有完整的并发/诊断/网关路由；
- 没有做生产级的连接池、重试策略、TLS/网关。

生产项目建议使用 NModbus、IoTClient 等成熟库；但先看懂本项目的 400 行，再换库会非常快。

## 8. 自己验证

```powershell
scripts\run-plc-simulator.ps1        # 启动 127.0.0.1:15020
scripts\run-v2.ps1                   # 连接并观察报文
```

或者用 Modbus Poll / QModMaster 连 `127.0.0.1:15020`，UnitId=1，读 HR0 起 6 个寄存器。
