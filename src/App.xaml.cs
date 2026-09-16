using System.Windows;

namespace DeltaPlayer;

public partial class App : Application
{
    private Mutex? singleton;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args.Length >= 2 && e.Args[0] == "--smoke";
        singleton = new Mutex(true, smoke ? "Local\\DeltaPlayer.Smoke" : "Local\\DeltaPlayer.Desktop", out bool created);
        if (!created) { MessageBox.Show("拾音已经在运行，请使用已打开的窗口。", "拾音"); Shutdown(); return; }
        var window = new MainWindow(smoke ? e.Args[1] : null);
        MainWindow = window; window.ShowActivated = !smoke; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { singleton?.Dispose(); base.OnExit(e); }
}
