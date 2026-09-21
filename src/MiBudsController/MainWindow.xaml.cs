using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace MiBudsController;

/// <summary>
/// 主应用窗口：扩展内容到标题栏，使用 Mica 背景，
/// 提供导航视图并负责应用级调试 UI 与托盘联动。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1200;
    private const int DefaultHeight = 900;

    // DWMWA_SYSTEMBACKDROP_TYPE = 38; DWMSBT_MAINWINDOW = 2 (Mica)
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// 初始化窗口：设置标题栏、窗口尺寸与背景，
    /// 默认导航到设备主页，同时登记托盘和窗口关闭逻辑。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ApplyWindowBounds();
        ApplyBackdrop();
        // 未打包场景下 SystemBackdrop 有时在 Show 后才生效，激活时再补一次 DWM。
        // 主界面前台时轮询电量；失焦时停轮询。
        Activated += (_, args) =>
        {
            TryApplyMicaDwm();
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                Main.StopBatteryPolling();
            }
            else
            {
                Main.StartBatteryPolling();
            }
        };
        NavView.SelectedItem = DeviceItem;
        RootFrame.Navigate(typeof(Views.HomePage));

        AppState.RegisterMainWindow(this);
        AppState.InitializeTray(DispatcherQueue);
        ApplyDebugModeUi(AppState.SettingsVm.DebugMode);

        Main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModels.MainViewModel.IsConnected)
                or nameof(ViewModels.MainViewModel.DeviceName))
            {
                AppState.UpdateTrayTooltip();
            }
        };

        AppWindow.Closing += OnAppWindowClosing;
    }

    /// <summary>主视图模型（从全局状态取出）。</summary>
    private ViewModels.MainViewModel Main => AppState.Main;

    /// <summary>调试模式下显示日志导航项；关闭时隐藏并切回设备页。</summary>
    public void ApplyDebugModeUi(bool debugMode)
    {
        try
        {
            LogItem.Visibility = debugMode ? Visibility.Visible : Visibility.Collapsed;
            if (!debugMode && ReferenceEquals(NavView.SelectedItem, LogItem))
            {
                NavigateTo("Device");
            }
        }
        catch
        {
            // Window may not be fully initialized yet.
        }
    }

    /// <summary>按默认尺寸调整主窗口；部分环境不允许早期 Resize 时保持系统默认。</summary>
    private void ApplyWindowBounds()
    {
        try
        {
            AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
        }
        catch
        {
            // Some hosts reject early Resize; default OS size still works.
        }
    }

    /// <summary>
    /// 窗口关闭前检查：非主动退出且设置了“最小化到托盘”时，
    /// 取消关闭并隐藏窗口，让进程继续留在托盘。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (AppState.ExitRequested)
        {
            return;
        }

        if (AppState.SettingsVm.MinimizeToTray && AppState.Tray is not null)
        {
            args.Cancel = true;
            Main.StopBatteryPolling();
            AppWindow.Hide();
        }
    }

    /// <summary>显示窗口并激活到前台；打开时校验连接并启动电量轮询。</summary>
    public void ShowAndActivate()
    {
        AppWindow.Show();
        Activate();
        Main.StartBatteryPolling();
        _ = Main.VerifyConnectionAsync();
    }

    /// <summary>按页面标签切换导航选中项并导航到对应页面。</summary>
    public void NavigateTo(string tag)
    {
        // “More” 为历史遗留标签，与设置页合并处理。
        NavigationViewItem? item = tag switch
        {
            "Spatial" => SpatialItem,
            "Sound" => SoundItem,
            "Settings" or "More" => SettingsItem,
            "Log" => LogItem,
            _ => DeviceItem,
        };
        NavView.SelectedItem = item;
        Type page = tag switch
        {
            "Spatial" => typeof(Views.SpatialPage),
            "Sound" => typeof(Views.SoundEffectsPage),
            "Settings" or "More" => typeof(Views.SettingsPage),
            "Log" => typeof(Views.LogPage),
            _ => typeof(Views.HomePage),
        };
        RootFrame.Navigate(page);
    }

    /// <summary>
    /// 应用窗口背景：Win11 优先 Mica（SystemBackdrop + DWM 双保险）。
    /// 内容层必须透明，否则导航/页面默认底色会盖住云母。
    /// </summary>
    private void ApplyBackdrop()
    {
        bool micaSupported = false;
        try
        {
            micaSupported = MicaController.IsSupported();
        }
        catch
        {
            micaSupported = false;
        }

        // 即使 IsSupported()==false，Win11 上仍尝试 DWM Mica（未打包更常见）。
        if (micaSupported)
        {
            try
            {
                SystemBackdrop = new MicaBackdrop();
            }
            catch
            {
                SystemBackdrop = null;
            }
        }
        else
        {
            SystemBackdrop = null;
        }

        bool applied = TryApplyMicaDwm() || micaSupported;
        if (applied)
        {
            MakeChromeTransparentForMica();
            return;
        }

        // Win10 / 关闭透明效果：回退实色。
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorDefaultBrush", out object brush)
            && brush is Brush solid)
        {
            RootGrid.Background = solid;
        }
        else
        {
            RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
    }

    /// <summary>用 DWM 将窗口 backdrop 设为 Mica；成功返回 true。</summary>
    private bool TryApplyMicaDwm()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            int type = DwmsbtMainWindow;
            int hr = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    private void MakeChromeTransparentForMica()
    {
        RootGrid.Background = null;
        if (AppTitleBar is not null)
        {
            AppTitleBar.Background = null;
        }

        if (NavView is not null)
        {
            NavView.Background = null;
        }

        if (RootFrame is not null)
        {
            RootFrame.Background = null;
        }
    }

    private void OnWindowActivatedApplyBackdrop(object sender, WindowActivatedEventArgs args)
    {
        if (TryApplyMicaDwm())
        {
            MakeChromeTransparentForMica();
            return;
        }

        try
        {
            if (SystemBackdrop is null && MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
                MakeChromeTransparentForMica();
            }
        }
        catch
        {
            // Ignore.
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        NavigateTo(tag);
    }
}
