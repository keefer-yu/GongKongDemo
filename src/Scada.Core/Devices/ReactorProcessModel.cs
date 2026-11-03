namespace Scada.Core.Devices;

public enum FaultKind
{
    None,
    HighTemperature,
    HighPressure,
    LowLevel,
}

public sealed record ReactorState(
    double Level,
    double Temperature,
    double Pressure,
    bool PumpRunning,
    bool HeaterOn,
    bool ValveOpen,
    bool AutoMode,
    double Setpoint)
{
    public bool FaultActive { get; init; }
}

/// <summary>
/// 软件版“被控对象”：一个小水箱/反应釜。
/// 它同时承担两个教学职责：
/// 1. 模拟真实设备的物理惯性，让趋势图有东西可看；
/// 2. 演示设备侧保护（低液位停泵、超温断加热），与 SCADA 侧联锁形成双保险。
/// </summary>
public sealed class ReactorProcessModel
{
    // 这个锁保护“设备状态”。会同时访问它的有三类线程：
    // 1. 模拟器的 200ms Tick 线程；2. Modbus 从站的读写请求；3. UI 的故障注入。
    // 真实 PLC 内部也是类似的一把“扫描周期锁”，只是由 PLC 固件完成。
    private readonly object _gate = new();

    private double _level = 40;
    private double _temperature = 25;
    private double _pressure = 0.25;
    private bool _pump;
    private bool _heater;
    private bool _valve;
    private bool _autoMode;
    private double _setpoint = 60;
    private ushort _heartbeat;

    private FaultKind _fault;
    private DateTimeOffset _faultUntil;
    private double _levelBeforeFault;
    private double _tempBeforeFault;
    private double _pressureBeforeFault;
    private bool _pumpBeforeFault;
    private bool _heaterBeforeFault;

    public double Level { get { lock (_gate) return _level; } }
    public double Temperature { get { lock (_gate) return _temperature; } }
    public double Pressure { get { lock (_gate) return _pressure; } }
    public bool PumpRunning { get { lock (_gate) return _pump; } }
    public bool HeaterOn { get { lock (_gate) return _heater; } }
    public bool ValveOpen { get { lock (_gate) return _valve; } }
    public bool AutoMode { get { lock (_gate) return _autoMode; } }
    public double Setpoint { get { lock (_gate) return _setpoint; } }

    public ReactorState Snapshot()
    {
        lock (_gate)
        {
            return new ReactorState(
                _level, _temperature, _pressure, _pump, _heater, _valve, _autoMode, _setpoint)
            {
                FaultActive = _fault != FaultKind.None && DateTimeOffset.UtcNow < _faultUntil,
            };
        }
    }

    public void Reset(double level = 40, double temperature = 25, double? pressure = null)
    {
        lock (_gate)
        {
            _level = Clamp(level, 0, 100);
            _temperature = Clamp(temperature, 0, 150);
            _pressure = Clamp(pressure ?? (0.1 + _level * 0.004 + (_temperature - 25) * 0.002), 0, 1);
            _pump = false;
            _heater = false;
            _valve = false;
            _autoMode = false;
            _setpoint = 60;
            _heartbeat = 0;
            _fault = FaultKind.None;
            _faultUntil = default;
        }
    }

    public void SetPump(bool on) { lock (_gate) _pump = on; }
    public void SetHeater(bool on) { lock (_gate) _heater = on; }
    public void SetValve(bool open) { lock (_gate) _valve = open; }
    public void SetAuto(bool auto) { lock (_gate) _autoMode = auto; }

    public void SetSetpoint(double value)
    {
        lock (_gate)
            _setpoint = Clamp(value, 0, 150);
    }

