using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MiBudsController.Core.Services;
using MiBudsController.Services;
using MiBudsController.ViewModels;
using MiBudsController.Views;

namespace MiBudsController;

/// <summary>
/// 应用级全局状态：集中持有日志、蓝牙客户端、设置和各页面视图模型，
/// 并负责主窗口、迷你面板、托盘图标的创建与切换。
/// </summary>
public static class AppState
{
    private static DispatcherQueue? _uiDispatcher;

    /// <summary>全局协议日志服务。</summary>
    public static LogService Logs { get; } = new();

    /// <summary>全局耳机蓝牙客户端，负责连接、鉴权与协议消息处理。</summary>
    public static EarbudsClient Client { get; } = new(Logs);

    /// <summary>本地 JSON 设置存储。</summary>
    public static AppSettings Settings { get; } = new();

    /// <summary>主页视图模型（设备列表、连接状态、电量信息）。</summary>
    public static MainViewModel Main { get; } = new(Client, Logs, Settings);

    /// <summary>降噪设置视图模型。</summary>
    public static NoiseViewModel Noise { get; } = new(Client);

    /// <summary>音效设置视图模型。</summary>
    public static SoundEffectsViewModel Sound { get; } = new(Client);

    /// <summary>空间音频设置视图模型。</summary>
    public static SpatialViewModel Spatial { get; } = new(Client);

    /// <summary>协议日志页面视图模型。</summary>
    public static LogViewModel Log { get; } = new(Logs);

    /// <summary>应用设置页面视图模型。</summary>
    public static SettingsViewModel SettingsVm { get; } = new(Settings);

    /// <summary>系统托盘图标服务，可能未初始化。</summary>
    public static TrayService? Tray { get; private set; }

    /// <summary>主应用窗口。</summary>
    public static MainWindow? MainAppWindow { get; private set; }

    /// <summary>迷你控制面板窗口，可因关闭而重建。</summary>
    public static MiniControlWindow? MiniWindow { get; private set; }

    /// <summary>是否为用户主动退出（退出时关闭窗口不再被托盘设置拦截）。</summary>
    public static bool ExitRequested { get; set; }

    /// <summary>登记主窗口并缓存其 UI 调度队列，供后台线程回传界面。</summary>
    public static void RegisterMainWindow(MainWindow window)
    {
        MainAppWindow = window;
        _uiDispatcher = window.DispatcherQueue;
    }

    /// <summary>设定 UI 调度队列（通常来自托盘或迷你窗口初始化）。</summary>
    public static void SetUiDispatcher(DispatcherQueue dispatcher) => _uiDispatcher = dispatcher;

    /// <summary>按优先级取当前可用的 UI 调度队列。</summary>
    private static DispatcherQueue? UiDispatcher =>
        _uiDispatcher ?? MiniWindow?.DispatcherQueue ?? MainAppWindow?.DispatcherQueue;

    /// <summary>
    /// 初始化系统托盘图标：读取设置开关，加载 AppIcon.ico，
    /// 并绑定“打开主界面 / 迷你面板 / 退出”三个事件，同一会话只初始化一次。
    /// </summary>
    public static void InitializeTray(DispatcherQueue dispatcher)
    {
        _uiDispatcher ??= dispatcher;
        if (Tray is not null || !SettingsVm.ShowTrayIcon)
        {
            return;
        }

        var tray = new TrayService(dispatcher);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (!tray.Initialize(iconPath))
        {
            tray.Dispose();
            return;
        }

        tray.OpenMainRequested += () => ShowMainWindow();
        tray.OpenMiniRequested += ToggleMiniPanel;
        tray.ExitRequested += ExitApplication;
        Tray = tray;
        UpdateTrayTooltip();
    }

    /// <summary>按设置开关动态显示或销毁托盘图标。</summary>
    public static void ApplyTrayVisibility(bool show)
    {
        if (!show)
        {
            Tray?.Dispose();
            Tray = null;
            return;
        }

        DispatcherQueue? dq = UiDispatcher;
        if (dq is not null)
        {
            InitializeTray(dq);
        }
    }

