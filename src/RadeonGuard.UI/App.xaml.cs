using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RadeonGuard.Diagnostics;

namespace RadeonGuard.UI;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWaitHandle;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\RadeonGuard.UI.Singleton", out bool createdNew);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RadeonGuard.UI.ActivateEvent");
        if (!createdNew)
        {
            try { _activateEvent.Set(); } catch { }
            _activateEvent.Dispose();
            _singleInstanceMutex.Dispose();
            Shutdown(0);
            return;
        }

        _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.RestoreFromTray()),
            null,
            -1,
            false);

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

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWaitHandle?.Unregister(null);
        _activateEvent?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