    /// <summary>
    /// 演示用“故障注入”：强制温度/压力/液位进入异常值，便于观察报警。
    /// 注入前先备份正常状态，故障到期后恢复，避免演示把设备永久弄坏。
    /// </summary>
    public void InjectFault(FaultKind fault, TimeSpan duration)
    {
        lock (_gate)
        {
            if (fault == FaultKind.None || duration <= TimeSpan.Zero)
            {
                _fault = FaultKind.None;
                return;
            }

            // 只有第一次注入时备份；连续注入多个故障时不覆盖最初状态。
            if (_fault == FaultKind.None)
            {
                _levelBeforeFault = _level;
                _tempBeforeFault = _temperature;
                _pressureBeforeFault = _pressure;
                _pumpBeforeFault = _pump;
                _heaterBeforeFault = _heater;
            }

            _fault = fault;
            _faultUntil = DateTimeOffset.UtcNow + duration;
        }
    }

    /// <summary>
    /// 推进一次“设备扫描”。dt 是距上次 Tick 的秒数，所以无论 100ms 还是 500ms
    /// 调用一次，物理变化速度都一致。这正是真实 PLC 周期扫描的简化版。
    /// </summary>
    public void Tick(double seconds)
    {
        if (seconds <= 0) return;

        // 防止程序休眠/断点后 dt 过大，一次把液位/温度“跳飞”。
        var dt = Math.Min(seconds, 5);

        lock (_gate)
        {
            if (_fault != FaultKind.None)
            {
                if (DateTimeOffset.UtcNow >= _faultUntil)
                {
                    // 故障到期：先恢复备份值，再继续正常工艺计算。
                    ClearFault();
                }
                else
                {
                    // 故障期间强制覆盖测量值；真实世界可能是传感器坏、工艺异常。
                    ApplyFault();
                    _heartbeat++;
                    return;
                }
            }

            // 液位：持续进液；泵 + 出口阀打开时出液。
            // 0.75/2.5 是“每秒百分比”，乘 dt 后与调用频率无关。
            _level += 0.75 * dt;
            if (_pump) _level -= (_valve ? 2.5 : 0.15) * dt;
            _level = Clamp(_level, 0, 100);

            // 温度：加热时向 120°C，否则向环境温度。出口流动会带走热量。
            // MoveTowards 让温度“逐渐靠近”目标，而不是瞬间跳变，趋势才自然。
            var target = _heater ? 120 : 25;
            var rate = _heater ? 0.45 : 0.15;
            _temperature = MoveTowards(_temperature, target, rate * dt);
            if (_pump && _valve) _temperature -= 0.08 * dt;
            _temperature = Clamp(_temperature, 0, 150);

            // 压力由液位和温度共同决定，叠加一点噪声，趋势更真实。
            // 真实项目里压力通常是独立测量点，这里是为了让 Demo 有联动效果。
            var noise = (Random.Shared.NextDouble() - 0.5) * 0.004;
            _pressure = Clamp(0.1 + _level * 0.004 + (_temperature - 25) * 0.002 + noise, 0, 1);

            // 设备侧保护：即使上位机乱写，设备也不允许干烧/干转。
            // 注意这是“设备自己”的保护，和 V4 的 SCADA 联锁是两层不同的防线。
            if (_level < 5) _pump = false;
            if (_level < 20 || _temperature > 110) _heater = false;

            // 自动温控在设备侧完成，SCADA 只写设定值。
            // 用 ±1°C 的回差模拟简单的继电器控制，避免加热器在设定点附近频繁开关。
            if (_autoMode)
                _heater = _temperature < _setpoint - 1;

            // 心跳寄存器每 Tick 加 1，上位机可用来判断“设备扫描是否还活着”。
            _heartbeat++;
        }
    }

