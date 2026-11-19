using System.Windows;
using System.Windows.Threading;
using RadeonGuard.Diagnostics;

namespace RadeonGuard.UI;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var log = FlightRecorder.CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            log.Fatal(args.Exception, "Unhandled UI dispatcher exception.");
            System.Windows.MessageBox.Show(
                $"RadeonGuard encountered an unexpected error:\n\n{args.Exception.Message}\n\nCheck logs in %LOCALAPPDATA%\\RadeonGuard\\logs",
                "RadeonGuard Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                log.Fatal(ex, "Unhandled AppDomain exception.");
            }
        };

        base.OnStartup(e);
    }
}
