namespace Scada.Core.Diagnostics;

/// <summary>所有 Demo 共用的命令行参数，保证 10 个程序行为一致。</summary>
public sealed class CommandLineOptions
{
    public bool Demo { get; init; }
    public bool AutoStart { get; init; } = true;
    public int? SmokeSeconds { get; init; }
    public string? DeviceConfigPath { get; init; }
    public string? NorthboundConfigPath { get; init; }

    public bool IsSmoke => SmokeSeconds is not null;

    public static CommandLineOptions Parse(string[] args)
    {
        var demo = false;
        var autoStart = true;
        int? smoke = null;
        string? config = null;
        string? northbound = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--demo":
                    demo = true;
                    break;
                case "--no-autostart":
                    autoStart = false;
                    break;
                case "--smoke":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var seconds))
                        smoke = Math.Clamp(seconds, 1, 300);
                    else
                        smoke = 8;
                    break;
                case "--config":
                    if (i + 1 < args.Length) config = args[++i];
                    break;
                case "--northbound":
                    if (i + 1 < args.Length) northbound = args[++i];
                    break;
            }
        }

        return new CommandLineOptions
        {
            Demo = demo,
            AutoStart = autoStart && !demo,
            SmokeSeconds = smoke,
            DeviceConfigPath = config,
            NorthboundConfigPath = northbound,
        };
    }
}
