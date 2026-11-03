# SCADA 基础概念（结合本项目代码）

## 1. 点位 / Tag

点位是 SCADA 的最小数据单位：一个温度、一个液位、一个泵状态都是一个点位。

- 定义：`Scada.Core/Tags/TagModels.cs` 的 `TagDefinition`
- 值：`TagValue`（工程值、原始值、质量、时间戳）
- 地址：`TagAddress`（`HR:0`、`C:3`、`40001` 等写法）

点位不只是“地址 + 类型”，还包含：缩放 `y = kx + b`、小数位、单位、报警限、是否可写。

## 2. 扫描周期 / Scan Cycle

采集引擎每隔固定时间读一次设备，这个间隔就是扫描周期。

- 代码：`Scada.Core/Acquisition/AcquisitionEngine.cs`
- 默认 500ms（`config/devices.json` 的 `scanIntervalMs`）
- 每轮：连接检查 → 读设备 → 写 TagStore → 统计
- 失败：记录错误、把点位标 Bad、指数退避重连（1/2/4/8/10 秒）

扫描周期不是越短越好：设备/网络有承受上限，界面刷新也有成本。

## 3. 质量码 / Quality

工业数据必须带质量：

| 质量 | 含义 | 本项目场景 |
|---|---|---|
| Good | 正常 | 采集成功 |
| Uncertain | 不确定 | 预留 |
| Bad | 坏值 | 通信失败、地址错误、未采集 |
| Stale | 陈旧 | 断线后保留最后值但标记过期 |

`TagStore.MarkAllBad` 会保留最后的数值，但把质量改成 Bad，界面显示 `ERR`。**坏值绝不能伪装成 0**，否则趋势和报警都会骗人。

## 4. 原始值与工程值

设备寄存器里通常是整数。例如液位寄存器值 418，缩放 0.1，工程值 41.8%。

- 解码：`TagValueCodec.Decode`
- 编码：`TagValueCodec.Encode`
- 公式：`工程值 = 原始值 × Scale + Offset`

float32 占两个 16 位寄存器，字节序有 ABCD/DCBA/BADC/CDAB 四种，接真实设备时必须和 PLC 工程师确认。

## 5. 标签库 / TagStore

采集线程写、界面/报警/北向读。`TagStore` 用不可变 `TagValue` + `ConcurrentDictionary`，读方拿到的是一致快照，不需要锁住整个系统。

- `Snapshot()`：一次拿到全部点位
- `GetHistory()`：内存环形缓冲，给实时趋势
- `TagChanged`：逐点事件

## 6. 报警 / Alarm

报警不是“值超了就叫”，而是一个状态机：

```text
Normal → Active → Acked → Cleared → Normal
```

- HH/H/L/LL 四级
- 死区：恢复时要离开阈值一段距离，防止临界抖动刷屏
- 延时：连续超限 N 秒才触发，防止尖峰误报
- 确认：操作员知道这条报警了，但报警不会因此消失

代码：`Scada.Alarms/AlarmEngine.cs`。

## 7. 历史 / History

实时值只保留最近一段；历史落 SQLite，用于追溯和趋势。

- 表：`tag_history` / `alarm_events` / `command_audit`
- 代码：`Scada.Storage/SqliteHistoryStore.cs`
- V3 每个采集周期写一次全量点位；生产项目会做批量、压缩、保留策略和时序库。

## 8. 控制 / Command

SCADA 写设备不是“发一条 Modbus 就完了”，而是一个可审计的状态机：

```text
Pending → Validating → Writing → Verifying → Succeeded / Failed / Rejected / Timeout
```

- 联锁：写之前检查工艺条件
- 确认：关键操作要操作员二次确认
- 回读：写完再读一次，确认设备真的接受了
- 审计：谁、什么时候、从哪个来源、写了什么、回读多少、结果如何

代码：`Scada.Control/CommandService.cs`。

## 9. 联锁 / Interlock

联锁是软件层的“不允许”：

- 液位 <10% 不允许启泵
- 液位 <30%、温度 ≥95°C、自动模式时不允许手动开加热器
- 设定值必须在 30–95°C

代码：`Scada.Control/InterlockEngine.cs`。

联锁不能替代设备侧保护：PLC 里仍然要有低液位停泵、超温断加热。两层保护的关系是“软件尽量不发出危险命令，设备保证即使收到也安全”。

## 10. 北向 / Northbound

SCADA 往上与 MES/上位系统对接，常见两种通道：

- **REST**：请求/响应，适合查询、按需写点、系统集成
- **MQTT**：发布/订阅，适合高频遥测、报警事件、远程命令

本项目 V5 同时提供两者，并让它们最终都走同一个 `CommandService`，保证联锁/回读/审计只有一套。

代码：`Scada.Northbound/RestApiServer.cs`、`MqttNorthbound.cs`、`NorthboundCoordinator.cs`。
