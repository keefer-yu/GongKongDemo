using Scada.Core.Control;
using Scada.Core.Devices;
using Scada.Core.Tags;

namespace Scada.Control;

/// <summary>
/// SCADA 侧联锁：在写设备之前做最后一道软件检查。
/// 它不能替代设备侧保护，两者必须同时存在。
///
/// 设计原则：
/// - “危险方向”从严（启泵、开加热、进自动、改设定值）；
/// - “安全方向”从宽（停泵、关加热、退自动通常永远允许）；
/// - 每个检查都返回“规则 + 是否通过 + 原因”，UI 能直接告诉操作员为什么被拒。
/// </summary>
public sealed class InterlockEngine
{
    private readonly IReadOnlyDictionary<string, TagDefinition> _tags;
    private readonly LimitSettings _limits;

    public InterlockEngine(IEnumerable<TagDefinition> tags, LimitSettings limits)
    {
        _tags = tags.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        _limits = limits;
    }

    /// <summary>
    /// 评估一次命令。返回的列表里只要有一个 Passed=false，CommandService 就会拒绝执行。
    /// </summary>
    public IReadOnlyList<InterlockCheck> Evaluate(
        CommandRequest request, IReadOnlyDictionary<string, TagValue> snapshot)
    {
        if (!_tags.TryGetValue(request.TagId, out var tag))
            return [InterlockCheck.Fail("unknown-tag", "点位存在", $"未知点位：{request.TagId}")];

        var checks = new List<InterlockCheck>();

        // 本项目约定：值 0=OFF/关，非 0=ON/开。
        var on = request.Value != 0;

        switch (tag.Id.ToLowerInvariant())
        {
            case "pump":
                checks.Add(InterlockCheck.Ok("pump-command", "泵命令合法"));
                if (on)
                {
                    // 启泵是危险方向：液位太低会干转，所以必须卡住。
                    // 停泵（on=false）不进入这个 if，永远允许。
                    var level = Value(snapshot, "level");
                    checks.Add(level >= _limits.PumpMinLevel
                        ? InterlockCheck.Ok("pump-min-level",
                            $"液位 ≥ {_limits.PumpMinLevel:F0}%（当前 {level:F1}%）")
                        : InterlockCheck.Fail("pump-min-level",
                            $"液位 ≥ {_limits.PumpMinLevel:F0}%",
                            $"当前液位 {level:F1}%，启泵会干转"));
                }
                break;

            case "heater":
                checks.Add(InterlockCheck.Ok("heater-command", "加热器命令合法"));
                if (on)
                {
                    // 开加热器要同时满足：液位够（不干烧）、温度没超、且不是自动模式。
                    // 自动模式由设备侧按设定值控制，手动开加热会打架。
                    var level = Value(snapshot, "level");
                    var temp = Value(snapshot, "temp");
                    var auto = Value(snapshot, "autoMode") != 0;
                    checks.Add(level >= _limits.HeaterMinLevel
                        ? InterlockCheck.Ok("heater-min-level",
                            $"液位 ≥ {_limits.HeaterMinLevel:F0}%（当前 {level:F1}%）")
                        : InterlockCheck.Fail("heater-min-level",
                            $"液位 ≥ {_limits.HeaterMinLevel:F0}%",
                            $"当前液位 {level:F1}%，加热器可能干烧"));
                    checks.Add(temp < _limits.HeaterMaxTemp
                        ? InterlockCheck.Ok("heater-max-temp",
                            $"温度 < {_limits.HeaterMaxTemp:F0}°C（当前 {temp:F1}°C）")
                        : InterlockCheck.Fail("heater-max-temp",
                            $"温度 < {_limits.HeaterMaxTemp:F0}°C",
                            $"当前温度 {temp:F1}°C，已超上限"));
                    checks.Add(!auto
                        ? InterlockCheck.Ok("heater-manual-mode", "手动模式允许操作加热器")
                        : InterlockCheck.Fail("heater-manual-mode", "自动模式禁止手动加热",
                            "请先退出自动模式，或直接修改温度设定值"));
                }
                break;

            case "automode":
                checks.Add(InterlockCheck.Ok("auto-command", "自动模式命令合法"));
                if (on)
                {
                    // 进自动模式前检查设定值在工艺窗口内，且液位足够。
                    var setpoint = Value(snapshot, "tempSetpoint");
                    var level = Value(snapshot, "level");
                    checks.Add(setpoint >= _limits.SetpointMin && setpoint <= _limits.SetpointMax
                        ? InterlockCheck.Ok("auto-setpoint-range",
                            $"设定值 {_limits.SetpointMin:F0}..{_limits.SetpointMax:F0}°C（当前 {setpoint:F1}°C）")
                        : InterlockCheck.Fail("auto-setpoint-range",
                            $"设定值 {_limits.SetpointMin:F0}..{_limits.SetpointMax:F0}°C",
                            $"当前设定值 {setpoint:F1}°C 超出范围"));
                    checks.Add(level >= _limits.HeaterMinLevel
                        ? InterlockCheck.Ok("auto-min-level",
                            $"液位 ≥ {_limits.HeaterMinLevel:F0}%（当前 {level:F1}%）")
                        : InterlockCheck.Fail("auto-min-level",
                            $"液位 ≥ {_limits.HeaterMinLevel:F0}%",
                            $"当前液位 {level:F1}%，自动加热不安全"));
                }
                break;

            case "tempsetpoint":
                // 设定值本身是“请求值”，不是当前快照值，所以直接看 request.Value。
                checks.Add(request.Value >= _limits.SetpointMin && request.Value <= _limits.SetpointMax
                    ? InterlockCheck.Ok("setpoint-range",
                        $"设定值 {_limits.SetpointMin:F0}..{_limits.SetpointMax:F0}°C")
                    : InterlockCheck.Fail("setpoint-range",
                        $"设定值 {_limits.SetpointMin:F0}..{_limits.SetpointMax:F0}°C",
                        $"请求值 {request.Value:F1}°C 超出范围"));
                break;

            case "valve":
                checks.Add(InterlockCheck.Ok("valve-command", "出口阀命令合法"));
                break;

            default:
                checks.Add(InterlockCheck.Fail("unknown-rule", "已配置联锁规则",
                    $"点位 {tag.Id} 没有联锁规则"));
                break;
        }

        return checks;
    }

