using Scada.Core.Diagnostics;

namespace Scada.WinForms.V3.AlarmHistory;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var options = CommandLineOptions.Parse(args);
        using var form = new MainForm(options);
        Application.Run(form);
    }
}
