using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MiBudsController.ViewModels;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;

namespace MiBudsController.Views;

/// <summary>
/// 无边框的托盘迷你面板窗口。
/// 打开：底部上移 + 淡入；隐藏：相对开启动画反向的下移 + 淡出后再 Hide。
/// 托盘二次点击通过 Toggle 关闭；同一时刻只允许一个面板实例可见。
/// </summary>
public sealed partial class MiniControlWindow : Window
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    private const int PanelWidth = 340;
    private const int PanelHeight = 560;
    private const int LeaveHideDelayMs = 500;
    private const int IdleHideDelayMs = 5000;
    private const float OpenRiseDip = 36f;
    private const int OpenDurationMs = 280;
    private const int CloseDurationMs = 280;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly DispatcherTimer _leaveHideTimer;
    private readonly DispatcherTimer _idleHideTimer;
    private DispatcherTimer? _closeEndTimer;
    private Storyboard? _fadeSb;
    private bool _pointerHasBeenInside;
    private bool _isHiding;
    private int _animVersion;

    /// <summary>
    /// 初始化窗口：设置无边框样式、背景、动画定时器和指针事件处理，
    /// 并在多个视图模型变化时刷新迷你面板绑定。
    /// </summary>
    public MiniControlWindow()
    {
        InitializeComponent();
        Title = "耳机迷你面板";
        try
        {
            AppWindow.SetIcon("Assets/AppIcon.ico");
        }
        catch
        {
            // 图标加载失败不影响面板使用。
        }

        ApplyBackdrop();
        ApplyBorderlessChrome();
        ApplyRoundedWindowCorners();
        ExtendContentIntoTitleBar();

        // Keep flyout dark like the main app, even if OS is in light theme.
        if (Content is FrameworkElement contentFe)
        {
            contentFe.RequestedTheme = ElementTheme.Dark;
        }

        if (PanelBody is FrameworkElement bodyFe)
        {
            bodyFe.RequestedTheme = ElementTheme.Dark;
        }

        RootGrid.Opacity = 0;
        EnableTranslationOnRoot();

        _leaveHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LeaveHideDelayMs) };
        _leaveHideTimer.Tick += LeaveHideTimer_Tick;

        _idleHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleHideDelayMs) };
        _idleHideTimer.Tick += IdleHideTimer_Tick;

        RootGrid.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnAnyPointerEntered), true);
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnAnyPointerActivity), true);
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnAnyPointerActivity), true);
        RootGrid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnAnyPointerActivity), true);
        RootGrid.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnAnyPointerExited), true);

        Main.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            Bindings.Update();
        });
        Noise.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(() => Bindings.Update());
        Sound.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(() => Bindings.Update());
        Closed += (_, _) =>
        {
            IsPanelVisible = false;
            AppState.NotifyMiniClosed();
        };
    }

    /// <summary>主视图模型。</summary>
    public MainViewModel Main { get; } = AppState.Main;

    /// <summary>降噪快捷设置视图模型。</summary>
    public NoiseViewModel Noise { get; } = AppState.Noise;

    /// <summary>音效视图模型。</summary>
    public SoundEffectsViewModel Sound { get; } = AppState.Sound;

    /// <summary>面板当前是否处于“已显示”状态（关闭动画结束后为 false）。</summary>
    public bool IsPanelVisible { get; private set; }

    /// <summary>是否正在播放关闭动画。</summary>
    public bool IsClosing => _isHiding;

    /// <summary>
    /// 显示并激活迷你面板：定位到任务栏/鼠标附近，播放打开动画，
    /// 同时启动空闲隐藏计时器。
    /// </summary>
    public void ShowAndActivate()
    {
        _isHiding = false;
        IsPanelVisible = true;
        _pointerHasBeenInside = false;
        _leaveHideTimer.Stop();
        _idleHideTimer.Stop();
        _animVersion++;

        PositionNearTaskbarOrCursor();

        try
        {
            AppWindow.Show();
        }
        catch
        {
            // Window may already be visible.
        }

        Activate();
        BringToForeground();
        Bindings.Update();

        // 打开迷你面板时校验耳机是否仍连接，失效则恢复未连接状态。
        _ = Main.VerifyConnectionAsync();

        PlayOpenAnimation();
        _idleHideTimer.Start();
    }

    /// <summary>
    /// 打开动画：Composition Translation.Y 上移 + XAML Opacity 淡入。
    /// 注意：不要用 Visual.Offset（会被布局冲掉），也不要在动画结束前写死终值。
    /// </summary>
    private void PlayOpenAnimation()
    {
        _isHiding = false;
        IsPanelVisible = true;
        _closeEndTimer?.Stop();
        _fadeSb?.Stop();
        _fadeSb = null;

        RootGrid.Opacity = 0;
        if (PanelCard is not null)
        {
            PanelCard.Opacity = 1;
        }

        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        StopCompositionAnims(visual);

        var compositor = visual.Compositor;
        var easeOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));

        var rise = compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0f, OpenRiseDip, easeOut);
        rise.InsertKeyFrame(1f, 0f, easeOut);
        rise.Duration = TimeSpan.FromMilliseconds(OpenDurationMs);
        // Translation.Y 为 Composition 原生通道，WinUI 3 下可对抗布局。
        visual.StartAnimation("Translation.Y", rise);

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(OpenDurationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, RootGrid);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        int version = _animVersion;
        sb.Completed += (_, _) =>
        {
            if (version != _animVersion)
            {
                return;
            }

            RootGrid.Opacity = 1;
        };
        _fadeSb = sb;
        sb.Begin();

        _idleHideTimer.Start();
    }

    /// <summary>
    /// 关闭动画：与打开相反——Translation.Y 下移 + Opacity 淡出，结束后 Hide。
    /// 动画播放期间不写 Opacity=0，避免闪现；定时器兜底保证一定会收起。
    /// </summary>
    private void PlayCloseAnimation()
    {
        if (_isHiding)
        {
            return;
        }

        _isHiding = true;
        _leaveHideTimer.Stop();
        _idleHideTimer.Stop();
        _fadeSb?.Stop();
        _fadeSb = null;
        _closeEndTimer?.Stop();

        int version = ++_animVersion;

        double fromOpacity = RootGrid.Opacity;
        if (fromOpacity <= 0.01)
        {
            fromOpacity = 1;
        }

        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        StopCompositionAnims(visual);

        var compositor = visual.Compositor;
        var easeIn = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.7f, 0f),
            new Vector2(0.84f, 0f));

        var sink = compositor.CreateScalarKeyFrameAnimation();
        sink.InsertKeyFrame(0f, 0f, easeIn);
        sink.InsertKeyFrame(1f, OpenRiseDip, easeIn);
        sink.Duration = TimeSpan.FromMilliseconds(CloseDurationMs);
        visual.StartAnimation("Translation.Y", sink);

        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(CloseDurationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(fade, RootGrid);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) => FinishHide(version);
        _fadeSb = sb;
        sb.Begin();

        _closeEndTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(CloseDurationMs + 60),
        };
        _closeEndTimer.Tick += (_, _) =>
        {
            _closeEndTimer?.Stop();
            FinishHide(version);
        };
        _closeEndTimer.Start();
    }

    /// <summary>关闭流程收尾：隐藏窗口并复位状态（可重入，版本号防误杀重开）。</summary>
    private void FinishHide(int version)
    {
        if (version != _animVersion)
        {
            return;
        }

        _closeEndTimer?.Stop();

        try
        {
            AppWindow.Hide();
        }
        catch
        {
            // Already hidden.
        }

        var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
        StopCompositionAnims(visual);
        RootGrid.Opacity = 0;
        if (PanelCard is not null)
        {
            PanelCard.Opacity = 0;
        }

        _isHiding = false;
        IsPanelVisible = false;
        _pointerHasBeenInside = false;
    }

    /// <summary>启用 XAML 元素的 Composition Translation 通道（若 SDK 提供）。</summary>
    private void EnableTranslationOnRoot()
    {
        try
        {
            var mi = typeof(ElementCompositionPreview).GetMethod(
                "SetIsTranslationEnabled",
                new[] { typeof(UIElement), typeof(bool) });
            mi?.Invoke(null, new object[] { RootGrid, true });
        }
        catch
        {
            // Optional API.
        }
    }

    private static void StopCompositionAnims(Visual visual)
    {
        try
        {
            visual.StopAnimation("Translation.Y");
            visual.StopAnimation("Offset.Y");
            visual.StopAnimation("Opacity");
        }
        catch
        {
            // Visual may not be ready yet.
        }
    }

    /// <summary>
    /// 托盘/用户切换：已显示则下移淡出；关闭动画中则取消并重新打开。
    /// </summary>
    public void TogglePanel()
    {
        if (_isHiding)
        {
            ShowAndActivate();
            return;
        }

        if (IsPanelVisible)
        {
            PlayCloseAnimation();
            return;
        }

        ShowAndActivate();
    }

    /// <summary>把面板定位到任务栏上方或鼠标附近，并限制在工作区范围内。</summary>
    private void PositionNearTaskbarOrCursor()
    {
        try
        {
            GetCursorPos(out POINT cursor);
            int x;
            int y;

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT trayRect))
            {
                y = trayRect.Top - PanelHeight - 8;
                x = cursor.X - PanelWidth + 48;
                x = Math.Clamp(x, trayRect.Left + 8, Math.Max(trayRect.Left + 8, trayRect.Right - PanelWidth - 8));
            }
            else
            {
                x = cursor.X - PanelWidth + 28;
                y = cursor.Y - PanelHeight - 12;
            }

            var screen = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
            RectInt32 work = screen.WorkArea;
            x = Math.Clamp(x, work.X + 4, work.X + work.Width - PanelWidth - 4);
            y = Math.Clamp(y, work.Y + 4, work.Y + work.Height - PanelHeight - 4);

            AppWindow.Move(new PointInt32(x, y));
            AppWindow.Resize(new SizeInt32(PanelWidth, PanelHeight));
        }
        catch
        {
            AppWindow.Resize(new SizeInt32(PanelWidth, PanelHeight));
        }
    }

    /// <summary>尽力将窗口置前；失败时不影响面板显示。</summary>
    private void BringToForeground()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// 应用背景：优先使用系统 Mica，根容器保持透明，
    /// 由圆角卡片提供主题背景和描边。
    /// </summary>
    private void ApplyBackdrop()
    {
        SystemBackdrop = null;
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }

        // 根容器透明以露出背景；圆角卡片才是实际表面。
        RootGrid.Background = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

        if (PanelCard is null)
        {
            return;
        }

        // 深色近实心卡片，保证文字可读；浅色 Mica 只透过边缘。
        PanelCard.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x24));
        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object strokeObj)
            && strokeObj is Brush stroke)
        {
            PanelCard.BorderBrush = stroke;
        }
        else
        {
            PanelCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>通过 DWM 设置窗口圆角偏好（Win11 支持时生效）。</summary>
    private void ApplyRoundedWindowCorners()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            int pref = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
        }
        catch
        {
            // 旧版 Windows 不支持时忽略，卡片的 CornerRadius 仍有效。
        }
    }

    /// <summary>内容延伸进标题栏，去掉默认标题栏占位（上边框过宽/底部被裁的常见原因）。</summary>
    private void ExtendContentIntoTitleBar()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            var transparent = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
            titleBar.ButtonBackgroundColor = transparent;
            titleBar.ButtonInactiveBackgroundColor = transparent;
            titleBar.ButtonHoverBackgroundColor = transparent;
            titleBar.ButtonPressedBackgroundColor = transparent;
            try
            {
                titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            }
            catch
            {
                // Older App SDK may not expose PreferredHeightOption.
            }
        }
        catch
        {
            // Customization unsupported — borderless style still applies.
        }
    }

    /// <summary>去掉标题栏与可调整边框，并设为工具窗口，避免出现在任务栏。</summary>
    private void ApplyBorderlessChrome()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            int style = GetWindowLong(hwnd, GwlStyle);
            style &= ~WsCaption;
            style &= ~WsThickFrame;
            SetWindowLong(hwnd, GwlStyle, style);

            int ex = GetWindowLong(hwnd, GwlExStyle);
            ex |= WsExToolWindow;
            SetWindowLong(hwnd, GwlExStyle, ex);

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoZOrder | SwpNoSize | SwpFrameChanged);
        }
        catch
        {
            // 尽力而为，失败也不影响面板主体。
        }
    }

    /// <summary>指针进入面板时记录活动。</summary>
    private void OnAnyPointerEntered(object sender, PointerRoutedEventArgs e) => MarkActivity(entered: true);

    /// <summary>指针在面板内移动/点击时刷新活动状态。</summary>
    private void OnAnyPointerActivity(object sender, PointerRoutedEventArgs e) => MarkActivity(entered: true);

    /// <summary>指针离开面板时启动延迟隐藏计时。</summary>
    private void OnAnyPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerHasBeenInside || _isHiding)
        {
            return;
        }

        _leaveHideTimer.Stop();
        _leaveHideTimer.Start();
    }

    /// <summary>统一记录面板内活动并重置隐藏计时器。</summary>
    private void MarkActivity(bool entered)
    {
        if (_isHiding)
        {
            return;
        }

        if (entered)
        {
            _pointerHasBeenInside = true;
        }

        _leaveHideTimer.Stop();
        RestartIdleTimer();
    }

    /// <summary>重新启动空闲隐藏计时器。</summary>
    private void RestartIdleTimer()
    {
        if (_isHiding)
        {
            return;
        }

        _idleHideTimer.Stop();
        _idleHideTimer.Start();
    }

    /// <summary>指针离开后的延迟回调：光标仍在窗口上则继续等待，否则关闭面板。</summary>
    private void LeaveHideTimer_Tick(object? sender, object e)
    {
        _leaveHideTimer.Stop();
        if (IsCursorOverWindow())
        {
            RestartIdleTimer();
            return;
        }

        PlayCloseAnimation();
    }

    /// <summary>空闲隐藏回调：期间有活动或光标仍在窗口内则重置计时。</summary>
    private void IdleHideTimer_Tick(object? sender, object e)
    {
        _idleHideTimer.Stop();
        if (_isHiding)
        {
            return;
        }

        if (_pointerHasBeenInside && IsCursorOverWindow())
        {
            RestartIdleTimer();
            return;
        }

        PlayCloseAnimation();
    }

    /// <summary>用 Win32 光标位置与窗口矩形判断光标是否悬停在面板上。</summary>
    private bool IsCursorOverWindow()
    {
        try
        {
            if (!GetCursorPos(out POINT pt))
            {
                return false;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect))
            {
                return false;
            }

            const int pad = 2;
            return pt.X >= rect.Left - pad
                && pt.X <= rect.Right + pad
                && pt.Y >= rect.Top - pad
                && pt.Y <= rect.Bottom + pad;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>外部（托盘或用户动作）请求隐藏面板时调用。</summary>
    public void HideFromTrayOrUser()
    {
        PlayCloseAnimation();
    }

    /// <summary>点击“打开主界面”：先关闭迷你面板，再显示主窗口。</summary>
    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        PlayCloseAnimation();
        AppState.ShowMainWindow();
    }

    /// <summary>点击“断开”：记录一次活动，避免面板在点击时立刻隐藏。</summary>
    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        MarkActivity(true);
        if (Main.DisconnectCommand.CanExecute(null))
        {
            Main.DisconnectCommand.Execute(null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
