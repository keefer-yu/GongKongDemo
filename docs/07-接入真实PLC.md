# 接入真实 PLC

本项目默认连内置模拟器（`127.0.0.1:15020`）。接真实设备时按下面清单逐项核对。

## 1. 网络与端口

- [ ] PC 与 PLC 在同一网段，能 `ping` 通。
- [ ] PLC 的 Modbus TCP 端口（通常 502）已开放，防火墙放行。
- [ ] 用 Modbus Poll / QModMaster 能读到寄存器。
- [ ] UnitId（站号）正确，有些网关是 1，有些是 255。

## 2. 修改配置

`config/devices.json`：

```json
"device": {
  "host": "192.168.1.10",
  "port": 502,
  "unitId": 1,
  "scanIntervalMs": 500
}
```

## 3. 修改点位

```json
{
  "id": "temp",
  "name": "温度",
  "address": "HR:1",
  "dataType": "Float32",
  "byteOrder": "ABCD",
  "scale": 1,
  "decimals": 1,
  "unit": "°C"
}
```

- 地址优先用 `HR:0` 这种 0 基写法；用 4xxxx 时注意厂商偏移。
- 类型：`UInt16/Int16/UInt32/Int32/Float32/Bool`。
- 缩放：`工程值 = 原始值 * scale + offset`。
- Float32 占两个寄存器，`byteOrder` 必须与 PLC 一致。

## 4. 先跑 V2

1. 不要启动内置模拟 PLC。
2. 直接点“连接并采集”。
3. 看点位表：工程值/原始值/质量是否合理。
4. 看报文表：功能码、起始地址、数量是否符合预期。
5. 如果数值乱码：检查 byteOrder、数据类型、地址偏移。
6. 如果质量 Bad：检查 IP/端口/UnitId/防火墙/寄存器权限。

## 5. 再做控制（V4）

- 先用 Modbus Poll 手动写线圈/寄存器，确认 PLC 接受。
- 确认写入是“保持型”还是“点动型”；点动型需要脉冲逻辑。
- 确认写寄存器后 PLC 是否会覆盖（扫描周期竞争）。
- 把联锁阈值改成你的工艺值。
- 在 PLC 里也实现设备侧保护，不要只依赖 SCADA。

## 6. 最后做北向（V5）

- 改 `config/northbound.json` 的 REST URL、MQTT Broker、主题前缀、Token。
- 生产环境换成企业 Broker/MES 地址，启用 TLS/认证。
- 确认上位系统的命令幂等和 ACK 处理。

## 7. 常见坑

| 现象 | 可能原因 |
|---|---|
| 连接超时 | IP/端口/防火墙/PLC 未启用 Modbus TCP |
| 异常码 02 | 地址越界/区域不对（HR vs IR） |
| 数值差 10 倍 | 缩放系数错 |
| 数值乱码 | 字节序/数据类型错 |
| 写入成功但设备不动 | 写错寄存器/线圈、PLC 逻辑覆盖、需要脉冲 |
| 读一段时间就断 | PLC 连接数限制、网络抖动、扫描太快 |
| 多个客户端冲突 | PLC 只允许一个 Modbus TCP 连接 |

## 8. 生产化清单

- [ ] 用成熟协议库（NModbus/IoTClient）替换教学实现
- [ ] 连接池/重连/超时/熔断
- [ ] 采集分块与失败隔离
- [ ] 报警确认/抑制/分级/通知
- [ ] 命令幂等、权限、双人复核、审计
- [ ] 历史库容量/保留/备份
- [ ] 网络分区、TLS、账号权限
- [ ] 与 PLC 工程师一起做 FAT/SAT
