using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using RadeonGuard.UI.ViewModels;
using Wpf.Ui.Controls;

namespace RadeonGuard.UI;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly NotifyIcon _trayIcon;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _trayIcon = new NotifyIcon
        {
            Text = "RadeonGuard — AMD GPU & Fan Manager",
            Icon = SystemIcons.Shield,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        Loaded += async (_, _) =>
        {
            FanCurveEditor.CurveEdited += (_, _) => _vm.ValidateCurrentFanDraft();
            await _vm.InitializeAsync().ConfigureAwait(true);
        };
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open RadeonGuard", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit RadeonGuard UI", null, (_, _) =>
        {
            _isExplicitExit = true;
            Close();
        });
        return menu;
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _vm.Dispose();
        base.OnClosing(e);
    }
}