    /// <summary>
    /// 调试模式：显示日志导航项并开启内存协议捕获；
    /// 磁盘日志保持关闭，直到日志页手动开启。
    /// </summary>
    public static void ApplyDebugMode(bool enabled)
    {
        Logs.IsCaptureEnabled = enabled;
        if (!enabled)
        {
            // 关闭调试模式时同时停止磁盘写入。
            Logs.IsDiskLogEnabled = false;
        }

        Log.SyncCaptureState();
        MainAppWindow?.ApplyDebugModeUi(enabled);
    }

    /// <summary>根据连接状态刷新托盘悬浮提示文字。</summary>
    public static void UpdateTrayTooltip()
    {
        string status = Main.IsConnected && Main.DeviceName is not null
            ? $"{Main.DeviceName} · 已连接"
            : "小米耳机控制器 · 未连接";
        Tray?.UpdateTooltip(status);
    }

    /// <summary>
    /// 显示并激活主窗口，可选跳转到指定导航页；
    /// 内部通过 UI 调度队列保证在正确的 UI 线程执行。
    /// </summary>
    public static void ShowMainWindow(bool navigateToSettings = false, string? pageTag = null)
    {
        DispatcherQueue? dq = UiDispatcher;
        if (dq is null && MainAppWindow is null)
        {
            return;
        }

        (dq ?? MainAppWindow!.DispatcherQueue).TryEnqueue(() =>
        {
            if (MainAppWindow is null)
            {
                return;
            }

            MainAppWindow.ShowAndActivate();
            if (navigateToSettings)
            {
                MainAppWindow.NavigateTo("Settings");
            }
            else if (pageTag is not null)
            {
                MainAppWindow.NavigateTo(pageTag);
            }
        });
    }

    /// <summary>
    /// 显示迷你控制面板。主窗口在托盘隐藏时同样可用；
    /// 若窗口实例已被关闭则自动重建。保证同一时刻只复用一个实例。
    /// </summary>
    public static void ShowMiniPanel()
    {
        DispatcherQueue? dq = UiDispatcher;
        if (dq is null)
        {
            EnsureMiniVisible();
            return;
        }

        dq.TryEnqueue(EnsureMiniVisible);
    }

    /// <summary>
    /// 托盘/快捷入口切换迷你面板：已显示则关闭，否则打开。
    /// 二次点击不会弹出多个面板。
    /// </summary>
    public static void ToggleMiniPanel()
    {
        DispatcherQueue? dq = UiDispatcher;
        if (dq is null)
        {
            ToggleMiniPanelCore();
            return;
        }

        dq.TryEnqueue(ToggleMiniPanelCore);
    }

    private static void ToggleMiniPanelCore()
    {
        try
        {
            if (MiniWindow is not null && (MiniWindow.IsPanelVisible || MiniWindow.IsClosing))
            {
                MiniWindow.TogglePanel();
                return;
            }
        }
        catch
        {
            MiniWindow = null;
        }

        EnsureMiniVisible();
    }

    private static void EnsureMiniVisible()
    {
        try
        {
            if (MiniWindow is null)
            {
                MiniWindow = new MiniControlWindow();
            }

            MiniWindow.ShowAndActivate();
        }
        catch
        {
            // 旧实例可能已损坏：关掉后重建一次，避免叠多个面板。
            try
            {
                MiniWindow?.Close();
            }
            catch
            {
                // Ignore close failures on a dead window.
            }

            MiniWindow = null;
            try
            {
                MiniWindow = new MiniControlWindow();
                MiniWindow.ShowAndActivate();
            }
            catch
            {
                MiniWindow = null;
            }
        }
    }

    /// <summary>迷你窗口关闭后清空引用，下次重新创建。</summary>
    public static void NotifyMiniClosed() => MiniWindow = null;

    /// <summary>
    /// 退出应用：依次关闭迷你窗口、托盘和主窗口，
    /// 最终调用 Application.Exit 结束进程。
    /// </summary>
    public static void ExitApplication()
    {
        ExitRequested = true;
        try
        {
            MiniWindow?.Close();
        }
        catch
        {
            // 窗口可能已不存在，忽略。
        }

        MiniWindow = null;
        Tray?.Dispose();
        Tray = null;
        MainAppWindow?.Close();
        Application.Current.Exit();
    }

    /// <summary>
    /// 判断启动时是否最小化到托盘：
    /// 命令行带 --minimized，或设置同时开启“开机自启 + 最小化启动”。
    /// </summary>
    public static bool ShouldStartMinimized()
    {
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return SettingsVm.StartMinimized && SettingsVm.AutoStart;
    }
}