    /// <summary>
    /// 把工艺状态编码成“保持寄存器镜像”。这是设备对外的寄存器表：
    /// HR0=液位×10，HR1-2=温度 float32（ABCD），HR3=压力×1000，
    /// HR4=设定值×10，HR5=心跳。
    /// </summary>
    public ushort[] ReadHoldingRegisters(int start, int count)
    {
        lock (_gate)
        {
            if (start < 0 || count < 0 || start + count > 6)
                throw new ArgumentOutOfRangeException(nameof(start), "模拟器保持寄存器范围为 0..5。");

            var all = new ushort[6];

            // ×10/×1000 是为了把小数变成整数：41.8% → 418，0.267MPa → 267。
            // 上位机再用 scale=0.1 / 0.001 还原成工程值。
            all[0] = (ushort)Math.Round(_level * 10);

            // float32 的 32 位拆成高 16 位/低 16 位，放到两个寄存器里。
            // 这里固定用 ABCD（高字在前），与 config/devices.json 的 ByteOrder 对应。
            var bits = BitConverter.SingleToInt32Bits((float)_temperature);
            all[1] = (ushort)(bits >> 16);
            all[2] = (ushort)(bits & 0xFFFF);

            all[3] = (ushort)Math.Round(_pressure * 1000);
            all[4] = (ushort)Math.Round(_setpoint * 10);
            all[5] = _heartbeat;

            // 只返回客户端请求的那一段，支持 FC03 的“起始地址 + 数量”。
            return all[start..(start + count)];
        }
    }

    public ushort[] ReadInputRegisters(int start, int count) => ReadHoldingRegisters(start, count);

    public bool[] ReadCoils(int start, int count)
    {
        lock (_gate)
        {
            if (start < 0 || count < 0 || start + count > 4)
                throw new ArgumentOutOfRangeException(nameof(start), "模拟器线圈范围为 0..3。");

            var all = new[] { _pump, _heater, _valve, _autoMode };
            return all[start..(start + count)];
        }
    }

    public bool[] ReadDiscreteInputs(int start, int count) => ReadCoils(start, count);

    /// <summary>FC05 写单线圈：C0 泵、C1 加热器、C2 出口阀、C3 自动模式。</summary>
    public void WriteCoil(int address, bool value)
    {
        lock (_gate)
        {
            switch (address)
            {
                case 0: _pump = value; break;
                case 1: _heater = value; break;
                case 2: _valve = value; break;
                case 3: _autoMode = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(address), "模拟器线圈范围为 0..3。");
            }
        }
    }

    /// <summary>FC06 写单寄存器：当前只允许写 HR4（温度设定值）。</summary>
    public void WriteRegister(int address, ushort value)
    {
        lock (_gate)
        {
            switch (address)
            {
                case 4:
                    // 750 → 75.0°C。设备侧仍然限幅，防止上位机写 200°C。
                    _setpoint = Clamp(value / 10.0, 0, 150);
                    break;
                case 0:
                case 1:
                case 2:
                case 3:
                case 5:
                    // 演示设备只接受设定值写入；其他保持寄存器只读。
                    // 真实 PLC 对只读寄存器通常返回异常码 02（非法地址）。
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(address), "模拟器保持寄存器范围为 0..5。");
            }
        }
    }

    private void ApplyFault()
    {
        switch (_fault)
        {
            case FaultKind.HighTemperature:
                _temperature = 100;
                _heater = false;
                break;
            case FaultKind.HighPressure:
                _pressure = 0.82;
                break;
            case FaultKind.LowLevel:
                _level = 2;
                _pump = false;
                break;
        }
    }

    /// <summary>故障到期后恢复注入前的状态，让演示可以反复做而不越跑越偏。</summary>
    private void ClearFault()
    {
        _fault = FaultKind.None;
        _level = _levelBeforeFault;
        _temperature = _tempBeforeFault;
        _pressure = _pressureBeforeFault;
        _pump = _pumpBeforeFault;
        _heater = _heaterBeforeFault;
    }

    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    private static double MoveTowards(double current, double target, double maxDelta)
        => Math.Abs(target - current) <= maxDelta ? target : current + Math.Sign(target - current) * maxDelta;
}