    /// <summary>
    /// 给 V4 的联锁矩阵用：一次性看所有“危险方向”命令当前是否允许。
    /// 这里只是展示，真正执行命令时还会再 Evaluate 一次，避免“看了矩阵之后工况又变了”。
    /// </summary>
    public IReadOnlyList<InterlockCheck> EvaluateAll(IReadOnlyDictionary<string, TagValue> snapshot)
    {
        var checks = new List<InterlockCheck>();

        // pump=1、heater=1、autoMode=1 是危险方向；过滤掉“命令合法”这种占位检查。
        foreach (var tagId in new[] { "pump", "heater", "autoMode" })
        {
            var request = new CommandRequest { TagId = tagId, Value = 1, Confirmed = true };
            checks.AddRange(Evaluate(request, snapshot).Where(c => c.RuleId != $"{tagId.ToLowerInvariant()}-command"));
        }

        // 设定值用“当前设定值”去评估范围是否仍然合法。
        var setpoint = Value(snapshot, "tempSetpoint");
        checks.AddRange(Evaluate(new CommandRequest { TagId = "tempSetpoint", Value = setpoint, Confirmed = true }, snapshot));
        return checks;
    }

    /// <summary>
    /// 读快照值；点位缺失或质量 Bad 时按 0 处理。
    /// 这是“保守失败”：例如液位 Bad=0，会拒绝启泵/加热，而不是误以为安全。
    /// </summary>
    private static double Value(IReadOnlyDictionary<string, TagValue> snapshot, string tagId)
        => snapshot.TryGetValue(tagId, out var value) && value.IsGood ? value.Value : 0;
}
