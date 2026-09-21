using Microsoft.UI.Xaml;

namespace MiBudsController;

/// <summary>
/// 应用入口：创建主窗口，启动时自动扫描已连接的耳机，
/// 并统一登记 UI 线程与进程级的未处理异常以便写入崩溃日志。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// 初始化 XAML 资源，并挂接两处未处理异常回调：
    /// 界面线程异常和 AppDomain 级异常都写入本地崩溃日志。
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
    }

    /// <summary>
    /// 应用启动：创建主窗口并异步初始化耳机扫描。
    /// 若设置了“最小化启动”，主窗口先隐藏，进程与托盘图标继续存活。
    /// </summary>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            // Auto-scan already-connected Bluetooth earbuds at launch (no Connect button).
            _ = AppState.Main.InitializeAsync();
            bool minimize = AppState.ShouldStartMinimized();
            if (minimize && AppState.Tray is not null)
            {
                // Keep process + tray alive; main window stays hidden until opened.
                AppState.MainAppWindow?.AppWindow.Hide();
            }
            else
            {
                _window.Activate();
            }
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            throw;
        }
    }

    /// <summary>
    /// 将异常内容追加写入本机 LocalApplicationData 下的 crash.log。
    /// 日志本身失败时不再抛出，避免掩盖原始故障。
    /// </summary>
    private static void WriteCrash(Exception? exception)
    {
        try
        {
            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiBudsController");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(folder, "crash.log"),
                $"[{DateTime.Now:O}]{exception}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never mask the original failure.
        }
    }
}
