using System.Windows;
using Scada.Core.Diagnostics;

namespace Scada.Wpf.V3.AlarmHistory;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = CommandLineOptions.Parse(e.Args);
        var window = new MainWindow(options);
        MainWindow = window;
        window.Show();
    }
}
